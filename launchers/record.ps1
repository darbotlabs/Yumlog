. "$PSScriptRoot/../Skills/Record-Screen.ps1"
param(
    [int]$Fps = 30
    ,[int]$DurationSec = 10
    ,[string]$OutFile = "./screenrecord.mp4"
)
Record-Screen -Fps $Fps -DurationSec $DurationSec -OutFile $OutFile
