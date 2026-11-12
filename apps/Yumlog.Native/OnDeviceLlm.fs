namespace Yumlog.Native

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open Microsoft.ML.OnnxRuntimeGenAI

/// In-process reasoning over mdkg runbooks using the same on-device model that
/// Microsoft Edge downloads for its built-in Prompt API.
///
/// Why this exists: the browser path (yumlog-manager.html) needs Edge Canary with
/// experimental flags and a WebGPU adapter. The model itself is a plain ONNX Runtime
/// GenAI folder, so the identical weights run in-process on the CPU with no browser,
/// no flags, and no GPU - which is what makes runbook reasoning usable from the CLI
/// and from CI. Edge pins "provider_options": [{ "webgpu": ... }] in genai_config.json;
/// Config.ClearProviders() drops that back to the portable CPU execution provider
/// WITHOUT modifying Edge's files on disk.
module OnDeviceLlm =

    [<Literal>]
    let private ModelFolderName = "EdgeLLMOnDeviceModel"

    [<Literal>]
    let private ModelDirVariable = "YUMLOG_LLM_MODEL_DIR"

    /// Telemetry lines appended to the model per prefill. Chosen because a batch of this
    /// size cuts ingest time by roughly an order of magnitude while still keeping the
    /// hot-swap check fine grained enough to never overshoot the context window.
    [<Literal>]
    let private BatchLines = 16

    /// Emitted so a caller can tell which weights produced an answer.
    [<CLIMutable>]
    type LlmModelInfo =
        { Directory: string
          Version: string
          ModelType: string
          ContextLength: int
          ConfiguredProvider: string
          WeightMegabytes: float }

    [<CLIMutable>]
    type LlmSwap =
        { Index: int
          AtSegment: int
          TokensBefore: int
          TokensAfter: int
          ElapsedMs: int64 }

    [<CLIMutable>]
    type LlmReasonResult =
        { Model: LlmModelInfo
          Provider: string
          ModelLoadMs: int64
          Segments: int
          SegmentTokens: int
          Swaps: LlmSwap array
          SwapCount: int
          CarryOver: string
          ContextTokens: int
          ContextLimit: int
          HighWaterPct: float
          Question: string
          Answer: string
          AnswerTokens: int
          TokensPerSecond: float
          IngestMs: int64
          AnswerMs: int64
          TotalMs: int64 }

    type LlmOptions = ReasonConfig

    let defaultOptions: ReasonConfig =
        { Input = ""
          ModelDir = ""
          Provider = "cpu"
          Question = "Summarise what this recording shows. Be specific and terse."
          HighWaterPct = 0.6
          MaxNewTokens = 320
          MaxSegments = 0
          JsonOut = "" }

    // ------------------------------------------------------------------
    // Model discovery
    // ------------------------------------------------------------------

    let private localAppData () =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)

    /// Every place Edge is known to stage the on-device model, plus the isolated
    /// probe profile the browser harness uses. Ordered most- to least-preferred.
    let private searchRoots () =
        let local = localAppData ()

        [ (match Environment.GetEnvironmentVariable(ModelDirVariable) with
           | null -> ""
           | value -> value)
          Path.Combine(local, "Microsoft", "Edge SxS", "User Data")
          Path.Combine(local, "Microsoft", "Edge Dev", "User Data")
          Path.Combine(local, "Microsoft", "Edge Beta", "User Data")
          Path.Combine(local, "Microsoft", "Edge", "User Data")
          Path.Combine(Path.GetTempPath(), "yumlog-edge-probe") ]
        |> List.filter (fun path -> not (String.IsNullOrWhiteSpace(path)))

    let private isModelDir (dir: string) =
        not (String.IsNullOrWhiteSpace(dir))
        && File.Exists(Path.Combine(dir, "genai_config.json"))

    let private weightMegabytes (dir: string) =
        try
            DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
            |> Seq.sumBy (fun file -> float file.Length)
            |> fun total -> Math.Round(total / 1048576.0, 1)
        with _ ->
            0.0

    /// Reads context length, model family, and the provider Edge baked in. Failure to
    /// parse is not fatal: the model still loads, we just report unknowns.
    let private describe (dir: string) =
        let mutable contextLength = 0
        let mutable modelType = "unknown"
        let mutable provider = "default"

        try
            use stream = File.OpenRead(Path.Combine(dir, "genai_config.json"))
            use document = JsonDocument.Parse(stream)
            let root = document.RootElement

            let mutable model = Unchecked.defaultof<JsonElement>

            if root.TryGetProperty("model", &model) then
                let mutable value = Unchecked.defaultof<JsonElement>

                if model.TryGetProperty("context_length", &value) then
                    contextLength <- value.GetInt32()

                if model.TryGetProperty("type", &value) then
                    match value.GetString() with
                    | null -> ()
                    | text -> modelType <- text

                let mutable decoder = Unchecked.defaultof<JsonElement>

                if model.TryGetProperty("decoder", &decoder) then
                    let mutable options = Unchecked.defaultof<JsonElement>

                    if decoder.TryGetProperty("session_options", &options) then
                        let mutable providers = Unchecked.defaultof<JsonElement>

                        if options.TryGetProperty("provider_options", &providers) then
                            let names =
                                providers.EnumerateArray()
                                |> Seq.collect (fun entry -> entry.EnumerateObject() |> Seq.map _.Name)
                                |> Seq.toArray

                            if names.Length > 0 then
                                provider <- String.Join("+", names)
        with _ ->
            ()

        { Directory = dir
          Version = DirectoryInfo(dir).Name
          ModelType = modelType
          ContextLength = (if contextLength > 0 then contextLength else 4096)
          ConfiguredProvider = provider
          WeightMegabytes = weightMegabytes dir }

    /// Version folders sort newest-last by their numeric parts, so a plain string sort
    /// would put "2026.5.30.1" before "2026.10.1.1". Compare component-wise instead.
    let private versionKey (dir: string) =
        let name = DirectoryInfo(dir).Name

        name.Split('.')
        |> Array.map (fun part ->
            match Int32.TryParse(part) with
            | true, value -> value
            | _ -> -1)
        |> Array.toList

    let private modelDirsUnder (root: string) =
        let container = Path.Combine(root, ModelFolderName)

        if isModelDir root then [ root ]
        elif isModelDir container then [ container ]
        elif Directory.Exists(container) then
            Directory.GetDirectories(container) |> Array.filter isModelDir |> Array.toList
        else
            []

    /// All on-device models visible on this machine, best candidate first.
    let discoverAll () =
        searchRoots ()
        |> List.collect (fun root ->
            try
                modelDirsUnder root
            with _ ->
                [])
        |> List.distinctBy (fun dir -> dir.ToLowerInvariant())
        |> List.sortByDescending versionKey
        |> List.map describe
        |> List.toArray

    let discover () = discoverAll () |> Array.tryHead

    // ------------------------------------------------------------------
    // Prompt shaping
    // ------------------------------------------------------------------

    let systemPrompt =
        "You are the Yumlog recording analyst. You read compacted telemetry from a screen "
        + "recording. Answer only from the telemetry you were given. Frame counts describe "
        + "sampling density, not elapsed time - never infer a duration from a frame count. "
        + "If the telemetry does not show something, say so instead of inventing it."

    // The model ships without a chat template and <|im_end|> is not one of its EOS ids,
    // so generation must be stopped on the marker text rather than by IsDone() alone.
    // Enumerating exact markers proved to be whack-a-mole: the model invents malformed
    // terminators freely (observed across runs: "<|im_start|>", "</|im_start|>", "</im",
    // "</assistant|>"). Both "<|" and "</" are ChatML/tag sentinels that cannot occur in
    // the natural-language telemetry prose this model is asked to produce, so stopping on
    // the two-character sentinel covers every past and future variant.
    let private stopMarkers = [| "<|"; "</" |]

    /// Length of the longest suffix of `text` that is a proper prefix of some stop
    /// marker. Such a suffix may still grow into a full marker on the next token, so
    /// it must be held back rather than streamed: emitting it leaks marker fragments
    /// (observed: a bare "</im") into the caller's output even though the accumulated
    /// answer is later truncated correctly.
    let private danglingMarkerLength (text: string) =
        let mutable best = 0

        for marker in stopMarkers do
            let longest = min (marker.Length - 1) text.Length

            for length in 1..longest do
                if length > best
                   && String.CompareOrdinal(text, text.Length - length, marker, 0, length) = 0 then
                    best <- length

        best

    let private turn (role: string) (content: string) =
        "<|im_start|>" + role + "\n" + content + "<|im_end|>\n"

    let private truncateText (maxLength: int) (value: string) =
        if String.IsNullOrEmpty(value) then ""
        elif value.Length <= maxLength then value
        else value.Substring(0, maxLength) + "..."

    /// One telemetry line per runbook step. Kept deliberately short: the window is the
    /// scarce resource, and a 1.4 GB model echoes long input instead of compressing it.
    let stepLine (step: MdkgIntel.RunbookStep) =
        let parts = ResizeArray<string>()

        let observed =
            if String.IsNullOrWhiteSpace(step.ObservedText) then step.Title else step.ObservedText

        if not (String.IsNullOrWhiteSpace(observed)) then
            parts.Add("text=" + truncateText 120 (observed.Replace("\n", " ").Trim()))

        if not (isNull (box step.Affordances)) && step.Affordances.Length > 0 then
            parts.Add("ui=" + String.Join("|", step.Affordances |> Array.truncate 6))

        parts.Add($"graph=%d{step.GraphNodeCount}n/%d{step.GraphEdgeCount}e")

        let body = String.Join(" ", parts)
        $"t=%.2f{step.OffsetSeconds}s | {body}", body

    // ------------------------------------------------------------------
    // Continuous session with deterministic hot swap
    // ------------------------------------------------------------------

    type private StateAggregate =
        { mutable Count: int
          mutable FirstTs: float
          mutable LastTs: float }

    /// Holds one resident generator, streams telemetry into it, and swaps to a fresh
    /// generator seeded with an exact carry-over when the window approaches full.
    ///
    /// The carry-over is computed from observed state, never by the model. An earlier
    /// LLM-distilled design cost ~87 s per swap and produced a carry-over LARGER than
    /// the swap threshold, so every subsequent append re-triggered a swap. Deterministic
    /// carry-over is instant, bounded, and cannot hallucinate.
    type ContinuousSession(model: Model, tokenizer: Tokenizer, contextLimit: int, highWaterPct: float) =
        let states = Dictionary<string, StateAggregate>(StringComparer.Ordinal)
        let swaps = ResizeArray<LlmSwap>()
        let mutable observedCount = 0
        let mutable firstTs = Double.NaN
        let mutable lastTs = 0.0
        let mutable sinceSwap = 0
        let mutable carryOver = ""
        // Telemetry is buffered and appended in batches. Each append triggers a prefill,
        // and prefill cost grows with the cache, so 155 single-line appends cost far more
        // than a handful of batched ones. The token stream the model sees is identical.
        let pending = StringBuilder()
        let mutable pendingLines = 0
        let mutable generatorParams: GeneratorParams | null = null
        let mutable generator: Generator | null = null

        // Strict nullness is on, so the live generator is reached through one checked
        // accessor rather than sprinkling null tests across every call site.
        let current () =
            match generator with
            | null -> failwith "Reasoning session is not open. Call Open() before ingesting."
            | value -> value

        let disposeGenerator () =
            match generator with
            | null -> ()
            | value ->
                value.Dispose()
                generator <- null

            match generatorParams with
            | null -> ()
            | value ->
                value.Dispose()
                generatorParams <- null

        let freshGenerator () =
            disposeGenerator ()
            let parameters = new GeneratorParams(model)
            parameters.SetSearchOption("max_length", float contextLimit)
            parameters.SetSearchOption("do_sample", false)
            // genai_config.json ships repetition_penalty 1.0, which makes greedy decoding
            // restate the same sentence until the token budget runs out on thin input.
            parameters.SetSearchOption("repetition_penalty", 1.1)
            generatorParams <- parameters
            generator <- new Generator(model, parameters)

        let appendText (text: string) =
            use sequences = tokenizer.Encode(text)
            (current ()).AppendTokenSequences(sequences)

        let measure (text: string) =
            use sequences = tokenizer.Encode(text)
            let span = sequences.[0UL]
            span.Length

        member _.Swaps = swaps.ToArray()
        member _.SwapCount = swaps.Count
        member _.CarryOver = carryOver
        member _.Segments = observedCount
        member _.ContextTokens =
            match generator with
            | null -> 0
            | value -> int (value.TokenCount())

        member private _.BuildCarryOver(maxStates: int) =
            let coverageStart = if Double.IsNaN(firstTs) then 0.0 else firstTs

            let ranked =
                states
                |> Seq.sortByDescending (fun pair -> pair.Value.Count)
                |> Seq.truncate (max 0 maxStates)
                |> Seq.map (fun pair ->
                    $"- %d{pair.Value.Count} frames (%.2f{pair.Value.FirstTs}s..%.2f{pair.Value.LastTs}s): "
                    + truncateText 160 pair.Key)
                |> Seq.toList

            let header =
                [ "Carry-over state for this recording (exact, machine-generated):"
                  $"- coverage: %.2f{coverageStart}s..%.2f{lastTs}s across %d{observedCount} frames"
                  $"- distinct observed states: %d{states.Count}" ]

            String.Join("\n", header @ ranked)

        member this.Open() =
            freshGenerator ()
            appendText (turn "system" systemPrompt)

        member private this.HotSwap(atSegment: int) =
            let watch = Stopwatch.StartNew()
            let before = this.ContextTokens
            freshGenerator ()
            appendText (turn "system" systemPrompt)

            // Shrink until the carry-over sits well below the trigger, otherwise the
            // very next append would swap again and the run would thrash.
            let ceiling = max 64 (int (float contextLimit * highWaterPct * 0.5))
            let mutable maxStates = 8
            let mutable text = this.BuildCarryOver(maxStates)

            while maxStates > 0 && measure text > ceiling do
                maxStates <- maxStates - 2
                text <- this.BuildCarryOver(max 0 maxStates)

            carryOver <- text
            appendText (turn "user" text)
            sinceSwap <- 0

            swaps.Add(
                { Index = swaps.Count + 1
                  AtSegment = atSegment
                  TokensBefore = before
                  TokensAfter = this.ContextTokens
                  ElapsedMs = watch.ElapsedMilliseconds }
            )

        member private _.Observe(signature: string, timestamp: float) =
            observedCount <- observedCount + 1

            if Double.IsNaN(firstTs) then
                firstTs <- timestamp

            lastTs <- timestamp

            match states.TryGetValue(signature) with
            | true, aggregate ->
                aggregate.Count <- aggregate.Count + 1
                aggregate.LastTs <- timestamp
            | _ ->
                states.[signature] <-
                    { Count = 1
                      FirstTs = timestamp
                      LastTs = timestamp }

        /// Writes any buffered telemetry into the model. Safe to call repeatedly.
        member _.Flush() =
            if pending.Length > 0 then
                let text = pending.ToString()
                pending.Clear() |> ignore
                pendingLines <- 0
                appendText text

        member this.Ingest(line: string, signature: string, timestamp: float) =
            this.Observe(signature, timestamp)
            pending.Append(line).Append('\n') |> ignore
            pendingLines <- pendingLines + 1
            sinceSwap <- sinceSwap + 1

            if pendingLines >= BatchLines then
                this.Flush()

                // The minimum run between swaps is a second guard against thrash: even a
                // mis-sized carry-over cannot cause swaps on consecutive batches.
                if float this.ContextTokens / float contextLimit >= highWaterPct
                   && sinceSwap >= BatchLines then
                    this.HotSwap(observedCount)

            this.ContextTokens

        /// Answers on the trunk, then rewinds the question and the answer back out of
        /// it. This is the native equivalent of the browser engine's free clone(): the
        /// telemetry context is never displaced by conversation.
        member this.Ask(question: string, maxNewTokens: int, onPiece: string -> unit) =
            this.Flush()
            let active = current ()
            let mark = active.TokenCount()
            appendText (turn "user" question + "<|im_start|>assistant\n")

            use stream = tokenizer.CreateStream()
            let builder = StringBuilder()
            let mutable produced = 0
            let mutable emitted = 0
            let mutable stop = false

            while not stop && not (active.IsDone()) && produced < maxNewTokens do
                active.GenerateNextToken()
                let sequence = active.GetSequence(0UL)
                let token = sequence.[sequence.Length - 1]
                let piece = stream.Decode(token)
                builder.Append(piece) |> ignore
                produced <- produced + 1

                let soFar = builder.ToString()

                match stopMarkers |> Array.tryFind soFar.Contains with
                | Some marker ->
                    let cut = soFar.IndexOf(marker, StringComparison.Ordinal)
                    builder.Clear().Append(soFar.Substring(0, cut)) |> ignore

                    // Hold-back guarantees the marker itself was never streamed, so any
                    // text before it is still owed to the caller.
                    if cut > emitted then
                        onPiece (soFar.Substring(emitted, cut - emitted))
                        emitted <- cut

                    stop <- true
                | None ->
                    let safe = soFar.Length - danglingMarkerLength soFar

                    if safe > emitted then
                        onPiece (soFar.Substring(emitted, safe - emitted))
                        emitted <- safe

            // A trailing partial marker never completed, so it is junk rather than text
            // the model meant to produce. Dropping it keeps the streamed output and the
            // returned answer byte-for-byte identical.
            if not stop then
                let final = builder.ToString()
                let keep = final.Length - danglingMarkerLength final

                if keep < final.Length then
                    builder.Clear().Append(final.Substring(0, keep)) |> ignore

            active.RewindTo(mark)
            builder.ToString().Trim(), produced

        interface IDisposable with
            member _.Dispose() = disposeGenerator ()

    // ------------------------------------------------------------------
    // Provider selection
    // ------------------------------------------------------------------

    let normalizeProvider (value: string | null) =
        match (match value with
               | null -> ""
               | text -> text.Trim().ToLowerInvariant())
            with
        | ""
        | "cpu"
        | "default" -> "cpu"
        | "auto" -> "auto"
        | "webgpu" -> "webgpu"
        | "dml"
        | "directml" -> "dml"
        | "cuda" -> "cuda"
        | other -> invalidArg "provider" $"Unknown provider '{other}'. Use cpu, auto, webgpu, dml, or cuda."

    /// "auto" keeps whatever genai_config.json specifies (WebGPU, for Edge's copy).
    /// Anything else replaces the provider list outright. Non-CPU providers need the
    /// matching ONNX Runtime GenAI native package to be present.
    let private applyProvider (provider: string) (config: Config) =
        match provider with
        | "auto" -> ()
        | "cpu" -> config.ClearProviders()
        | other ->
            config.ClearProviders()
            config.AppendProvider(other)

    // ------------------------------------------------------------------
    // Entry point
    // ------------------------------------------------------------------

    /// Resolves an explicit --model folder or falls back to discovery. Shared by the
    /// offline runbook path and the live watch path so both fail with the same guidance.
    let private resolveModelInfo (modelDir: string) =
        if not (String.IsNullOrWhiteSpace(modelDir)) then
            if not (isModelDir modelDir) then
                failwith
                    $"No genai_config.json under '{modelDir}'. Point --model at an ONNX Runtime GenAI model folder."

            describe modelDir
        else
            match discover () with
            | Some found -> found
            | None ->
                failwith
                    "No on-device model found. Install it once via Edge Canary's built-in Prompt API, or pass --model <folder>, or set YUMLOG_LLM_MODEL_DIR."

    /// Streams a runbook through the resident model and answers one question about it.
    /// onPiece receives answer text as it is generated so a caller can render a stream.
    let reasonOverRunbook (options: ReasonConfig) (runbook: MdkgIntel.Runbook) (onPiece: string -> unit) =
        let total = Stopwatch.StartNew()
        let provider = normalizeProvider options.Provider
        let info = resolveModelInfo options.ModelDir

        let load = Stopwatch.StartNew()
        use config = new Config(info.Directory)
        applyProvider provider config
        use model = new Model(config)
        load.Stop()

        use tokenizer = new Tokenizer(model)

        let highWater = Math.Clamp(options.HighWaterPct, 0.05, 0.95)
        let session = new ContinuousSession(model, tokenizer, info.ContextLength, highWater)
        use _guard = (session :> IDisposable)
        session.Open()

        let steps =
            if options.MaxSegments > 0 then
                runbook.Steps |> Array.truncate options.MaxSegments
            else
                runbook.Steps

        let ingest = Stopwatch.StartNew()

        for step in steps do
            let line, signature = stepLine step
            session.Ingest(line, signature, step.OffsetSeconds) |> ignore

        session.Flush()
        let segmentTokens = session.ContextTokens
        ingest.Stop()

        // Ground facts the telemetry cannot convey on its own. Without these the model
        // infers a duration from the frame count and reports minutes for a 6 s clip.
        //
        // Capture rate and drop count are properties of the recording, not of any one
        // step, so they belong here rather than repeated on every ingested line. They
        // are read back from the timing proofs the runbook already resolved, so these
        // statements remain citable rather than asserted. Omitting them is not
        // neutral: asked "what frame rate was this captured at" with no grounding, the
        // model reported 30 fps for a 15 fps recording.
        let proofValue (pointerSuffix: string) (step: MdkgIntel.RunbookStep) =
            if isNull (box step.Proofs) then
                None
            else
                step.Proofs
                |> Array.tryFind (fun proof -> proof.Path.EndsWith(pointerSuffix, StringComparison.Ordinal))
                |> Option.map (fun proof -> proof.Value)

        let captureFps =
            steps |> Array.tryPick (proofValue "#/fps")

        let maxDropped =
            steps
            |> Array.choose (proofValue "#/dropCount")
            |> Array.choose (fun value ->
                match Int32.TryParse(value) with
                | true, parsed -> Some parsed
                | _ -> None)
            |> function
                | [||] -> None
                | values -> Some(Array.max values)

        let grounding =
            $"Ground facts (authoritative): source={Path.GetFileName(runbook.Source)}; "
            + $"duration=%.3f{runbook.DurationSeconds} seconds; steps=%d{runbook.StepCount}; "
            + $"segments ingested=%d{steps.Length}"
            + (match captureFps with
               | Some fps -> $"; capture frame rate={fps} fps"
               | None -> "")
            + (match maxDropped with
               | Some dropped -> $"; frames dropped by the capture pipeline={dropped}"
               | None -> "")
            + "."

        let answering = Stopwatch.StartNew()
        let answer, produced = session.Ask(grounding + "\n\n" + options.Question, options.MaxNewTokens, onPiece)
        answering.Stop()

        let seconds = answering.Elapsed.TotalSeconds

        { Model = info
          Provider = provider
          ModelLoadMs = load.ElapsedMilliseconds
          Segments = steps.Length
          SegmentTokens = segmentTokens
          Swaps = session.Swaps
          SwapCount = session.SwapCount
          CarryOver = session.CarryOver
          ContextTokens = session.ContextTokens
          ContextLimit = info.ContextLength
          HighWaterPct = highWater
          Question = options.Question
          Answer = answer
          AnswerTokens = produced
          TokensPerSecond = (if seconds > 0.0 then Math.Round(float produced / seconds, 2) else 0.0)
          IngestMs = ingest.ElapsedMilliseconds
          AnswerMs = answering.ElapsedMilliseconds
          TotalMs = total.ElapsedMilliseconds }

    // ------------------------------------------------------------------
    // Live screen watching
    // ------------------------------------------------------------------

    /// One narration emitted while watching, with the frame window it covers.
    type LlmNarration =
        { Index: int
          AtSeconds: float
          FramesCovered: int
          ChangesCovered: int
          Text: string
          TokensProduced: int
          ElapsedMs: int64 }

    type LlmWatchResult =
        { Model: LlmModelInfo
          Provider: string
          ModelLoadMs: int64
          Frames: int
          Changes: int
          ChangeThreshold: float
          OcrProvider: string
          OcrAvailable: bool
          Narrations: LlmNarration array
          NarrationCount: int
          Swaps: LlmSwap array
          SwapCount: int
          CarryOver: string
          ContextTokens: int
          ContextLimit: int
          HighWaterPct: float
          WatchedSeconds: float
          TotalMs: int64 }

    /// One telemetry line per CHANGED frame. Mirrors stepLine so the model sees the
    /// same shape live as it does replaying a runbook.
    ///
    /// When OCR is unavailable the absence of text is stated explicitly rather than
    /// omitted. An empty line reads to the model as "nothing worth reporting", and it
    /// fills the gap by inventing UI it never saw.
    let private liveLine (atSeconds: float) (changeScore: float) (ocrText: string) (hints: UiNavigationHint array) =
        let parts = ResizeArray<string>()
        parts.Add($"change=%.1f{changeScore * 100.0}%%")

        if String.IsNullOrWhiteSpace(ocrText) then
            parts.Add("text=<none captured>")
        else
            parts.Add("text=" + truncateText 160 (ocrText.Replace("\n", " ").Trim()))

        if not (isNull (box hints)) && hints.Length > 0 then
            let labels =
                hints
                |> Array.map (fun hint -> hint.Kind + ":" + truncateText 24 hint.Label)
                |> Array.distinct
                |> Array.truncate 6

            parts.Add("ui=" + String.Join("|", labels))

        let body = String.Join(" ", parts)
        $"t=%.2f{atSeconds}s | {body}", body

    /// Watches the screen live and streams every changed frame into one resident model.
    ///
    /// This is the streaming counterpart to reasonOverRunbook: instead of replaying a
    /// finished recording, frames are captured one at a time and only those that differ
    /// from the previous frame are ingested. Narration is produced from the SAME session
    /// via RewindTo, so asking a question mid-stream costs nothing and never truncates
    /// the accumulated context.
    let watchScreen (options: WatchConfig) (onPiece: string -> unit) (onTick: int -> float -> float -> bool -> unit) =
        let total = Stopwatch.StartNew()
        let provider = normalizeProvider options.Provider
        let info = resolveModelInfo options.ModelDir

        let load = Stopwatch.StartNew()
        use config = new Config(info.Directory)
        applyProvider provider config
        use model = new Model(config)
        load.Stop()

        use tokenizer = new Tokenizer(model)

        let highWater = Math.Clamp(options.HighWaterPct, 0.05, 0.95)
        let session = new ContinuousSession(model, tokenizer, info.ContextLength, highWater)
        use _guard = (session :> IDisposable)
        session.Open()

        // Probe OCR once. Whether screen text is readable decides what the model may
        // legitimately claim, so it is established before any frame is ingested rather
        // than left for the model to infer from empty lines.
        let ocrRequested =
            not (String.IsNullOrWhiteSpace(options.OcrMode))
            && not (List.contains (options.OcrMode.Trim().ToLowerInvariant()) [ "none"; "off" ])

        let ocrAvailable = ocrRequested && NativeOcr.windowsAiAvailable ()

        let capability =
            if not ocrRequested then
                "Screen text capture is DISABLED for this session. Telemetry contains only the "
                + "percentage of pixels that changed between frames."
            elif not ocrAvailable then
                "Screen text capture was requested but is UNAVAILABLE on this machine. Telemetry "
                + "contains only the percentage of pixels that changed between frames."
            else
                "Telemetry contains the percentage of pixels that changed between frames plus the "
                + "text read from the screen."

        session.Ingest(
            "Ground facts (authoritative): "
            + capability
            + " You can therefore report WHEN and HOW MUCH the screen changed. "
            + "You cannot identify applications, windows, controls, or user intent unless they "
            + "appear in a text= field. If text=<none captured>, say the screen changed but its "
            + "content is unknown. Never name an app or a UI element that is not in the telemetry.",
            "capability",
            0.0
        )
        |> ignore

        let captureConfig =
            { OutDir = options.OutDir
              Fps = options.Fps
              DurationSec = options.DurationSec
              Count = 0
              AllScreens = true
              ImageFormat = "png" }

        let stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss")
        let intervalMs = if options.Fps > 0 then max 1 (1000 / options.Fps) else 1000
        let narrateEvery = max 0.0 options.NarrateEverySec
        let narrations = ResizeArray<LlmNarration>()

        let mutable previousPath = ""
        let mutable frames = 0
        let mutable changes = 0
        let mutable ocrProvider = if ocrAvailable then options.OcrMode else "none"
        let mutable lastNarrationAt = 0.0
        let mutable framesAtLastNarration = 0
        let mutable changesAtLastNarration = 0

        let watch = Stopwatch.StartNew()

        while watch.Elapsed.TotalSeconds < float options.DurationSec do
            let frame = NativeCapture.captureFrame captureConfig stamp frames
            let atSeconds = watch.Elapsed.TotalSeconds
            // A 4px grid catches text-level UI churn, which the coarse default grid used
            // by 'follow' misses almost entirely on a large desktop.
            let score = NativeFollow.frameChangeScoreAt 4 previousPath frame.Path
            previousPath <- frame.Path
            frames <- frames + 1

            // The first frame is always ingested: it establishes what "unchanged" means.
            let ingested = score >= options.ChangeThreshold || frames = 1

            if ingested then
                changes <- changes + 1

                let ocrText, hints =
                    if ocrAvailable then
                        let ocr = NativeOcr.recognize options.OcrMode frame.Path
                        ocrProvider <- ocr.Provider
                        ocr.Text, NativeNavigation.inferHints ocr
                    else
                        "", Array.empty

                let line, signature = liveLine atSeconds score ocrText hints
                session.Ingest(line, signature, atSeconds) |> ignore

            onTick frames atSeconds score ingested

            if narrateEvery > 0.0
               && atSeconds - lastNarrationAt >= narrateEvery
               && changes > changesAtLastNarration then
                let ask = Stopwatch.StartNew()
                let answer, produced = session.Ask(options.Question, options.MaxNewTokens, onPiece)
                ask.Stop()

                narrations.Add(
                    { Index = narrations.Count + 1
                      AtSeconds = Math.Round(atSeconds, 2)
                      FramesCovered = frames - framesAtLastNarration
                      ChangesCovered = changes - changesAtLastNarration
                      Text = answer
                      TokensProduced = produced
                      ElapsedMs = ask.ElapsedMilliseconds }
                )

                lastNarrationAt <- watch.Elapsed.TotalSeconds
                framesAtLastNarration <- frames
                changesAtLastNarration <- changes

            // Pace to the requested fps using time actually left, so capture, OCR, and
            // narration cost is absorbed rather than added on top of the interval.
            let spent = watch.Elapsed.TotalMilliseconds - (atSeconds * 1000.0)
            let remaining = float intervalMs - spent
            if remaining > 0.0 then
                Threading.Thread.Sleep(int remaining)

        session.Flush()
        watch.Stop()

        { Model = info
          Provider = provider
          ModelLoadMs = load.ElapsedMilliseconds
          Frames = frames
          Changes = changes
          ChangeThreshold = options.ChangeThreshold
          OcrProvider = ocrProvider
          OcrAvailable = ocrAvailable
          Narrations = narrations.ToArray()
          NarrationCount = narrations.Count
          Swaps = session.Swaps
          SwapCount = session.SwapCount
          CarryOver = session.CarryOver
          ContextTokens = session.ContextTokens
          ContextLimit = info.ContextLength
          HighWaterPct = highWater
          WatchedSeconds = Math.Round(watch.Elapsed.TotalSeconds, 2)
          TotalMs = total.ElapsedMilliseconds }
