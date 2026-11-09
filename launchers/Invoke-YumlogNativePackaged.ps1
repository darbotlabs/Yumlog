[CmdletBinding()]
param(
    [string[]]$YumlogArgs = @("--help"),
    [switch]$InstallLatest,
    [switch]$Reinstall
)

$ErrorActionPreference = "Stop"
$launcherRoot = Split-Path -Parent $PSCommandPath

if ($InstallLatest) {
    & (Join-Path $launcherRoot "Install-YumlogNativeMsix.ps1") -BuildIfMissing -Reinstall:$Reinstall | Out-Host
}

$aliasPath = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\Yumlog.Native.exe"
if (-not (Test-Path -LiteralPath $aliasPath)) {
    $command = Get-Command "Yumlog.Native.exe" -ErrorAction SilentlyContinue
    if ($command) {
        $aliasPath = $command.Source
    }
}

if (-not (Test-Path -LiteralPath $aliasPath)) {
    throw "Yumlog.Native.exe app execution alias was not found. Install the MSIX package and ensure app execution aliases are enabled."
}

& $aliasPath @YumlogArgs
exit $LASTEXITCODE
