. "$PSScriptRoot/../Skills/Capture-Screens.ps1"
param(
    [int]$Fps = 1,
    [int]$DurationSec = 10,
    [string]$OutDir = "./screenshots"
)
Capture-Screens -Fps $Fps -DurationSec $DurationSec -OutDir $OutDir
