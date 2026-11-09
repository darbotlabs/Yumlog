[CmdletBinding()]
param(
    [string]$PackagePath = "S:\Yumlog\dist\Yumlog.Native\DarbotLabs.Yumlog.Native_0.1.2.0_x64.msix",
    [string]$CertificatePath = "S:\Yumlog\dist\Yumlog.Native\DarbotLabs.Yumlog.Native.cer",
    [string]$LogPath = "S:\Yumlog\dist\Yumlog.Native\install-elevated.log",
    [switch]$Reinstall
)

$ErrorActionPreference = "Stop"
$launcherRoot = Split-Path -Parent $PSCommandPath
$elevatedScript = Join-Path $launcherRoot "Install-YumlogNativeMsixElevated.ps1"

if (-not (Test-Path -LiteralPath $elevatedScript)) {
    throw "Elevated installer not found: $elevatedScript"
}

$arguments = @(
    "-NoProfile",
    "-ExecutionPolicy", "Bypass",
    "-File", "`"$elevatedScript`"",
    "-PackagePath", "`"$PackagePath`"",
    "-CertificatePath", "`"$CertificatePath`"",
    "-LogPath", "`"$LogPath`""
)
if ($Reinstall) {
    $arguments += "-Reinstall"
}

$process = Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -Verb RunAs -Wait -PassThru

[PSCustomObject]@{
    ExitCode = $process.ExitCode
    LogPath  = $LogPath
}

if (Test-Path -LiteralPath $LogPath) {
    Get-Content -LiteralPath $LogPath -Tail 120
}

if ($process.ExitCode -ne 0) {
    throw "Elevated MSIX installer failed with exit code $($process.ExitCode). See $LogPath."
}
