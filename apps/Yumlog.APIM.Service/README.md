# Yumlog.APIM.Service

F# / ASP.NET Core Windows Service foundation for the Yumlog APIM Symphony.
This is a local HTTP contract scaffold, not an Azure API Management deployment
or a working proxy to OCR, Paperboy, Foundry, or Widget Studio backends.

## Prerequisites and console mode

Requires Windows and a .NET 11 SDK supporting `net11.0-windows` (currently
preview). Restore includes `Microsoft.Extensions.Hosting.WindowsServices`
`11.0.0-preview.5.26302.115`. Framework-dependent execution needs compatible
.NET 11 and ASP.NET Core runtimes; .NET 10 alone is insufficient.

Run from the repository root in PowerShell. Console mode requires no elevation
and does not install a Windows Service:

```powershell
dotnet build .\apps\Yumlog.APIM.Service\Yumlog.APIM.Service.fsproj -c Release
dotnet run --project .\apps\Yumlog.APIM.Service\Yumlog.APIM.Service.fsproj -c Release
```

Default URL: `http://127.0.0.1:5118`. Use `Yumlog__Root` / `Yumlog:Root` to
override repository-root discovery.

## Endpoints and authentication

Always public, even when an API key is configured:

- `GET /` redirects to `/capabilities`.
- `GET /health` checks process liveness.
- `GET /readiness` and `GET /ready` check worker startup, not backend availability.
- `GET /version` exposes build/runtime/process metadata and local paths.
- `GET /capabilities` exposes static catalogs and integration-file existence checks.

Key-protected when a key is configured:

- `GET /acp/resources`
- `POST /acp/resources/read`
- `GET /acp/tools`
- `POST /acp/tools/call`
- `GET /acp/prompts`
- `POST /acp/prompts/get`
- `GET /apim/routes`
- `POST /apim/routes/invoke`

The default listener binds to loopback; there is no separate remote-address
authorization check. Do not expose it on a non-loopback interface. With no
configured key, **all routes allow unauthenticated access**, not just probes.
`YUMLOG_APIM_API_KEY` takes precedence over `Auth:ApiKey` (also configurable
as `Auth__ApiKey`). When set, the ACP/APIM routes require an exact key match
through any one of these locations:

- `X-Yumlog-APIM-Key` header
- `Authorization: Bearer <api-key>` header
- JSON envelope body at `auth.token` (POST routes only)

Missing or incorrect keys return HTTP 401 on these routes. The optional
`auth.scheme` label is not validated. Public probes and `/capabilities` remain
public and may reveal local metadata; this is not production authN/Z.

Example POST body for `/acp/resources/read` (replace the development token
with the configured key):

```json
{
  "uri": "acp://capabilities",
  "auth": {
    "scheme": "api-key",
    "token": "local-roundtrip-key"
  }
}
```

## ACP contracts and scaffold limitations

Static resource payloads (not live backend reads):

- `acp://capabilities`
- `acp://tools`
- `acp://resources`
- `acp://prompts`
- `acp://widgets`
- `acp://apim/routes`
- `acp://mdkg/layers`

Advertised tool contracts (not executable backend integrations):

- `yumlog.ocr.analyze`
- `yumlog.art.frame.read`
- `yumlog.mdkg.write`
- `paperboy.bundle.pack`
- `paperboy.foundry.analyze`
- `apim.route.invoke`
- `livetile.widget.render`
- `widgetstudio.project.validate`

Prompt templates (text substitution only; no model invocation):

- `yumlog.ocr.summarize`
- `paperboy.bundle.classify`
- `apim.route.design`
- `livetile.widget.plan`
- `widgetstudio.project.review`

Tool calls return HTTP 200 with `status: "accepted"`,
`execution: "local-envelope-validated"`, and `forwarded: false`. Returned
`/apim/routes/invoke/<tool>` strings are metadata, not registered endpoints.
Route invocation returns `backend: "local-foundation"` and `executed: false`.
Neither handler executes or queues work, or validates backend argument schemas;
unknown tool/route names are not rejected. Unknown resource URIs return an
`error: "not_found"` payload inside a successful HTTP envelope. Unknown prompts
return an "Unknown prompt" message rather than HTTP 404.

## Round-trip verification

```powershell
dotnet build .\apps\Yumlog.APIM.Service\Yumlog.APIM.Service.fsproj -c Release
.\launchers\Test-YumlogAPIMRoundTrip.ps1
```

The script requires an unused loopback port (default `http://127.0.0.1:5119`),
starts the built Release DLL directly without installing a Windows Service,
and verifies the responding PID. It checks health, the resource/tool/prompt/
route catalogs, a resource read, prompt generation, tool/route scaffold
responses, and bad-key rejection. A timestamped JSON report is written under
the repository's `Scratchpad\apim-roundtrip\`.

`-BaseUrl`, `-ProjectPath`, `-OutDir`, `-ApiKey`, and `-TimeoutSec` override
defaults. Failed response assertions throw after writing the report. The test
stops only its own server process in `finally` and restores the previous
process API-key environment value. This verifies scaffold responses, not
backend functionality, Windows Service installation, or production security.

## Optional Windows Service administration

These administrative operations are **not** required for console development
or round-trip tests. Run from an elevated PowerShell session:

```powershell
.\launchers\Install-YumlogAPIMService.ps1
.\launchers\Start-YumlogAPIMService.ps1
.\launchers\Stop-YumlogAPIMService.ps1
.\launchers\Uninstall-YumlogAPIMService.ps1
```

Installation publishes framework-dependent `win-x64` output into
`apps\Yumlog.APIM.Service\publish\service`, registers automatic startup, and
starts the service unless `-NoStart` is supplied. Uninstall removes service
registration, not the published files. For a custom install URL, use
`-NoStart`, then start with the matching `-HealthUrl` for the health check.

## Remaining work

- Connect APIM route envelopes to concrete backends.
- Add signed packaging, upgrade/rollback, and installer diagnostics.
- Add durable authN/Z, audit logs, rate limits, and installed Windows Service E2E tests.
