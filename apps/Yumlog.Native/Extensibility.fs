namespace Yumlog.Native

open System
open System.IO
open System.Reflection

/// Extensibility foundation: a single declarative catalog of the models, agents, tools,
/// skills, workflows, connectors, plugins, extensions, MCP/API surfaces, and continuous AI
/// loops this native app exposes, plus a file-based manifest host for third-party additions.
module NativeExtensibility =

    [<Literal>]
    let Namespace = "yumlog.native"

    [<Literal>]
    let SchemaVersion = "v1"

    [<Literal>]
    let ManifestSchemaUri = "urn:yumlog:native:extension-manifest:v1"

    [<Literal>]
    let CatalogSchemaUri = "urn:yumlog:native:extension-catalog:v1"

    [<Literal>]
    let McpProtocolVersion = "2024-11-05"

    module Kind =
        [<Literal>]
        let Model = "model"

        [<Literal>]
        let Agent = "agent"

        [<Literal>]
        let Tool = "tool"

        [<Literal>]
        let Skill = "skill"

        [<Literal>]
        let Workflow = "workflow"

        [<Literal>]
        let Connector = "connector"

        [<Literal>]
        let Plugin = "plugin"

        [<Literal>]
        let Extension = "extension"

        [<Literal>]
        let McpServer = "mcp-server"

        [<Literal>]
        let Api = "api"

        [<Literal>]
        let ContinuousAi = "continuous-ai"

        let all =
            [| Model
               Agent
               Tool
               Skill
               Workflow
               Connector
               Plugin
               Extension
               McpServer
               Api
               ContinuousAi |]

        let isKnown (kind: string) =
            all |> Array.exists (fun known -> String.Equals(known, kind, StringComparison.OrdinalIgnoreCase))

    module Status =
        [<Literal>]
        let Available = "available"

        [<Literal>]
        let Preview = "preview"

        [<Literal>]
        let Unavailable = "unavailable"

        [<Literal>]
        let Disabled = "disabled"

    module Transport =
        [<Literal>]
        let Cli = "cli"

        [<Literal>]
        let InProcess = "in-process"

        [<Literal>]
        let Http = "http"

        [<Literal>]
        let McpStdio = "mcp-stdio"

    module Severity =
        [<Literal>]
        let Error = "error"

        [<Literal>]
        let Warning = "warning"

    let private assembly = Assembly.GetExecutingAssembly()

    let hostVersion =
        match assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
        | null ->
            match assembly.GetName().Version with
            | null -> "0.0.0.0"
            | version -> version.ToString()
        | attribute -> attribute.InformationalVersion

    let identifier kind key =
        Mdkg.Producers.semanticIdentifier Namespace kind key SchemaVersion

    let invocation transport target arguments inputSchemaUri outputSchemaUri =
        let value =
            { Transport = transport
              Target = target
              Arguments = arguments
              InputSchemaUri = inputSchemaUri
              OutputSchemaUri = outputSchemaUri }

        Mdkg.Validation.validateExtensionInvocation "invocation" value |> ignore
        value

    let capability id kind displayName description provider status invocation tags attributes =
        let value =
            { Id = id
              Kind = kind
              DisplayName = displayName
              Description = description
              Provider = provider
              Status = status
              Invocation = invocation
              Tags = tags
              Attributes = attributes }

        Mdkg.Validation.validateExtensionCapability "capability" value |> ignore
        value

    let manifest id name version publisher description enabled source capabilities =
        let value =
            { Id = id
              Name = name
              Version = version
              Publisher = publisher
              Description = description
              Enabled = enabled
              SchemaUri = ManifestSchemaUri
              Source = source
              Capabilities = capabilities }

        Mdkg.Validation.validateExtensionManifest "extension-manifest" value |> ignore
        value

    let private schema name =
        $"urn:yumlog:native:schema:{name}:{SchemaVersion}"

    let private declaredAttribute name value =
        let confidence =
            Mdkg.Producers.semanticConfidence 1.0 "yumlog.native.builtin" "declared" DateTimeOffset.UtcNow

        Mdkg.Producers.semanticAttribute name value "string" confidence

    let private cliCapability key kind displayName description status command arguments inputSchema outputSchema tags =
        capability
            (identifier kind key)
            kind
            displayName
            description
            "yumlog.native"
            status
            (invocation Transport.Cli command arguments inputSchema outputSchema)
            tags
            Array.empty

    let private inProcessCapability key kind displayName description status target inputSchema outputSchema tags attributes =
        capability
            (identifier kind key)
            kind
            displayName
            description
            "yumlog.native"
            status
            (invocation Transport.InProcess target Array.empty inputSchema outputSchema)
            tags
            attributes

    let private ocrModelStatus () =
        try
            if NativeOcr.windowsAiAvailable () then Status.Available else Status.Unavailable
        with _ ->
            Status.Unavailable

    /// Capabilities this app implements natively. Kept declarative so agents, MCP hosts, and
    /// the APIM/ACP service can discover the same surface without hard-coding CLI knowledge.
    let builtInCapabilities () =
        [| inProcessCapability
               "windows-ai-ocr"
               Kind.Model
               "Windows AI text recognizer"
               "On-device Windows App SDK text recognition used by analyze and follow."
               (ocrModelStatus ())
               "Yumlog.Native.WindowsAiOcr.recognize"
               (schema "image-input")
               (schema "ocr-result")
               [| "ocr"; "on-device"; "windows-app-sdk" |]
               [| declaredAttribute "ocrModes" "auto,windows-ai,raw-com,none" |]

           cliCapability
               "capture-screens"
               Kind.Tool
               "Capture screens"
               "Capture one or more desktop frames as PNG files with hashes and timestamps."
               Status.Available
               "capture"
               Array.empty
               (schema "capture-config")
               (schema "frame-info")
               [| "capture"; "screenshot" |]

           cliCapability
               "record-screen"
               Kind.Tool
               "Record screen"
               "Record the desktop to MP4 through the configured FFmpeg encoder."
               Status.Available
               "record"
               Array.empty
               (schema "record-config")
               (schema "record-result")
               [| "record"; "ffmpeg" |]

           cliCapability
               "analyze-image"
               Kind.Tool
               "Analyze image"
               "Analyze an image or directory and emit OCR plus file metadata."
               Status.Available
               "analyze"
               Array.empty
               (schema "analyze-config")
               (schema "analysis-result")
               [| "ocr"; "analysis"; "acp:yumlog.ocr.analyze" |]

           inProcessCapability
               "mdkg-write"
               Kind.Tool
               "MDKG writer"
               "Write layered MDKG metadata samples for timing, OCR, affordances, graphs, ACP, APIM, LiveTile, Widget Studio, and extension catalogs."
               Status.Available
               "Yumlog.Native.Mdkg.MdkgWriter"
               (schema "mdkg-sample")
               (schema "mdkg-file")
               [| "mdkg"; "metadata"; "acp:yumlog.mdkg.write" |]
               Array.empty

           inProcessCapability
               "ui-affordance-hints"
               Kind.Skill
               "UI affordance hints"
               "Infer next/back/submit/cancel/search navigation hints from OCR lines."
               Status.Available
               "Yumlog.Native.NativeNavigation.inferHints"
               (schema "ocr-result")
               (schema "ui-navigation-hint")
               [| "navigation"; "heuristic" |]
               Array.empty

           inProcessCapability
               "frame-integrity-hash"
               Kind.Skill
               "Frame integrity hash"
               "Compute SHA-256 digests for captured frames and recorded artifacts."
               Status.Available
               "Yumlog.Native.FileHash.sha256"
               (schema "file-path")
               (schema "sha256")
               [| "hash"; "provenance" |]
               Array.empty

           cliCapability
               "follow-me"
               Kind.Agent
               "Follow-me observer"
               "Capture, score frame change, run OCR, and infer affordances into a follow manifest."
               Status.Available
               "follow"
               Array.empty
               (schema "follow-config")
               (schema "follow-manifest")
               [| "agent"; "observer" |]

           cliCapability
               "orchestrate-plan"
               Kind.Workflow
               "Orchestration plan runner"
               "Run a JSON orchestration plan of native steps and report per-step results."
               Status.Available
               "orchestrate"
               [| "--plan" |]
               (schema "orchestration-plan")
               (schema "orchestration-step-result")
               [| "workflow"; "plan" |]

           capability
               (identifier Kind.Connector "apim-service")
               Kind.Connector
               "Yumlog APIM service"
               "Loopback APIM/ACP service that brokers routes, tools, prompts, and resources for agents."
               "yumlog.apim.service"
               Status.Preview
               (invocation Transport.Http "http://127.0.0.1:5118" Array.empty (schema "apim-request") (schema "apim-trace"))
               [| "apim"; "acp"; "loopback" |]
               [| declaredAttribute "capabilitiesPath" "/capabilities" |]

           capability
               (identifier Kind.Api "acp-http")
               Kind.Api
               "ACP HTTP surface"
               "ACP resource, tool, and prompt endpoints exposed by the local APIM service."
               "yumlog.apim.service"
               Status.Preview
               (invocation Transport.Http "http://127.0.0.1:5118/acp" Array.empty (schema "acp-request") (schema "acp-context"))
               [| "acp"; "api" |]
               Array.empty

           capability
               (identifier Kind.McpServer "paperboy-mcp")
               Kind.McpServer
               "Paperboy MCP host"
               "Stdio MCP server that exposes Paperboy bundle and delivery tools to agents."
               "paperboy.mcp"
               Status.Preview
               (invocation Transport.McpStdio "Paperboy.Mcp" Array.empty (schema "mcp-request") (schema "mcp-response"))
               [| "mcp"; "stdio" |]
               [| declaredAttribute "protocolVersion" McpProtocolVersion |]

           inProcessCapability
               "mdkg-extension-layer"
               Kind.Plugin
               "MDKG extension catalog layer"
               "Emit the resolved extension catalog as MDKG layer 11 so recordings carry their own capability provenance."
               Status.Available
               "Yumlog.Native.Mdkg.MdkgWriter.WriteExtensionCatalog"
               (schema "extension-catalog")
               "urn:yumlog:mdkg:layer:extension-catalog:v1"
               [| "mdkg"; "plugin" |]
               [| declaredAttribute "layerId" (string Mdkg.LayerId.ExtensionCatalog) |]

           inProcessCapability
               "extension-host"
               Kind.Extension
               "File-based extension host"
               "Load, validate, and merge extension manifests from the configured extensions directory."
               Status.Available
               "Yumlog.Native.NativeExtensibility.loadManifests"
               ManifestSchemaUri
               CatalogSchemaUri
               [| "host"; "manifest" |]
               [| declaredAttribute "manifestSchemaUri" ManifestSchemaUri |]

           cliCapability
               "follow-loop"
               Kind.ContinuousAi
               "Continuous follow loop"
               "Continuous capture, change scoring, OCR, and affordance inference for always-on observation."
               Status.Available
               "follow"
               [| "--duration" |]
               (schema "follow-config")
               (schema "follow-manifest")
               [| "continuous"; "loop"; "observer" |] |]

    let private emptyIfNull (values: 'T array) =
        if Object.ReferenceEquals(values, null) then Array.empty else values

    let private textOrDefault (value: string) fallback =
        if String.IsNullOrWhiteSpace(value) then fallback else value

    let private normalizeInvocation (value: ExtensionInvocation) =
        if Object.ReferenceEquals(value, null) then
            invocation Transport.InProcess "" Array.empty "" ""
        else
            { value with
                Transport = textOrDefault value.Transport Transport.InProcess
                Target = textOrDefault value.Target ""
                Arguments = emptyIfNull value.Arguments
                InputSchemaUri = textOrDefault value.InputSchemaUri ""
                OutputSchemaUri = textOrDefault value.OutputSchemaUri "" }

    let private normalizeCapability (publisher: string) (value: ExtensionCapability) =
        { value with
            Provider = textOrDefault value.Provider publisher
            Status = textOrDefault value.Status Status.Available
            Description = textOrDefault value.Description ""
            Invocation = normalizeInvocation value.Invocation
            Tags = emptyIfNull value.Tags
            Attributes = emptyIfNull value.Attributes }

    /// Manifests are authored by hand, so optional members are backfilled before validation.
    let normalizeManifest (source: string) (value: ExtensionManifest) =
        let publisher = textOrDefault value.Publisher "unknown"

        { value with
            Version = textOrDefault value.Version "0.1.0"
            Publisher = publisher
            Description = textOrDefault value.Description ""
            SchemaUri = textOrDefault value.SchemaUri ManifestSchemaUri
            Source = textOrDefault source value.Source
            Capabilities =
                emptyIfNull value.Capabilities
                |> Array.map (normalizeCapability publisher) }

    let manifestDirectory (config: ExtensionsConfig) =
        let dir = textOrDefault config.Dir "config/extensions"
        Path.GetFullPath(dir)

    let manifestFiles (config: ExtensionsConfig) =
        let dir = manifestDirectory config

        if not config.Enabled || not (Directory.Exists(dir)) then
            Array.empty
        else
            Directory.EnumerateFiles(dir, textOrDefault config.ManifestPattern "*.extension.json")
            |> Seq.sortBy (fun path -> Path.GetFileName(path))
            |> Seq.toArray

    let tryLoadManifest (path: string) =
        try
            NativeJson.read<ExtensionManifest> path
            |> normalizeManifest path
            |> Mdkg.Validation.validateExtensionManifest "extension-manifest"
            |> Ok
        with ex ->
            Error
                { Source = path
                  Severity = Severity.Error
                  Message = ex.Message }

    /// Loads every manifest in the configured directory, returning manifests and load issues.
    let loadManifests (config: ExtensionsConfig) =
        let manifests = ResizeArray<ExtensionManifest>()
        let issues = ResizeArray<ExtensionIssue>()

        for path in manifestFiles config do
            match tryLoadManifest path with
            | Ok manifest -> manifests.Add(manifest)
            | Error issue -> issues.Add(issue)

        manifests.ToArray(), issues.ToArray()

    let private manifestCapabilities (manifests: ExtensionManifest array) =
        manifests
        |> Array.filter (fun manifest -> manifest.Enabled)
        |> Array.collect (fun manifest -> manifest.Capabilities)

    let catalogOf (manifests: ExtensionManifest array) (includeBuiltIn: bool) =
        let builtIn = if includeBuiltIn then builtInCapabilities () else Array.empty

        let value =
            { CatalogId = identifier "catalog" "native"
              Host = "Yumlog.Native"
              HostVersion = hostVersion
              GeneratedUtc = DateTimeOffset.UtcNow
              Manifests = manifests
              Capabilities = Array.append builtIn (manifestCapabilities manifests) }

        Mdkg.Validation.validateExtensionCatalog "extension-catalog" value |> ignore
        value

    /// Resolves the full catalog: built-in capabilities plus enabled manifest capabilities.
    let catalog (config: ExtensionsConfig) =
        let manifests, _ = loadManifests config
        catalogOf manifests config.IncludeBuiltIn

    let private matches (filter: string) (value: string) =
        String.IsNullOrWhiteSpace(filter)
        || String.Equals(filter, value, StringComparison.OrdinalIgnoreCase)

    let private containsText (filter: string) (value: string) =
        String.IsNullOrWhiteSpace(filter)
        || (not (String.IsNullOrEmpty(value))
            && value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)

    let filter (query: ExtensionQuery) (source: ExtensionCatalog) =
        if Object.ReferenceEquals(query, null) then
            source
        else
            { source with
                Capabilities =
                    source.Capabilities
                    |> Array.filter (fun item ->
                        matches query.Kind item.Kind
                        && (containsText query.Name item.Id.Key
                            || containsText query.Name item.DisplayName)) }

    let validate (config: ExtensionsConfig) =
        let manifests, loadIssues = loadManifests config
        let issues = ResizeArray<ExtensionIssue>(loadIssues)
        let seen = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

        for manifest in manifests do
            for item in manifest.Capabilities do
                let key = $"{item.Kind}/{item.Id.Key}"

                if not (Kind.isKnown item.Kind) then
                    issues.Add(
                        { Source = manifest.Source
                          Severity = Severity.Warning
                          Message = $"Capability '{key}' uses unknown kind '{item.Kind}'." })

                if not (seen.Add(key)) then
                    issues.Add(
                        { Source = manifest.Source
                          Severity = Severity.Warning
                          Message = $"Capability '{key}' is declared more than once." })

        let capabilityCount =
            manifests |> Array.sumBy (fun manifest -> manifest.Capabilities.Length)

        { Directory = manifestDirectory config
          ManifestCount = manifests.Length
          CapabilityCount = capabilityCount
          IsValid = issues |> Seq.forall (fun issue -> issue.Severity <> Severity.Error)
          Issues = issues.ToArray() }

    let private isInvocable (item: ExtensionCapability) =
        match item.Kind with
        | Kind.Tool
        | Kind.Skill
        | Kind.Agent
        | Kind.Workflow
        | Kind.ContinuousAi -> true
        | _ -> false

    let mcpToolName (item: ExtensionCapability) =
        $"{Namespace}.{item.Kind}.{item.Id.Key}"

    /// Projects the catalog onto an MCP-shaped tool manifest that MCP hosts can serve verbatim.
    let mcpManifest (source: ExtensionCatalog) =
        { Name = source.Host
          Version = source.HostVersion
          ProtocolVersion = McpProtocolVersion
          Description = "Yumlog.Native capture, recording, OCR, follow-me, and MDKG capabilities."
          Tools =
            source.Capabilities
            |> Array.filter isInvocable
            |> Array.filter (fun item -> item.Status <> Status.Unavailable && item.Status <> Status.Disabled)
            |> Array.map (fun item ->
                { Name = mcpToolName item
                  Title = item.DisplayName
                  Description = item.Description
                  Transport = item.Invocation.Transport
                  Command = item.Invocation.Target
                  Arguments = item.Invocation.Arguments
                  InputSchemaUri = item.Invocation.InputSchemaUri
                  OutputSchemaUri = item.Invocation.OutputSchemaUri
                  Tags = item.Tags }) }

    let mdkgSample ptsTicks durationTicks (source: ExtensionCatalog) =
        Mdkg.Producers.extensionCatalogLayerSample ptsTicks durationTicks source

    /// Writes the catalog into a standalone .mdkg file as an extension-catalog keyframe sample.
    let writeMdkg (path: string) (source: ExtensionCatalog) =
        use writer = Mdkg.MdkgWriter.Open(path)
        writer.AddDefaultLayers()
        writer.WriteExtensionCatalog(mdkgSample 0L 0L source)
        writer.Close()
        Path.GetFullPath(path)
