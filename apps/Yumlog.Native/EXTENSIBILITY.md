# Yumlog.Native extensibility foundation

Yumlog.Native publishes a single declarative **extension catalog** that describes everything the
app can offer to agents and hosts: models, agents, tools, skills, workflows, connectors, plugins,
extensions, MCP/API surfaces, and continuous AI loops. The catalog is built from two sources:

1. **Built-in capabilities** — native capture, record, analyze/OCR, follow, orchestration and
   MDKG APIs, plus preview descriptors for the separate ACP/APIM and Paperboy MCP hosts.
2. **Extension manifests** — JSON files dropped into the configured extensions directory.

Everything is data, so the same catalog can be printed as JSON, projected onto an MCP tool
manifest, or recorded into a `.mdkg` file as capability provenance.

This is **discovery metadata, not an extension execution engine**: loading a manifest does not
load an assembly, start a process/service, download a model, or invoke its targets. The MCP
projection is not a running MCP server or a complete JSON-RPC `tools/list` response; a host must
resolve schema URIs and implement dispatch. Sample Contoso targets are illustrative, not bundled.
Except for the OCR readiness probe, capability statuses are declarations, not dependency or
endpoint health checks. `follow-loop` describes the existing duration-bounded `follow` command:
it captures frames first, then analyzes them; it is not a background daemon.

## Build and runtime requirements

The F# project targets **`net10.0-windows10.0.26100.0` / `win-x64`**, uses Windows Forms,
and references **`Microsoft.WindowsAppSDK.AI` 2.2.3**. Build with the .NET 10 SDK (or a compatible
newer SDK) and restored Windows targeting packs. Framework-dependent output requires the x64
.NET 10 runtime **and Windows Desktop runtime**; the Windows App SDK payload does not replace
those .NET frameworks.

From the repository root, no installed app alias is needed for discovery:

```powershell
dotnet build .\apps\Yumlog.Native\Yumlog.Native.fsproj -c Debug
$native = '.\apps\Yumlog.Native\bin\Debug\net10.0-windows10.0.26100.0\win-x64\Yumlog.Native.dll'
dotnet $native extensions list --dir .\apps\Yumlog.Native\Samples
dotnet $native extensions validate --dir .\apps\Yumlog.Native\Samples
dotnet $native extensions mcp --no-external
```

### Package identity and OCR

`Package.appxmanifest` declares `DarbotLabs.Yumlog.Native` version `0.1.2.0`, x64,
`packagedClassicApp` / `mediumIL`, `runFullTrust`, `systemAIModels`, and the `Yumlog.Native.exe`
execution alias. Its OS declarations are `MinVersion=10.0.22621.0` and
`MaxVersionTested=10.0.26226.0`; those declarations alone do not guarantee AI availability.

Windows AI OCR additionally depends on supported OS/hardware/drivers, policy, model readiness
and package capability access. The explicit bootstrap requests **Windows App Runtime 2.2**;
unpackaged execution can fail runtime initialization or the `systemAIModels` identity gate.
Use the registered execution alias for packaged identity, not `dotnet` on a loose DLL.
`auto`, `windows-ai`, and `raw-com` all use the managed Windows AI recognizer; `raw-com` is
an alias, not a separate fallback. `auto` reports unavailable results rather than switching OCR
engines. Recognition may call `EnsureReadyAsync` and **download/provision the model** when
`NotReady`; `none` disables OCR. Catalog discovery only checks readiness and never calls
`EnsureReadyAsync`. Successful `analyze` command completion is not proof OCR was available:
inspect `ocr.isAvailable` and `ocr.message`.

Packaging/install helpers under `launchers` are manual, side-effecting operations, not tests:

| Helper | Behavior |
| --- | --- |
| `Build-YumlogNativeMsix.ps1` | Publishes self-contained .NET output, rebuilds `dist\Yumlog.Native\layout`, copies native `Assets`, and runs Windows SDK `makeappx`. By default it also creates/reuses a signing certificate, exports its public `.cer`, trusts it in CurrentUser `TrustedPeople` and `Root`, and runs `signtool`; `-NoSign` skips signing/trust changes. |
| `Install-YumlogNativeMsix.ps1` | Registers the chosen/latest MSIX with `Add-AppxPackage`; `-BuildIfMissing` may invoke the signing build, and `-Reinstall` first removes the existing package. |
| `Install-YumlogNativeMsixElevated.ps1` / `Start-YumlogNativeMsixElevatedInstall.ps1` | Elevated certificate trust and package installation, with transcript output; the latter launches a UAC elevation prompt. |
| `Invoke-YumlogNativePackaged.ps1` | Invokes the registered alias with `-YumlogArgs`; `-InstallLatest` may build/install, with optional `-Reinstall`. |
| `Test-YumlogNativeOcr.ps1` | Runs packaged `analyze` on an existing image and reports OCR availability; may provision the model and optionally install the package. |

These launchers currently default to `S:\Yumlog` packaging paths; the OCR helper also has
machine-specific image/output defaults. Pass explicit paths when using another checkout.
`Install-YumlogNativeMsix.ps1 -BuildIfMissing` calls the builder with its defaults, so build
explicitly and pass `-PackagePath` for a relocated checkout.
Required package source assets are `Assets\StoreLogo.png`, `Assets\Square44x44Logo.png`, and
`Assets\Square150x150Logo.png`. Keep them in source control; do not commit generated packages,
publish/layout directories, certificates, or test output.

The older project `/t:PackageMsix` target is a separate **unsigned, build-output-based** path:
it uses Paperboy.Desktop's `WindowsAppSDK128.png` / `WindowsAppSDK240.png` as logos and defaults
to the Windows SDK `10.0.26100.0` `makeappx.exe` path (override `YumlogMakeAppx` if necessary).
Those two legacy image inputs are absent from this checkout; that packaging target cannot
complete until its asset inputs are supplied or corrected. The new launcher uses the native
assets listed above instead. The older target is not the self-contained publish/sign/install
workflow above.

## Capability kinds

| Kind | Meaning | Built-in example |
| --- | --- | --- |
| `model` | Inference model available to the app | `windows-ai-ocr` |
| `agent` | Autonomous or semi-autonomous loop | `follow-me` |
| `tool` | Single invocable operation | `capture-screens`, `analyze-image`, `mdkg-write` |
| `skill` | Reusable in-process function | `ui-affordance-hints`, `frame-integrity-hash` |
| `workflow` | Multi-step plan | `orchestrate-plan` |
| `connector` | Outbound system link | `apim-service` |
| `plugin` | In-process add-on to a native pipeline | `mdkg-extension-layer` |
| `extension` | Extension host / manifest surface | `extension-host` |
| `mcp-server` | MCP host that serves tools | `paperboy-mcp` |
| `api` | HTTP/API surface | `acp-http` |
| `continuous-ai` | Always-on observation loop | `follow-loop` |

Each capability carries a `SemanticIdentifier`, provider, status (`available`, `preview`,
`unavailable`, `disabled`), an `ExtensionInvocation` (transport, target, arguments, input/output
schema URIs), tags, and `SemanticAttribute` metadata — the same shapes used by the MDKG and
ACP/APIM layers in `Domain.fs`.

## Configuration

`AppConfig` gained an optional `extensions` section. Config files written before this section
existed keep working: `NativeConfig.load` normalizes missing sections back to defaults.

```json
{
  "extensions": {
    "dir": "config/extensions",
    "enabled": true,
    "includeBuiltIn": true,
    "manifestPattern": "*.extension.json"
  }
}
```

* `dir` — directory scanned for manifests (relative paths resolve against the working directory).
* `enabled` — set to `false` to ignore external manifests entirely.
* `includeBuiltIn` — set to `false` to emit only manifest-provided capabilities.
* `manifestPattern` — glob used when scanning `dir`.

Missing/null whole config sections are backfilled, and empty extension directory/pattern strings
use defaults. This is not a deep merge of every property: if an `extensions` object is present,
explicitly set `enabled` and `includeBuiltIn` (omitted booleans deserialize as `false`).
Scanning is non-recursive and sorted by filename. A missing directory is valid and loads nothing.

## CLI

```text
Yumlog.Native extensions list [--config path] [--dir path] [--kind kind] [--name text] [--no-builtin] [--no-external]
Yumlog.Native extensions mcp [--config path] [--dir path] [--no-builtin] [--no-external]
Yumlog.Native extensions validate [--config path] [--dir path] [--pattern glob]
```

* `extensions list` (alias: `extensions`, `extensions show`) prints the resolved catalog as JSON,
  optionally filtered by exact case-insensitive `--kind` and by a case-insensitive `--name`
  substring of the capability key/display name. Filters leave the `manifests` array unchanged.
* `extensions mcp` prints an MCP-shaped manifest (`protocolVersion` `2024-11-05`, matching the
  Paperboy MCP host) containing only invocable kinds: tools, skills, agents, workflows, and
  continuous AI loops that are not unavailable/disabled.
* `extensions validate` loads every manifest, reports issues, and exits non-zero when any issue
  has severity `error` (exit 1 for an invalid report). It validates external manifests, not
  installed binaries or endpoint reachability. Duplicate `kind/key` declarations and unknown
  kinds are warnings; duplicates are not removed from the catalog.

All three accept `--pattern`, `--no-builtin`, and `--no-external`; the latter flags only disable
configured sources, not re-enable them. `--kind`/`--name` apply to list/show, not MCP projection.
`list`/`mcp` omit invalid files without returning their load issues; use `validate` to inspect
errors before consuming the catalog.

## Extension manifests

A manifest uses the `urn:yumlog:native:extension-manifest:v1` record shape. It requires a semantic
`id` and `name`; each capability needs its own semantic `id`, `kind`, and `displayName`.
Optional strings/arrays are normalized, including absent `capabilities` to an empty array,
version to `0.1.0`, publisher to `unknown`, and capability status to `available`.
Set **`"enabled": true` explicitly** to contribute capabilities: an omitted boolean is false.
Validation checks record structure and semantic values, not a remotely fetched JSON Schema,
target existence, transport support, or exhaustive status enums. Use the lowercase kinds,
statuses, and transports shown in the sample for interoperable MCP projection.

See [`Samples/contoso-vision-pack.extension.json`](Samples/contoso-vision-pack.extension.json) for
a complete example. To load it:

```powershell
# From the repository root, with the packaged execution alias already installed:
Yumlog.Native extensions list --dir .\apps\Yumlog.Native\Samples
```

Manifests with `"enabled": false` stay in the catalog's `manifests` array but contribute no
capabilities. Malformed/invalid file content surfaces as issues from `extensions validate`;
directory enumeration, access, and invalid path failures can still fail the command.

## MDKG layer 11: `extension-catalog`

`Mdkg.LayerId.ExtensionCatalog` (11) can record the resolved catalog inside a `.mdkg` file.
Callers must explicitly write it: capture, record, follow, and extension CLI commands do not
automatically emit `.mdkg` files, and there is **no `Yumlog.Native mdkg` CLI command**.
The advertised `mdkg-write` capability points to the in-process writer API.

```fsharp
use writer = Mdkg.MdkgWriter.Open("session.mdkg")
writer.AddDefaultLayers()
writer.WriteExtensionCatalog(Mdkg.Producers.extensionCatalogLayerSample 0L 0L catalog)
```

`NativeExtensibility.writeMdkg path catalog` does the same in one call. The layer is sparse and
flagged as an extension layer, so existing readers that do not know it keep working (unknown-layer
rule in [`hevc-swarm-out/MDKG-SPEC.md`](hevc-swarm-out/MDKG-SPEC.md)). The helper creates/truncates
the target file, registers all eleven layers, and writes one `KeyFrame | Sparse` catalog sample
at PTS/duration zero. It does not append to an existing recording or create timing samples.

## Programmatic API (`NativeExtensibility`)

| Function | Purpose |
| --- | --- |
| `builtInCapabilities ()` | Native capability descriptors (probes Windows AI OCR availability) |
| `loadManifests config` | Manifests plus load issues from the configured directory |
| `catalog config` | Full resolved catalog |
| `catalogOf manifests includeBuiltIn` | Catalog from already-loaded manifests |
| `filter query catalog` | Filter capabilities by kind and name |
| `validate config` | `ExtensionValidationReport` with issues |
| `mcpManifest catalog` | MCP tool manifest projection |
| `mdkgSample` / `writeMdkg` | MDKG layer 11 emission |

Constructors (`identifier`, `invocation`, `capability`, `manifest`) validate through
`Mdkg.Validation`, mirroring the `Mdkg.Producers` pattern.

## Tests

```powershell
# From the repository root:
.\apps\Yumlog.Native\Test-YumlogNativeExtensibility.ps1
.\launchers\Test-YumlogNativeMdkgRoundTrip.ps1
```

Mirrors `launchers/Test-YumlogNativeMdkgRoundTrip.ps1`: it builds the project, generates an F#
script against the built assembly, and checks kind coverage, manifest loading, invalid-manifest
reporting, legacy config normalization, filters, the MCP projection, and the MDKG round trip, then
smoke-tests the three CLI commands.

Both tests build and write deterministic fixtures below native `obj`; they do not capture,
record, install/register packages, alter certificate trust, or run model provisioning.
Extensibility discovery may probe Windows AI readiness. Test runners fail on build/F# script
failures, and the expected invalid-manifest CLI exit is consumed rather than reported as an
overall test failure.
