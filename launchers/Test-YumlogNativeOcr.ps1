[CmdletBinding()]
param(
    [string]$ImagePath = "C:\Users\darbot\OneDrive - Cypherdyne\Pictures\Screenshots\Screenshot 2026-06-14 135425.png",
    [ValidateSet("auto", "windows-ai", "raw-com", "none")]
    [string]$OcrMode = "windows-ai",
    [string]$OutDir = "C:\Users\darbot\OneDrive - Cypherdyne\Documents\Microsoft Scout\yumlog_native_ocr",
    [switch]$InstallLatest,
    [switch]$Reinstall
)

$ErrorActionPreference = "Stop"
$launcherRoot = Split-Path -Parent $PSCommandPath

if (-not (Test-Path -LiteralPath $ImagePath)) {
    throw "Image not found: $ImagePath"
}

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$jsonOut = Join-Path $OutDir ("yumlog-native-ocr-{0:yyyyMMdd-HHmmss}.json" -f (Get-Date))

$arguments = @(
    "analyze",
    "--input", (Resolve-Path -LiteralPath $ImagePath).Path,
    "--ocr", $OcrMode,
    "--json-out", $jsonOut
)

$timer = [Diagnostics.Stopwatch]::StartNew()
& (Join-Path $launcherRoot "Invoke-YumlogNativePackaged.ps1") -YumlogArgs $arguments -InstallLatest:$InstallLatest -Reinstall:$Reinstall
$exitCode = $LASTEXITCODE
$timer.Stop()

if ($exitCode -ne 0) {
    throw "Yumlog.Native packaged OCR command failed with exit code $exitCode."
}

if (-not (Test-Path -LiteralPath $jsonOut)) {
    throw "Expected OCR output was not written: $jsonOut"
}

$result = (Get-Content -LiteralPath $jsonOut -Raw | ConvertFrom-Json)[0]
[PSCustomObject]@{
    Image       = $result.input
    Size        = "$($result.width)x$($result.height)"
    Provider    = $result.ocr.provider
    IsAvailable = $result.ocr.isAvailable
    Message     = $result.ocr.message
    Lines       = $result.ocr.lines.Count
    Seconds     = [math]::Round($timer.Elapsed.TotalSeconds, 3)
    JsonOut     = $jsonOut
}
