. "$PSScriptRoot/Run-FFmpeg.ps1"
function Record-Screen {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$false)]
        [int]$Fps = 30,
        [Parameter(Mandatory=$false)]
        [int]$DurationSec = 10,
        [Parameter(Mandatory=$false)]
        [string]$OutFile = "./screenrecord.mp4"
    )
    $ErrorActionPreference = 'Stop'
    $args = @("-y", "-f", "gdigrab", "-framerate", $Fps, "-t", $DurationSec, "-i", "desktop", "-c:v", "libx264", "-preset", "ultrafast", $OutFile)
    Run-FFmpeg -Arguments $args
    Write-Host "Screen recording saved to $OutFile"
}

# Only export if we're being imported as a module
if ($MyInvocation.Line -match 'Import-Module') {
    Export-ModuleMember -Function Record-Screen
}
