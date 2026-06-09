param(
    [Parameter(Position=0, Mandatory=$true)]
    [ValidateSet('start','pause','stop','get','count','size','config','capture')]
    [string]$Action,
    [int]$Fps = 30,
    [int]$DurationSec = 10,
    [string]$OutDir = "./yumlogs",
    [string]$OutFile = "./yumlogs/yumlog.mp4"
)

Import-Module "$PSScriptRoot/../Skills/Record-Screen.ps1" -Force
Import-Module "$PSScriptRoot/../Skills/Capture-Screens.ps1" -Force

switch ($Action) {
    'capture' {
        $captureDir = if ($OutDir -eq "./yumlogs") { "./screenshots" } else { $OutDir }
        if (-not (Test-Path $captureDir)) { New-Item -ItemType Directory -Path $captureDir | Out-Null }
        $captureFps = if ($Fps -eq 30) { 1 } else { $Fps }
        Capture-Screens -Fps $captureFps -DurationSec $DurationSec -OutDir $captureDir
        $latest = Get-ChildItem $captureDir -Filter *.png | Sort-Object LastWriteTime -Desc | Select-Object -First 1
        if ($latest) { Write-Host "Latest: $($latest.FullName)" }
    }
    'start' {
        if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
        Record-Screen -Fps $Fps -DurationSec $DurationSec -OutFile $OutFile
    }
    'pause' {
        Write-Host "Pause not implemented (requires process tracking)."
    }
    'stop' {
        Write-Host "Stop not implemented (requires process tracking)."
    }
    'get' {
        $latest = Get-ChildItem $OutDir -Filter *.mp4 | Sort-Object LastWriteTime -Desc | Select-Object -First 1
        if ($latest) { $latest.FullName } else { Write-Host "No yumlog found." }
    }
    'count' {
        (Get-ChildItem $OutDir -Filter *.mp4 | Measure-Object).Count
    }
    'size' {
        $size = (Get-ChildItem $OutDir -Filter *.mp4 | Measure-Object Length -Sum).Sum
        if ($size) { '{0:N2} MB' -f ($size/1MB) } else { '0 MB' }
    }
    'config' {
        Get-Content "$PSScriptRoot/../config/tools.json"
    }
    default {
        Write-Host "Unknown action."
    }
}
