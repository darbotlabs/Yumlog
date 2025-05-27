# launchers/install.ps1
param()
$ErrorActionPreference = 'Stop'

# Ensure .tools/ffmpeg/bin exists
$ffmpegDir = Join-Path $PSScriptRoot '../.tools/ffmpeg/bin'
if (-not (Test-Path $ffmpegDir)) {
    New-Item -ItemType Directory -Path $ffmpegDir -Force | Out-Null
}

# Check for ffmpeg in PATH
$ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
if (-not $ffmpeg) {
    Write-Host "FFmpeg not found in PATH. Downloading static build..."
    $ffmpegZip = Join-Path $ffmpegDir 'ffmpeg.zip'
    $ffmpegExe = Join-Path $ffmpegDir 'ffmpeg.exe'
    $ffmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
    Invoke-WebRequest -Uri $ffmpegUrl -OutFile $ffmpegZip
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ffmpegZip, $ffmpegDir)
    Remove-Item $ffmpegZip
    $exe = Get-ChildItem -Path $ffmpegDir -Recurse -Filter ffmpeg.exe | Select-Object -First 1
    if ($exe) {
        Copy-Item $exe.FullName $ffmpegExe -Force
    }
    Write-Host "FFmpeg downloaded to $ffmpegExe"
}

# Prepend .tools/ffmpeg/bin to PATH for this session
$env:PATH = "$ffmpegDir;" + $env:PATH

# Install npm dependencies if package.json exists
$pkg = Join-Path $PSScriptRoot '../package.json'
if (Test-Path $pkg) {
    Write-Host "Running npm install..."
    npm install
    Write-Host "npm install complete."
}

Write-Host "Install script complete."
