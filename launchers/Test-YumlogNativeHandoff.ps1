param(
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Project = Join-Path $RepoRoot 'apps\Yumlog.Native\Yumlog.Native.fsproj'
dotnet build $Project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Native build failed: $LASTEXITCODE" }
$Dll = Join-Path $RepoRoot "apps\Yumlog.Native\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64\Yumlog.Native.dll"
$WorkDir = Join-Path $RepoRoot "apps\Yumlog.Native\obj\HandoffTests-$([guid]::NewGuid().ToString('N'))"
$InputDir = Join-Path $WorkDir 'input'
$Session = Join-Path $WorkDir 'session'
$script:Checks = 0

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Invoke-Native([string[]] $Arguments, [bool] $Success = $true) {
    $result = & dotnet $Dll @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if (($Success -and $exitCode -ne 0) -or (-not $Success -and $exitCode -eq 0)) {
        throw "Unexpected exit $exitCode for $($Arguments -join ' '): $result"
    }
    $script:Checks++
    if ($Success) { return ($result -join "`n") }
}

function Write-Bmp([string] $Path, [byte] $Blue = 0) {
    # A 1x1, 24-bit BMP with one padded BGR pixel; no capture or model is involved.
    $bytes = [byte[]]::new(58)
    $bytes[0] = 66; $bytes[1] = 77; $bytes[2] = 58
    $bytes[10] = 54; $bytes[14] = 40
    $bytes[18] = 1; $bytes[22] = 1; $bytes[26] = 1; $bytes[28] = 24
    $bytes[34] = 4; $bytes[54] = $Blue
    [IO.File]::WriteAllBytes($Path, $bytes)
}

try {
    New-Item -ItemType Directory -Path $InputDir -Force | Out-Null
    $first = Join-Path $InputDir 'a.bmp'
    Write-Bmp $first
    Write-Bmp (Join-Path $InputDir 'b.bmp') 255
    foreach ($mode in @('none', 'auto', 'windows-ai', 'raw-com')) {
        $readiness = (Invoke-Native @('readiness', '--ocr', $mode)) | ConvertFrom-Json
        Assert-True ($readiness.ready -is [bool]) 'Readiness must expose a boolean ready field.'
        Assert-True (-not [string]::IsNullOrWhiteSpace($readiness.code)) 'Readiness must expose a code.'
        Assert-True (-not [string]::IsNullOrWhiteSpace($readiness.message)) 'Readiness must explain status.'
        if ($mode -eq 'none') {
            Assert-True ($readiness.ready -and $readiness.code -ceq 'ocr-disabled') 'Disabled OCR must require no model.'
        }
    }
    Invoke-Native @('readiness') $false
    Invoke-Native @('readiness', '--ocr', 'invalid') $false
    Invoke-Native @('readiness', '--ocr', 'none', '--extra') $false
    $argsBase = @('handoff', '--input', $InputDir, '--out-dir', $Session, '--ocr', 'none')
    $manifest = (Invoke-Native $argsBase) | ConvertFrom-Json
    Assert-True ($manifest.schema -ceq 'urn:yumlog:handoff:v1') 'Wrong manifest schema.'
    Assert-True ($manifest.schemaVersion -ceq '1.0') 'Wrong schema version.'
    Assert-True ([guid]::Parse($manifest.sessionId) -ne [guid]::Empty) 'Missing session ID.'
    Assert-True ($manifest.producer.name -ceq 'Yumlog.Native') 'Wrong producer.'
    Assert-True ($manifest.analysisPath -ceq 'analysis.json' -and $manifest.mdkgPath -ceq 'session.mdkg') 'Wrong payload paths.'
    Assert-True ($manifest.files.Count -eq 4) 'Wrong file inventory.'
    Assert-True (($manifest.files.role -join ',') -ceq 'source,source,analysis,mdkg') 'Wrong file roles.'
    foreach ($file in $manifest.files) {
        $path = Join-Path $Session $file.path
        Assert-True ($file.sha256 -cmatch '^[0-9a-f]{64}$') 'Hash is not lowercase SHA256.'
        Assert-True ((Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $file.sha256) 'File hash mismatch.'
        Assert-True ((Get-Item $path).Length -eq $file.sizeBytes) 'File size mismatch.'
        Assert-True (-not [IO.Path]::IsPathRooted($file.path) -and $file.path -notmatch '\.\.') 'Unsafe file path.'
    }
    $analysis = Get-Content (Join-Path $Session 'analysis.json') -Raw | ConvertFrom-Json
    Assert-True ($analysis.Count -eq 2) 'Expected two analysis results.'
    Assert-True ($analysis[0].input -ceq 'frames/000001.bmp') 'Analysis must use portable paths.'
    Assert-True ($analysis[0].width -eq 1 -and $analysis[0].height -eq 1 -and $analysis[0].ocr.provider -ceq 'none') 'Wrong native analysis.'
    $legacy = (Invoke-Native @('analyze', '--input', $first, '--ocr', 'none')) | ConvertFrom-Json
    $legacy[0].input = $analysis[0].input
    Assert-True (($legacy[0] | ConvertTo-Json -Depth 20 -Compress) -ceq ($analysis[0] | ConvertTo-Json -Depth 20 -Compress)) 'Non-provisioning analysis changed legacy image results.'

    $before = @{}
    Get-ChildItem $Session -Recurse -File | ForEach-Object {
        $before[$_.FullName] = @((Get-FileHash $_.FullName).Hash, $_.LastWriteTimeUtc.Ticks)
    }
    $resumed = (Invoke-Native ($argsBase + '--resume')) | ConvertFrom-Json
    Assert-True ($resumed.sessionId -ceq $manifest.sessionId) 'Resume replaced session identity.'
    foreach ($path in $before.Keys) {
        Assert-True ((Get-FileHash $path).Hash -eq $before[$path][0] -and (Get-Item $path).LastWriteTimeUtc.Ticks -eq $before[$path][1]) 'Resume rewrote session.'
    }
    Invoke-Native $argsBase $false
    Invoke-Native @('handoff', '--input', $InputDir, '--out-dir', $Session, '--ocr', 'auto', '--resume') $false

    foreach ($relative in @($manifest.files.path) + @('handoff.json', 'handoff.sha256')) {
        $path = Join-Path $Session $relative
        $original = [IO.File]::ReadAllBytes($path)
        try {
            [IO.File]::AppendAllText($path, 'tamper')
            Invoke-Native ($argsBase + '--resume') $false
        } finally { [IO.File]::WriteAllBytes($path, $original) }
    }
    foreach ($relative in @('unexpected.txt', 'frames\unexpected.bmp')) {
        $extra = Join-Path $Session $relative
        [IO.File]::WriteAllText($extra, 'extra')
        try { Invoke-Native ($argsBase + '--resume') $false }
        finally { Remove-Item -LiteralPath $extra }
    }
    $missing = Join-Path $Session 'analysis.json'
    $saved = [IO.File]::ReadAllBytes($missing)
    Remove-Item $missing
    try { Invoke-Native ($argsBase + '--resume') $false }
    finally { [IO.File]::WriteAllBytes($missing, $saved) }

    Write-Bmp $first 128
    try { Invoke-Native ($argsBase + '--resume') $false }
    finally { Write-Bmp $first }
    $extraInput = Join-Path $InputDir 'c.bmp'
    Write-Bmp $extraInput
    try { Invoke-Native ($argsBase + '--resume') $false }
    finally { Remove-Item $extraInput }

    $manifestPath = Join-Path $Session 'handoff.json'
    $checksumPath = Join-Path $Session 'handoff.sha256'
    $savedManifest = [IO.File]::ReadAllBytes($manifestPath)
    $savedChecksum = [IO.File]::ReadAllBytes($checksumPath)
    try {
        $unsafe = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $unsafe.files[2].path = '../outside.json'
        [IO.File]::WriteAllText($manifestPath, ($unsafe | ConvertTo-Json -Depth 20))
        [IO.File]::WriteAllText($checksumPath, (Get-FileHash $manifestPath).Hash.ToLowerInvariant())
        Invoke-Native ($argsBase + '--resume') $false
    } finally {
        [IO.File]::WriteAllBytes($manifestPath, $savedManifest)
        [IO.File]::WriteAllBytes($checksumPath, $savedChecksum)
    }
    Invoke-Native @('handoff', '--input', $InputDir) $false
    Invoke-Native @('handoff', '--out-dir', $Session) $false
    Invoke-Native @('handoff', '--input', (Join-Path $WorkDir 'missing.bmp'), '--out-dir', (Join-Path $WorkDir 'missing-session')) $false
    Invoke-Native @('handoff', '--input', $InputDir, '--out-dir', (Join-Path $InputDir 'nested')) $false
    Invoke-Native @('handoff', '--input', $InputDir, '--out-dir', $first) $false
    Invoke-Native ($argsBase + @('--ocr', 'none')) $false
    Invoke-Native ($argsBase + '--unknown') $false
    Invoke-Native ($argsBase + @('--resume', '--resume')) $false
    Invoke-Native @('handoff', '--input', $InputDir, '--out-dir', (Join-Path $WorkDir 'bad-mode'), '--ocr', 'bad') $false

    $junction = Join-Path $WorkDir 'linked-input'
    New-Item -ItemType Junction -Path $junction -Target $InputDir | Out-Null
    try {
        Invoke-Native @('handoff', '--input', $junction, '--out-dir', (Join-Path $WorkDir 'linked-session')) $false
        Invoke-Native @('handoff', '--input', $InputDir, '--out-dir', (Join-Path $junction 'output')) $false
    } finally { [IO.Directory]::Delete($junction) }

    $invalidImage = Join-Path $WorkDir 'invalid.bmp'
    [IO.File]::WriteAllText($invalidImage, 'not an image')
    $invalidTarget = Join-Path $WorkDir 'invalid-session'
    Invoke-Native @('handoff', '--input', $invalidImage, '--out-dir', $invalidTarget) $false
    Assert-True (-not (Test-Path $invalidTarget)) 'Failed analysis published a partial session.'

    $single = Join-Path $WorkDir 'single'
    $defaultManifest = (Invoke-Native @('handoff', '--input', $first, '--out-dir', $single)) | ConvertFrom-Json
    Assert-True ($defaultManifest.options.ocrMode -ceq 'none' -and $defaultManifest.files.Count -eq 3) 'Single image/default OCR failed.'
    $readerScript = Join-Path $WorkDir 'ReadHandoff.fsx'
    @"
#r @"$($Dll.Replace('"', '""'))"
open Yumlog.Native.Mdkg
let check path count =
    use reader = MdkgReader.Open(path)
    let timing = reader.ReadLayer(LayerId.Timing) |> Array.map reader.ReadTiming
    let ocr = reader.ReadLayer(LayerId.OcrText) |> Array.map reader.ReadOcrText
    let ui = reader.ReadLayer(LayerId.UiAffordance) |> Array.map reader.ReadUiAffordance
    if timing.Length <> count || ocr.Length <> count || ui.Length <> count then failwith "MDKG sample counts"
    timing |> Array.iteri (fun i sample ->
        if sample.FrameIndex <> i || sample.PtsTicks <> int64 i * MovieTimescale ||
           sample.DurationTicks <> MovieTimescale || sample.Fps <> 1 then failwith "MDKG synthetic timing")
    if ocr |> Array.exists (fun sample -> sample.Ocr.Provider <> "none") then failwith "MDKG OCR provider"
    if ui |> Array.exists (fun sample -> sample.Hints.Length <> 0) then failwith "MDKG empty UI hints"
check @"$($Session.Replace('"', '""'))\session.mdkg" 2
check @"$($single.Replace('"', '""'))\session.mdkg" 1
printfn "Actual MDKG reader verified handoff timing/OCR/UI layers."
"@ | Set-Content -LiteralPath $readerScript -Encoding utf8
    dotnet fsi --exec $readerScript
    if ($LASTEXITCODE -ne 0) { throw 'MDKG handoff read failed.' }
    Write-Host "Yumlog.Native handoff checks passed ($script:Checks CLI cases plus manifest/hash/analysis and actual MDKG reads)."
} finally {
    if (Test-Path $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
}
$global:LASTEXITCODE = 0
