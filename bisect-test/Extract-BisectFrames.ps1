<#
.SYNOPSIS
    Extract frames from a video using binary bisection sampling via FFmpeg.
.DESCRIPTION
    Extracts frames at timestamps determined by recursive midpoint bisection:
    start, end, middle, quarter, three-quarter, etc.
    This prioritizes the most structurally informative frames first.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$VideoPath,

    [Parameter(Mandatory=$false)]
    [string]$OutDir = "./ffmpeg-frames",

    [Parameter(Mandatory=$false)]
    [int]$MaxDepth = 4,

    [Parameter(Mandatory=$false)]
    [string]$FFmpegPath = "E:\Yumlog\.tools\ffmpeg\bin\ffmpeg.exe"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

# Get video duration via ffmpeg stderr
$infoFile = Join-Path $env:TEMP "bisect_ffinfo.txt"
$argStr = "-i `"$VideoPath`""
Start-Process -FilePath $FFmpegPath -ArgumentList $argStr -NoNewWindow -Wait -PassThru `
    -RedirectStandardError $infoFile -RedirectStandardOutput "$env:TEMP\bisect_ffout.txt" | Out-Null

$infoText = Get-Content $infoFile -Raw
if ($infoText -match 'Duration:\s*(\d+):(\d+):(\d+)\.(\d+)') {
    $durationSec = [int]$Matches[1]*3600 + [int]$Matches[2]*60 + [int]$Matches[3] + [double]("0.$($Matches[4])")
} else {
    throw "Could not parse video duration from: $infoText"
}

Write-Host "Video duration: ${durationSec}s"
Write-Host "Bisection depth: $MaxDepth (produces $([math]::Pow(2, $MaxDepth) + 1) frames max)"

# Generate bisection timestamps
function Get-BisectTimestamps {
    param([double]$Start, [double]$End, [int]$Depth, [int]$CurrentDepth = 0)

    $results = @()
    if ($CurrentDepth -eq 0) {
        $results += @{ Time = $Start; Order = 0; Label = "start" }
        $results += @{ Time = $End;   Order = 1; Label = "end" }
    }

    $mid = ($Start + $End) / 2.0
    $results += @{ Time = $mid; Order = $CurrentDepth * 100 + 2; Label = "d${CurrentDepth}_mid" }

    if ($CurrentDepth -lt ($Depth - 1)) {
        $results += Get-BisectTimestamps -Start $Start -End $mid -Depth $Depth -CurrentDepth ($CurrentDepth + 1)
        $results += Get-BisectTimestamps -Start $mid -End $End -Depth $Depth -CurrentDepth ($CurrentDepth + 1)
    }

    return $results
}

$timestamps = Get-BisectTimestamps -Start 0.0 -End $durationSec -Depth $MaxDepth

# Deduplicate by rounding to 2 decimal places
$seen = @{}
$unique = @()
foreach ($ts in $timestamps) {
    $key = [math]::Round($ts.Time, 2)
    if (-not $seen.ContainsKey($key)) {
        $seen[$key] = $true
        $unique += $ts
    }
}

# Sort by extraction order (start/end first, then by bisection depth)
$sorted = $unique | Sort-Object { $_.Time }

Write-Host "Extracting $($sorted.Count) frames..."

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$frameIndex = 0
foreach ($ts in $sorted) {
    $timeSec = [math]::Round($ts.Time, 2)
    $padded = "{0:D3}" -f $frameIndex
    $outFile = Join-Path $OutDir "frame_${padded}_${timeSec}s.png"

    $extractArgs = "-y -ss $timeSec -i `"$VideoPath`" -frames:v 1 -q:v 2 `"$outFile`""
    $proc = Start-Process -FilePath $FFmpegPath -ArgumentList $extractArgs `
        -NoNewWindow -Wait -PassThru `
        -RedirectStandardError "$env:TEMP\bisect_extract_err.txt" `
        -RedirectStandardOutput "$env:TEMP\bisect_extract_out.txt"

    if ($proc.ExitCode -eq 0) {
        Write-Host "  [$padded] ${timeSec}s -> $(Split-Path $outFile -Leaf)"
    } else {
        Write-Warning "  [$padded] FAILED at ${timeSec}s"
    }
    $frameIndex++
}
$sw.Stop()

$frameCount = (Get-ChildItem $OutDir -Filter *.png).Count
$totalSize = (Get-ChildItem $OutDir -Filter *.png | Measure-Object Length -Sum).Sum
Write-Host "`n--- FFmpeg Bisection Results ---"
Write-Host "Frames extracted: $frameCount"
Write-Host "Total size: $([math]::Round($totalSize/1KB, 1)) KB"
Write-Host "Elapsed: $([math]::Round($sw.Elapsed.TotalSeconds, 2))s"
Write-Host "Avg per frame: $([math]::Round($sw.Elapsed.TotalMilliseconds / [math]::Max($frameCount,1), 0))ms"
