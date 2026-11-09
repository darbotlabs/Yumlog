namespace Yumlog.APIM.Service

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Runtime.Versioning
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Json
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Hosting.WindowsServices
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Console

type ServiceRuntimeState() =
    let gate = obj()
    let mutable status = "starting"
    let mutable startedAtUtc = Nullable<DateTimeOffset>()
    let mutable lastHeartbeatUtc = Nullable<DateTimeOffset>()

    member _.MarkReady(utc: DateTimeOffset) =
        lock gate (fun () ->
            status <- "ready"
            startedAtUtc <- Nullable utc
            lastHeartbeatUtc <- Nullable utc)

    member _.MarkHeartbeat(utc: DateTimeOffset) =
        lock gate (fun () -> lastHeartbeatUtc <- Nullable utc)

    member _.MarkStopping() =
        lock gate (fun () -> status <- "stopping")

    member _.Status =
        lock gate (fun () -> status)

    member _.UptimeSeconds(utc: DateTimeOffset) =
        lock gate (fun () ->
            if startedAtUtc.HasValue then
                Math.Round((utc - startedAtUtc.Value).TotalSeconds, 3)
            else
                0.0)

    member _.Snapshot() =
        lock gate (fun () ->
            {|
                status = status
                startedAtUtc = startedAtUtc
                lastHeartbeatUtc = lastHeartbeatUtc
            |})

type ApimFoundationWorker(logger: ILogger<ApimFoundationWorker>, state: ServiceRuntimeState) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            let started = DateTimeOffset.UtcNow
            state.MarkReady(started)
            logger.LogInformation("Yumlog APIM background worker started at {StartedAtUtc}.", started)

            try
                while not stoppingToken.IsCancellationRequested do
                    state.MarkHeartbeat(DateTimeOffset.UtcNow)
                    do! Task.Delay(TimeSpan.FromSeconds(15.0), stoppingToken)
            with
            | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> ()

            state.MarkStopping()
            logger.LogInformation("Yumlog APIM background worker stopped.")
        }

module private Metadata =
    let serviceName = "Yumlog.APIM.Service"
    let displayName = "Yumlog APIM Service"
    let schemaVersion = "2026-07-07.phase1"
    let apiVersion = "v1"
    let acpVersion = "acp/0.1"

    let assembly = Assembly.GetExecutingAssembly()

    let assemblyVersion =
        match assembly.GetName().Version with
        | null -> "0.0.0.0"
        | version -> version.ToString()

    let informationalVersion =
        let attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        if isNull attribute then assemblyVersion else attribute.InformationalVersion

    let targetFramework =
        let attribute = assembly.GetCustomAttribute<TargetFrameworkAttribute>()
        if isNull attribute then "unknown" else attribute.FrameworkName

module private Paths =
    let private hasYumlogShape (path: string) =
        Directory.Exists(Path.Combine(path, "apps"))
        && Directory.Exists(Path.Combine(path, "launchers"))

    let rec private findRoot (directory: DirectoryInfo) =
        if isNull directory then
            Directory.GetCurrentDirectory()
        elif hasYumlogShape directory.FullName then
            directory.FullName
        else
            findRoot directory.Parent

    let yumlogRoot (configuration: IConfiguration) =
        let configured = configuration["Yumlog:Root"]
        if String.IsNullOrWhiteSpace(configured) then
            findRoot (DirectoryInfo(AppContext.BaseDirectory))
        else
            Path.GetFullPath(configured)

    let combine root segments =
        Path.Combine(Array.append [| root |] segments)

module private Responses =
    let private endpoint method path description =
        {| method = method; path = path; description = description |}

    let endpoints =
        [|
            endpoint "GET" "/" "Redirects to /capabilities."
            endpoint "GET" "/health" "Liveness probe for the APIM service process."
            endpoint "GET" "/readiness" "Readiness probe for background worker startup."
            endpoint "GET" "/ready" "Alias for /readiness."
            endpoint "GET" "/version" "Build, runtime, and process version metadata."
            endpoint "GET" "/capabilities" "Phase 1 service capabilities and integration contracts."
            endpoint "GET" "/acp/resources" "Lists ACP resources exposed by the local APIM service."
            endpoint "POST" "/acp/resources/read" "Reads an ACP resource by URI using the local auth envelope."
            endpoint "GET" "/acp/tools" "Lists ACP tools exposed by the local APIM service."
            endpoint "POST" "/acp/tools/call" "Invokes an ACP tool envelope."
            endpoint "GET" "/acp/prompts" "Lists ACP prompts exposed by the local APIM service."
            endpoint "POST" "/acp/prompts/get" "Gets an ACP prompt envelope."
            endpoint "GET" "/apim/routes" "Lists local APIM route metadata."
            endpoint "POST" "/apim/routes/invoke" "Invokes a local APIM route envelope."
        |]

    let private capability name status description =
        {| name = name; status = status; description = description |}

    let capabilities =
        [|
            capability "windows-service-lifetime" "implemented" "Host uses Microsoft.Extensions.Hosting.WindowsServices.UseWindowsService."
            capability "background-worker" "implemented" "BackgroundService heartbeat tracks readiness."
            capability "structured-logging" "implemented" "Console logging is configured with JSON records."
            capability "local-http-probes" "implemented" "Kestrel exposes health, readiness, version, and capabilities endpoints."
            capability "local-auth-envelope" "implemented" "Loopback-first API-key capable ACP/APIM envelope validation is available."
            capability "acp-contracts" "implemented" "ACP resource, tool, and prompt metadata is available over local endpoints."
            capability "apim-orchestration" "foundation" "Local APIM route metadata and safe route invocation envelopes are available; production backends remain explicit future bindings."
        |]

    let private acpResource uri name mime description =
        {| uri = uri; name = name; mimeType = mime; description = description |}

    let acpResources =
        [|
            acpResource "acp://capabilities" "Capabilities" "application/json" "Service, ACP, APIM, and integration capabilities."
            acpResource "acp://tools" "Tools" "application/json" "ACP tool catalog."
            acpResource "acp://resources" "Resources" "application/json" "ACP resource catalog."
            acpResource "acp://prompts" "Prompts" "application/json" "ACP prompt catalog."
            acpResource "acp://widgets" "Widgets" "application/json" "Widget and LiveTile capability catalog."
            acpResource "acp://apim/routes" "APIM Routes" "application/json" "Local APIM route metadata."
            acpResource "acp://mdkg/layers" "MDKG Layers" "application/json" "Yumlog metadata graph layer catalog."
        |]

    let private acpTool name description inputSchemaUri outputSchemaUri =
        {| name = name; description = description; inputSchemaUri = inputSchemaUri; outputSchemaUri = outputSchemaUri |}

    let acpTools =
        [|
            acpTool "yumlog.ocr.analyze" "Analyze an image with packaged Yumlog.Native Windows AI OCR." "urn:yumlog:acp:tool:yumlog.ocr.analyze:input:v1" "urn:yumlog:acp:tool:yumlog.ocr.analyze:output:v1"
            acpTool "yumlog.art.frame.read" "Read ART frame telemetry from a source frame or stream checkpoint." "urn:yumlog:acp:tool:yumlog.art.frame.read:input:v1" "urn:yumlog:acp:tool:yumlog.art.frame.read:output:v1"
            acpTool "yumlog.mdkg.write" "Write MDKG layer samples for OCR, affordances, semantic graph, APIM, ACP, ART, LiveTile, or Widget Studio metadata." "urn:yumlog:acp:tool:yumlog.mdkg.write:input:v1" "urn:yumlog:acp:tool:yumlog.mdkg.write:output:v1"
            acpTool "paperboy.bundle.pack" "Pack payloads into a manifest-backed Paperboy bundle." "urn:yumlog:acp:tool:paperboy.bundle.pack:input:v1" "urn:yumlog:acp:tool:paperboy.bundle.pack:output:v1"
            acpTool "paperboy.foundry.analyze" "Analyze payloads using a local Foundry-compatible model endpoint." "urn:yumlog:acp:tool:paperboy.foundry.analyze:input:v1" "urn:yumlog:acp:tool:paperboy.foundry.analyze:output:v1"
            acpTool "apim.route.invoke" "Invoke a registered local APIM route envelope." "urn:yumlog:acp:tool:apim.route.invoke:input:v1" "urn:yumlog:acp:tool:apim.route.invoke:output:v1"
            acpTool "livetile.widget.render" "Render an adaptive LiveTile widget preview payload." "urn:yumlog:acp:tool:livetile.widget.render:input:v1" "urn:yumlog:acp:tool:livetile.widget.render:output:v1"
            acpTool "widgetstudio.project.validate" "Validate a Widget Studio project schema and APIM/ACP bindings." "urn:yumlog:acp:tool:widgetstudio.project.validate:input:v1" "urn:yumlog:acp:tool:widgetstudio.project.validate:output:v1"
        |]

    let private acpPrompt name description arguments =
        {| name = name; description = description; arguments = arguments |}

    let acpPrompts =
        [|
            acpPrompt "yumlog.ocr.summarize" "Summarize OCR lines, words, confidence, and actionable UI context." [| "ocrJson" |]
            acpPrompt "paperboy.bundle.classify" "Classify a Paperboy bundle manifest and suggest tags, routes, and metadata." [| "manifestJson" |]
            acpPrompt "apim.route.design" "Design an APIM route, auth requirement, backend contract, and policy binding." [| "intent"; "backend" |]
            acpPrompt "livetile.widget.plan" "Plan an adaptive LiveTile widget state model and refresh cadence." [| "payloadDescription" |]
            acpPrompt "widgetstudio.project.review" "Review a Widget Studio project for schema, APIM, ACP, and MDKG readiness." [| "projectJson" |]
        |]

    let private apimRoute route method tool description authRequired =
        {| route = route; method = method; acpTool = tool; description = description; authRequired = authRequired |}

    let apimRoutes =
        [|
            apimRoute "/apim/routes/invoke" "POST" "apim.route.invoke" "Generic route invocation envelope." true
            apimRoute "/acp/tools/call" "POST" "apim.route.invoke" "ACP tool invocation route." true
            apimRoute "/acp/resources/read" "POST" "apim.route.invoke" "ACP resource read route." true
            apimRoute "/acp/prompts/get" "POST" "apim.route.invoke" "ACP prompt get route." true
        |]

    let mdkgLayers =
        [|
            {| id = 1; name = "timing"; schemaUri = "urn:yumlog:mdkg:layer:timing:v1" |}
            {| id = 2; name = "ocr-text"; schemaUri = "urn:yumlog:mdkg:layer:ocr-text:v1" |}
            {| id = 3; name = "ui-affordance"; schemaUri = "urn:yumlog:mdkg:layer:ui-affordance:v1" |}
            {| id = 4; name = "semantic-graph"; schemaUri = "urn:yumlog:mdkg:layer:semantic-graph:v1" |}
            {| id = 5; name = "input-event"; schemaUri = "urn:yumlog:mdkg:layer:input-event:v1" |}
            {| id = 6; name = "art-frame"; schemaUri = "urn:yumlog:mdkg:layer:art-frame:v1" |}
            {| id = 7; name = "acp-context"; schemaUri = "urn:yumlog:mdkg:layer:acp-context:v1" |}
            {| id = 8; name = "apim-trace"; schemaUri = "urn:yumlog:mdkg:layer:apim-trace:v1" |}
            {| id = 9; name = "live-tile"; schemaUri = "urn:yumlog:mdkg:layer:live-tile:v1" |}
            {| id = 10; name = "widget-studio"; schemaUri = "urn:yumlog:mdkg:layer:widget-studio:v1" |}
        |]

    let private contractComponent root name segments required description =
        let path = Paths.combine root segments
        {|
            name = name
            path = path
            exists = File.Exists(path) || Directory.Exists(path)
            required = required
            description = description
        |}

    let integrationContracts root =
        [|
            contractComponent root "Yumlog.Native CLI" [| "apps"; "Yumlog.Native"; "Yumlog.Native.fsproj" |] true "Read-only native capture/record/analyze CLI contract for future APIM operations."
            contractComponent root "Paperboy.Mcp stdio host" [| "apps"; "Paperboy.Mcp"; "Paperboy.Mcp.csproj" |] true "Read-only MCP and bundle contract for future agent delivery flows."
            contractComponent root "Launchers directory" [| "launchers" |] true "Windows Service install/start/stop entry points live here."
        |]

    let handoff =
        {|
            phase2 =
                [|
                    "Define APIM route catalog and request/response contracts."
                    "Add authenticated loopback-only command endpoints that call Yumlog.Native without blocking health probes."
                    "Decide whether APIM operations should be queued, streaming, or direct process invocations."
                |]
            phase8 =
                [|
                    "Add deployment packaging, signing, and service upgrade/rollback automation."
                    "Wire service state into installer logs and operational diagnostics."
                |]
            phase9 =
                [|
                    "Add security hardening: authN/Z, named-pipe or mTLS option, rate limits, and audit logs."
                    "Add end-to-end tests that validate service install, start, stop, and endpoint readiness on Windows."
                |]
        |}

module Program =
    let private configureJson (options: JsonOptions) =
        options.SerializerOptions.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        options.SerializerOptions.WriteIndented <- false

    let private configureLogging (logging: ILoggingBuilder) =
        logging.ClearProviders() |> ignore
        logging.AddJsonConsole(fun options ->
            options.IncludeScopes <- true
            options.TimestampFormat <- "yyyy-MM-ddTHH:mm:ss.fffZ") |> ignore

    let private readinessResult (state: ServiceRuntimeState) =
        let now = DateTimeOffset.UtcNow
        let ready = String.Equals(state.Status, "ready", StringComparison.OrdinalIgnoreCase)
        let response =
            {|
                status = if ready then "ready" else state.Status
                service = Metadata.serviceName
                utc = now
                worker = state.Snapshot()
                checks =
                    [|
                        {|
                            name = "background-worker"
                            status = if ready then "ready" else state.Status
                            description = "Hosted BackgroundService has signaled startup."
                        |}
                    |]
            |}

        if ready then
            Results.Ok(response)
        else
            Results.Json(response, statusCode = Nullable(StatusCodes.Status503ServiceUnavailable))

    let private headerValue (ctx: HttpContext) name =
        let value = ctx.Request.Headers[name].ToString()
        if String.IsNullOrWhiteSpace(value) then "" else value

    let private stringProperty name (json: JsonObject) =
        let mutable value: JsonNode = null
        if json.TryGetPropertyValue(name, &value) && not (isNull value) then
            try value.GetValue<string>() with _ -> ""
        else
            ""

    let private authTokenFromEnvelope (json: JsonObject) =
        let mutable value: JsonNode = null
        if json.TryGetPropertyValue("auth", &value) && not (isNull value) then
            match value with
            | :? JsonObject as auth -> stringProperty "token" auth
            | _ -> ""
        else
            ""

    let private configuredApiKey (configuration: IConfiguration) =
        let envKey = Environment.GetEnvironmentVariable("YUMLOG_APIM_API_KEY")
        if not (String.IsNullOrWhiteSpace(envKey)) then envKey else configuration["Auth:ApiKey"]

    let private isAuthorized (ctx: HttpContext) (configuration: IConfiguration) (envelope: JsonObject option) =
        let expected = configuredApiKey configuration
        if String.IsNullOrWhiteSpace(expected) then
            true
        else
            let headerKey = headerValue ctx "X-Yumlog-APIM-Key"
            let bearer =
                let authorization = headerValue ctx "Authorization"
                if authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
                    authorization.Substring("Bearer ".Length).Trim()
                else ""
            let envelopeToken =
                envelope
                |> Option.map authTokenFromEnvelope
                |> Option.defaultValue ""
            [| headerKey; bearer; envelopeToken |]
            |> Array.exists (fun candidate -> String.Equals(candidate, expected, StringComparison.Ordinal))

    let private unauthorized () =
        Results.Json(
            {| error = "unauthorized"; message = "A valid APIM API key is required." |},
            statusCode = Nullable(StatusCodes.Status401Unauthorized))

    let private readEnvelope (ctx: HttpContext) =
        task {
            use reader = new StreamReader(ctx.Request.Body)
            let! text = reader.ReadToEndAsync()
            if String.IsNullOrWhiteSpace(text) then
                return JsonObject()
            else
                match JsonNode.Parse(text) with
                | :? JsonObject as json -> return json
                | _ -> return JsonObject()
        }

    let private okEnvelope kind payload =
        Results.Ok(
            {|
                envelope =
                    {|
                        id = Guid.NewGuid().ToString("n")
                        kind = kind
                        acpVersion = Metadata.acpVersion
                        createdUtc = DateTimeOffset.UtcNow
                    |}
                payload = payload
            |})

    let private resourcePayload root uri =
        match uri with
        | "acp://capabilities" ->
            box {| capabilities = Responses.capabilities; routes = Responses.apimRoutes; authMode = "api-key-or-loopback-dev" |}
        | "acp://tools" -> box {| tools = Responses.acpTools |}
        | "acp://resources" -> box {| resources = Responses.acpResources |}
        | "acp://prompts" -> box {| prompts = Responses.acpPrompts |}
        | "acp://widgets" ->
            box {| widgets = [| {| id = "paperboy.livetile"; schemaUri = "urn:yumlog:widget:paperboy-livetile:v1"; status = "foundation" |} |] |}
        | "acp://apim/routes" -> box {| routes = Responses.apimRoutes |}
        | "acp://mdkg/layers" -> box {| layers = Responses.mdkgLayers |}
        | _ -> box {| error = "not_found"; uri = uri; yumlogRoot = root |}

    let private envelopeString name fallback (json: JsonObject) =
        let direct = stringProperty name json
        if not (String.IsNullOrWhiteSpace(direct)) then direct else fallback

    let private handleResourceRead (ctx: HttpContext) (configuration: IConfiguration) =
        task {
            let! envelope = readEnvelope ctx
            if not (isAuthorized ctx configuration (Some envelope)) then
                return unauthorized()
            else
                let uri = envelopeString "uri" "acp://capabilities" envelope
                let root = Paths.yumlogRoot configuration
                return okEnvelope "acp.resource.read.result" (resourcePayload root uri)
        }

    let private promptPayload name (arguments: JsonObject) =
        let arg key fallback =
            let value = stringProperty key arguments
            if String.IsNullOrWhiteSpace(value) then fallback else value
        match name with
        | "yumlog.ocr.summarize" ->
            let ocrJson = arg "ocrJson" "<ocrJson>"
            $"Summarize OCR text, confidence, and UI action candidates from: {ocrJson}"
        | "paperboy.bundle.classify" ->
            let manifestJson = arg "manifestJson" "<manifestJson>"
            $"Classify the Paperboy bundle manifest and propose tags/routes: {manifestJson}"
        | "apim.route.design" ->
            let intent = arg "intent" "<intent>"
            let backend = arg "backend" "<backend>"
            $"Design an APIM route for intent '{intent}' targeting backend '{backend}'. Include auth and policy notes."
        | "livetile.widget.plan" ->
            let payloadDescription = arg "payloadDescription" "<payloadDescription>"
            $"Plan an adaptive LiveTile widget for: {payloadDescription}"
        | "widgetstudio.project.review" ->
            let projectJson = arg "projectJson" "<projectJson>"
            $"Review this Widget Studio project for ACP/APIM/MDKG readiness: {projectJson}"
        | _ -> $"Unknown prompt: {name}"

    let private handlePromptGet (ctx: HttpContext) (configuration: IConfiguration) =
        task {
            let! envelope = readEnvelope ctx
            if not (isAuthorized ctx configuration (Some envelope)) then
                return unauthorized()
            else
                let name = envelopeString "name" "apim.route.design" envelope
                let arguments =
                    let mutable value: JsonNode = null
                    if envelope.TryGetPropertyValue("arguments", &value) && not (isNull value) then
                        match value with
                        | :? JsonObject as args -> args
                        | _ -> JsonObject()
                    else
                        JsonObject()
                let text = promptPayload name arguments
                return okEnvelope "acp.prompt.get.result" (box {| name = name; messages = [| {| role = "user"; content = text |} |] |})
        }

    let private handleToolCall (ctx: HttpContext) (configuration: IConfiguration) =
        task {
            let! envelope = readEnvelope ctx
            if not (isAuthorized ctx configuration (Some envelope)) then
                return unauthorized()
            else
                let name = envelopeString "name" "apim.route.invoke" envelope
                let route =
                    match name with
                    | "yumlog.ocr.analyze" -> "/apim/routes/invoke/yumlog.ocr.analyze"
                    | "yumlog.art.frame.read" -> "/apim/routes/invoke/yumlog.art.frame.read"
                    | "yumlog.mdkg.write" -> "/apim/routes/invoke/yumlog.mdkg.write"
                    | "paperboy.bundle.pack" -> "/apim/routes/invoke/paperboy.bundle.pack"
                    | "paperboy.foundry.analyze" -> "/apim/routes/invoke/paperboy.foundry.analyze"
                    | "livetile.widget.render" -> "/apim/routes/invoke/livetile.widget.render"
                    | "widgetstudio.project.validate" -> "/apim/routes/invoke/widgetstudio.project.validate"
                    | _ -> "/apim/routes/invoke/apim.route.invoke"
                return okEnvelope "acp.tool.call.result" (box {| name = name; route = route; status = "accepted"; execution = "local-envelope-validated"; forwarded = false |})
        }

    let private handleRouteInvoke (ctx: HttpContext) (configuration: IConfiguration) =
        task {
            let! envelope = readEnvelope ctx
            if not (isAuthorized ctx configuration (Some envelope)) then
                return unauthorized()
            else
                let route = envelopeString "route" "/apim/routes/invoke" envelope
                return okEnvelope "apim.route.invoke.result" (box {| route = route; status = "accepted"; auth = "validated"; backend = "local-foundation"; executed = false |})
        }

    let private mapEndpoints (app: WebApplication) =
        app.MapGet("/", Func<IResult>(fun () -> Results.Redirect("/capabilities"))) |> ignore

        app.MapGet(
            "/health",
            Func<ServiceRuntimeState, IResult>(fun state ->
                let now = DateTimeOffset.UtcNow
                Results.Ok(
                    {|
                        status = "healthy"
                        service = Metadata.serviceName
                        utc = now
                        uptimeSeconds = state.UptimeSeconds(now)
                        worker = state.Snapshot()
                    |})))
        |> ignore

        app.MapGet("/readiness", Func<ServiceRuntimeState, IResult>(readinessResult)) |> ignore
        app.MapGet("/ready", Func<ServiceRuntimeState, IResult>(readinessResult)) |> ignore

        app.MapGet(
            "/version",
            Func<IResult>(fun () ->
                Results.Ok(
                    {|
                        service = Metadata.serviceName
                        displayName = Metadata.displayName
                        version = Metadata.informationalVersion
                        assemblyVersion = Metadata.assemblyVersion
                        targetFramework = Metadata.targetFramework
                        frameworkDescription = RuntimeInformation.FrameworkDescription
                        osDescription = RuntimeInformation.OSDescription
                        processId = Environment.ProcessId
                        contentRoot = app.Environment.ContentRootPath
                        baseDirectory = AppContext.BaseDirectory
                    |})))
        |> ignore

        app.MapGet(
            "/capabilities",
            Func<IResult>(fun () ->
                let root = Paths.yumlogRoot app.Configuration
                Results.Ok(
                    {|
                        schemaVersion = Metadata.schemaVersion
                        apiVersion = Metadata.apiVersion
                        acpVersion = Metadata.acpVersion
                        service = Metadata.serviceName
                        status = "foundation"
                        yumlogRoot = root
                        endpoints = Responses.endpoints
                        capabilities = Responses.capabilities
                        acp =
                            {|
                                resources = Responses.acpResources
                                tools = Responses.acpTools
                                prompts = Responses.acpPrompts
                            |}
                        apimRoutes = Responses.apimRoutes
                        mdkgLayers = Responses.mdkgLayers
                        integrationContracts = Responses.integrationContracts root
                        handoff = Responses.handoff
                    |})))
        |> ignore

        app.MapGet("/acp/resources", Func<HttpContext, IConfiguration, IResult>(fun ctx configuration ->
            if isAuthorized ctx configuration None then okEnvelope "acp.resources.list" (box {| resources = Responses.acpResources |})
            else unauthorized())) |> ignore

        app.MapPost("/acp/resources/read", Func<HttpContext, IConfiguration, Task<IResult>>(handleResourceRead)) |> ignore

        app.MapGet("/acp/tools", Func<HttpContext, IConfiguration, IResult>(fun ctx configuration ->
            if isAuthorized ctx configuration None then okEnvelope "acp.tools.list" (box {| tools = Responses.acpTools |})
            else unauthorized())) |> ignore

        app.MapPost("/acp/tools/call", Func<HttpContext, IConfiguration, Task<IResult>>(handleToolCall)) |> ignore

        app.MapGet("/acp/prompts", Func<HttpContext, IConfiguration, IResult>(fun ctx configuration ->
            if isAuthorized ctx configuration None then okEnvelope "acp.prompts.list" (box {| prompts = Responses.acpPrompts |})
            else unauthorized())) |> ignore

        app.MapPost("/acp/prompts/get", Func<HttpContext, IConfiguration, Task<IResult>>(handlePromptGet)) |> ignore

        app.MapGet("/apim/routes", Func<HttpContext, IConfiguration, IResult>(fun ctx configuration ->
            if isAuthorized ctx configuration None then okEnvelope "apim.routes.list" (box {| routes = Responses.apimRoutes |})
            else unauthorized())) |> ignore

        app.MapPost("/apim/routes/invoke", Func<HttpContext, IConfiguration, Task<IResult>>(handleRouteInvoke)) |> ignore

    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateBuilder(args)
        let configuredServiceName =
            match builder.Configuration["Service:Name"] with
            | value when not (String.IsNullOrWhiteSpace(value)) -> value
            | _ -> Metadata.serviceName

        builder.Host.UseWindowsService(Action<WindowsServiceLifetimeOptions>(fun options ->
            options.ServiceName <- configuredServiceName))
        |> ignore

        configureLogging builder.Logging

        builder.Services.Configure<JsonOptions>(Action<JsonOptions>(configureJson)) |> ignore
        builder.Services.AddSingleton<ServiceRuntimeState>() |> ignore
        builder.Services.AddHostedService<ApimFoundationWorker>() |> ignore

        let app = builder.Build()
        let logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(Metadata.serviceName)

        app.Lifetime.ApplicationStarted.Register(Action(fun () ->
            logger.LogInformation(
                "Yumlog APIM service started. Urls={Urls}; WindowsService={IsWindowsService}",
                String.Join(",", app.Urls),
                WindowsServiceHelpers.IsWindowsService())))
        |> ignore

        app.Lifetime.ApplicationStopping.Register(Action(fun () ->
            logger.LogInformation("Yumlog APIM service is stopping.")))
        |> ignore

        mapEndpoints app
        app.Run()
        0
