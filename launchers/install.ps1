# launchers/install.ps1
param()
$ErrorActionPreference = 'Stop'

# Ensure .tools/ffmpeg/bin exists
$ffmpegDir = Join-Path $PSScriptRoot '../.tools/ffmpeg/bin'
if (-not (Test-Path $ffmpegDir)) {
    New-Item -ItemType Directory -Path $ffmpegDir -Force | Out-Null
}

# Check for ffmpeg in PATH or local installation
$ffmpegExe = Join-Path $ffmpegDir 'ffmpeg.exe'
$ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue

if (-not $ffmpeg -and -not (Test-Path $ffmpegExe)) {
    Write-Host "FFmpeg not found. Downloading static build..."
    $ffmpegZip = Join-Path $ffmpegDir 'ffmpeg.zip'
    $ffmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
    Invoke-WebRequest -Uri $ffmpegUrl -OutFile $ffmpegZip
    
    # Extract to temp location first
    $tempExtract = Join-Path $ffmpegDir 'temp_extract'
    if (Test-Path $tempExtract) {
        Remove-Item $tempExtract -Recurse -Force
    }
    New-Item -ItemType Directory -Path $tempExtract -Force | Out-Null
    
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ffmpegZip, $tempExtract)
    Remove-Item $ffmpegZip
    
    # Find and copy ffmpeg.exe to the bin directory
    $exe = Get-ChildItem -Path $tempExtract -Recurse -Filter ffmpeg.exe | Select-Object -First 1
    if ($exe) {
        Copy-Item $exe.FullName $ffmpegExe -Force
        Write-Host "FFmpeg downloaded to $ffmpegExe"
    } else {
        Write-Host "Warning: Could not find ffmpeg.exe in downloaded package"
    }
    
    # Clean up temp extraction
    Remove-Item $tempExtract -Recurse -Force
} elseif (Test-Path $ffmpegExe) {
    Write-Host "FFmpeg already installed at $ffmpegExe"
} else {
    Write-Host "FFmpeg found in system PATH"
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
