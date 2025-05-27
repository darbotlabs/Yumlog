. "$PSScriptRoot/Run-FFmpeg.ps1"
function Capture-Screens {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$false)]
        [int]$Fps = 1,
        [Parameter(Mandatory=$false)]
        [int]$DurationSec = 10,
        [Parameter(Mandatory=$false)]
        [string]$OutDir = "./screenshots"
    )
    $ErrorActionPreference = 'Stop'
    if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
    $timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $outPattern = Join-Path $OutDir "screenshot_${timestamp}_%03d.png"
    $args = @("-y", "-f", "gdigrab", "-framerate", $Fps, "-t", $DurationSec, "-i", "desktop", $outPattern)
    Run-FFmpeg -Arguments $args
    Write-Host "Screenshots saved to $OutDir"
}

# Only export if we're being imported as a module
if ($MyInvocation.Line -match 'Import-Module') {
    Export-ModuleMember -Function Capture-Screens
}
