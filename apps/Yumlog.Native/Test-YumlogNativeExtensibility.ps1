param(
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$ProjectDir = Resolve-Path $PSScriptRoot
$Project = Join-Path $ProjectDir 'Yumlog.Native.fsproj'

dotnet build $Project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE." }

$Dll = Join-Path $ProjectDir "bin\$Configuration\net10.0-windows10.0.26100.0\win-x64\Yumlog.Native.dll"
if (-not (Test-Path $Dll)) {
    throw "Yumlog.Native.dll was not produced at $Dll"
}

$WorkDir = Join-Path $ProjectDir 'obj\ExtensibilityTests'
$ManifestDir = Join-Path $WorkDir 'extensions'
$BrokenDir = Join-Path $WorkDir 'broken'
Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $WorkDir, $ManifestDir, $BrokenDir | Out-Null

Copy-Item -LiteralPath (Join-Path $ProjectDir 'Samples\contoso-vision-pack.extension.json') -Destination $ManifestDir

'{ "name": "broken", ' | Set-Content -LiteralPath (Join-Path $BrokenDir 'bad-json.extension.json') -Encoding UTF8
'{ "name": "missing-id", "version": "0.1.0" }' | Set-Content -LiteralPath (Join-Path $BrokenDir 'missing-id.extension.json') -Encoding UTF8

$LegacyConfig = Join-Path $WorkDir 'legacy.native.json'
@'
{
  "capture": {
    "outDir": "./legacy-shots",
    "fps": 3,
    "durationSec": 1,
    "count": 0,
    "allScreens": true,
    "imageFormat": "png"
  }
}
'@ | Set-Content -LiteralPath $LegacyConfig -Encoding UTF8

$FsxPath = Join-Path $WorkDir 'ExtensibilityChecks.fsx'
$MdkgPath = Join-Path $WorkDir 'extensions.mdkg'
$EscapedDll = $Dll.Replace('"', '""')
$EscapedManifestDir = $ManifestDir.Replace('"', '""')
$EscapedBrokenDir = $BrokenDir.Replace('"', '""')
$EscapedLegacyConfig = $LegacyConfig.Replace('"', '""')
$EscapedMdkg = $MdkgPath.Replace('"', '""')

@"
#r @"$EscapedDll"

open System
open System.IO
open Yumlog.Native

let fail message = raise (InvalidOperationException(message))
let assertTrue message condition = if not condition then fail message
let assertEqual message expected actual = if not (Object.Equals(expected, actual)) then fail `$"{message}. Expected={expected}; Actual={actual}"

let manifestDir = @"$EscapedManifestDir"
let brokenDir = @"$EscapedBrokenDir"
let legacyConfigPath = @"$EscapedLegacyConfig"
let mdkgPath = @"$EscapedMdkg"

// Built-in catalog: every extensibility kind is represented and every descriptor validates.
let builtInOnly = { NativeConfig.defaults.Extensions with Enabled = false }
let builtIn = NativeExtensibility.catalog builtInOnly
assertEqual "built-in catalog should not load manifests" 0 builtIn.Manifests.Length

for kind in NativeExtensibility.Kind.all do
    assertTrue `$"built-in catalog is missing kind '{kind}'" (builtIn.Capabilities |> Array.exists (fun item -> item.Kind = kind))

builtIn.Capabilities
|> Array.iteri (fun index item -> Mdkg.Validation.validateExtensionCapability `$"capability[{index}]" item |> ignore)

let ocrModel =
    builtIn.Capabilities
    |> Array.find (fun item -> item.Id.Key = "windows-ai-ocr")
assertTrue "OCR model status must be a known status" ([ "available"; "unavailable" ] |> List.contains ocrModel.Status)

// External manifests merge with built-ins.
let mergedConfig = { NativeConfig.defaults.Extensions with Dir = manifestDir }
let merged = NativeExtensibility.catalog mergedConfig
assertEqual "manifest count" 1 merged.Manifests.Length
assertTrue "manifest source is recorded" (merged.Manifests[0].Source.EndsWith("contoso-vision-pack.extension.json"))
assertTrue "merged catalog keeps built-ins" (merged.Capabilities |> Array.exists (fun item -> item.Id.Key = "capture-screens"))
assertTrue "merged catalog adds manifest capabilities" (merged.Capabilities |> Array.exists (fun item -> item.Id.Key = "contoso-redact"))

// Query filters.
let toolQuery : ExtensionQuery = { Kind = "tool"; Name = "" }
let tools = NativeExtensibility.filter toolQuery merged
assertTrue "kind filter returns tools only" (tools.Capabilities |> Array.forall (fun item -> item.Kind = "tool"))
assertTrue "kind filter keeps manifest tools" (tools.Capabilities |> Array.exists (fun item -> item.Id.Key = "contoso-redact"))

let nameQuery : ExtensionQuery = { Kind = ""; Name = "capture" }
let named = NativeExtensibility.filter nameQuery merged
assertTrue "name filter matches capture-screens" (named.Capabilities |> Array.exists (fun item -> item.Id.Key = "capture-screens"))
assertTrue "name filter drops unrelated capabilities" (named.Capabilities |> Array.forall (fun item -> item.Id.Key.Contains("capture") || item.DisplayName.ToLowerInvariant().Contains("capture")))

// Invalid manifests are reported instead of throwing.
let brokenReport = NativeExtensibility.validate { NativeConfig.defaults.Extensions with Dir = brokenDir }
assertTrue "broken manifests must fail validation" (not brokenReport.IsValid)
assertEqual "broken manifests must not load" 0 brokenReport.ManifestCount
assertEqual "broken manifest issue count" 2 (brokenReport.Issues |> Array.filter (fun issue -> issue.Severity = "error") |> Array.length)

let missingDirReport = NativeExtensibility.validate { NativeConfig.defaults.Extensions with Dir = Path.Combine(brokenDir, "does-not-exist") }
assertTrue "missing extension directory is valid" missingDirReport.IsValid

// Legacy configs without an extensions section keep working.
let legacy = NativeConfig.load legacyConfigPath
assertEqual "legacy capture fps is preserved" 3 legacy.Capture.Fps
assertEqual "legacy record section is backfilled" NativeConfig.defaults.Record.Encoder legacy.Record.Encoder
assertEqual "legacy extensions dir is backfilled" NativeConfig.defaults.Extensions.Dir legacy.Extensions.Dir
assertTrue "legacy extensions are enabled by default" legacy.Extensions.Enabled

// MCP projection.
let mcp = NativeExtensibility.mcpManifest merged
assertEqual "mcp protocol version" "2024-11-05" mcp.ProtocolVersion
assertTrue "mcp exposes tools" (mcp.Tools.Length > 0)
assertTrue "mcp tool names are namespaced" (mcp.Tools |> Array.forall (fun tool -> tool.Name.StartsWith("yumlog.native.")))
assertTrue "mcp excludes connectors" (mcp.Tools |> Array.forall (fun tool -> not (tool.Name.Contains(".connector."))))
assertTrue "mcp includes the follow-me agent" (mcp.Tools |> Array.exists (fun tool -> tool.Name = "yumlog.native.agent.follow-me"))

// MDKG extension-catalog layer round trip.
if File.Exists(mdkgPath) then File.Delete(mdkgPath)
NativeExtensibility.writeMdkg mdkgPath merged |> ignore

do
    use reader = Mdkg.MdkgReader.Open(mdkgPath)
    let layerNames = reader.Layers |> Array.map (fun layer -> layer.Name) |> Set.ofArray
    assertTrue "extension-catalog layer must be registered" (layerNames.Contains "extension-catalog")

    for name in [ "timing"; "ocr-text"; "semantic-graph"; "widget-studio" ] do
        assertTrue `$"default layer '{name}' must still be registered" (layerNames.Contains name)

    let entry = reader.ReadLayer(Mdkg.LayerId.ExtensionCatalog) |> Array.exactlyOne
    let sample = reader.ReadExtensionCatalog(entry)
    assertEqual "round-trip host" merged.Host sample.Catalog.Host
    assertEqual "round-trip capability count" merged.Capabilities.Length sample.Catalog.Capabilities.Length
    assertEqual "round-trip manifest count" merged.Manifests.Length sample.Catalog.Manifests.Length

printfn "Extensibility catalog, manifest, filter, MCP, config, and MDKG checks passed."
"@ | Set-Content -LiteralPath $FsxPath -Encoding UTF8

dotnet fsi --exec $FsxPath
if ($LASTEXITCODE -ne 0) { throw "Extensibility checks failed with exit code $LASTEXITCODE." }

Write-Host 'Running CLI surface checks...'

$listOutput = dotnet $Dll extensions list --dir $ManifestDir
if ($LASTEXITCODE -ne 0) { throw "extensions list failed with exit code $LASTEXITCODE." }
$listJson = $listOutput | ConvertFrom-Json
if ($listJson.capabilities.Count -lt 1) { throw 'extensions list returned no capabilities.' }

$mcpOutput = dotnet $Dll extensions mcp --dir $ManifestDir
if ($LASTEXITCODE -ne 0) { throw "extensions mcp failed with exit code $LASTEXITCODE." }
$mcpJson = $mcpOutput | ConvertFrom-Json
if ($mcpJson.tools.Count -lt 1) { throw 'extensions mcp returned no tools.' }

dotnet $Dll extensions validate --dir $ManifestDir | Out-Null
if ($LASTEXITCODE -ne 0) { throw "extensions validate failed for $ManifestDir" }

dotnet $Dll extensions validate --dir $BrokenDir | Out-Null
if ($LASTEXITCODE -ne 1) { throw "extensions validate should return 1 for broken manifests, got $LASTEXITCODE." }

Write-Host 'Yumlog.Native extensibility checks passed.'
$global:LASTEXITCODE = 0
