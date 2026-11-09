[CmdletBinding()]
param(
    [string]$PackagePath,
    [string]$OutputRoot = "S:\Yumlog\dist\Yumlog.Native",
    [switch]$BuildIfMissing,
    [switch]$Reinstall
)

$ErrorActionPreference = "Stop"
$launcherRoot = Split-Path -Parent $PSCommandPath
$packageName = "DarbotLabs.Yumlog.Native"

if (-not $PackagePath) {
    $latest = Get-ChildItem -LiteralPath $OutputRoot -File -Filter "*.msix" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($latest) {
        $PackagePath = $latest.FullName
    }
}

if ((-not $PackagePath -or -not (Test-Path -LiteralPath $PackagePath)) -and $BuildIfMissing) {
    $build = & (Join-Path $launcherRoot "Build-YumlogNativeMsix.ps1")
    $PackagePath = $build.PackagePath
}

if (-not $PackagePath -or -not (Test-Path -LiteralPath $PackagePath)) {
    throw "MSIX package not found. Run Build-YumlogNativeMsix.ps1 first or pass -BuildIfMissing."
}

if ($Reinstall) {
    Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Remove-AppxPackage
}

Add-AppxPackage -Path (Resolve-Path -LiteralPath $PackagePath).Path

$package = Get-AppxPackage -Name $packageName -ErrorAction Stop
[PSCustomObject]@{
    Name              = $package.Name
    Version           = $package.Version
    PackageFamilyName = $package.PackageFamilyName
    InstallLocation   = $package.InstallLocation
    PackagePath       = (Resolve-Path -LiteralPath $PackagePath).Path
}
