<#
.SYNOPSIS
    Captures a functionality and performance baseline for Yumlog.Native.

.DESCRIPTION
    Runs each record backend under identical conditions and records wall time,
    process CPU time, peak working set, output size, effective bitrate, frame
    count, dropped frames, and frame-pacing jitter derived from the .mdkg
    timing layer. Emits JSON so successive runs can be diffed.
#>
[CmdletBinding()]
param(
    [string]$Exe = "E:\Yumlog\apps\Yumlog.Native.Cli\bin\Debug\net10.0-windows10.0.26100.0\win-x64\Yumlog.Native.Cli.exe",
    [string]$OutDir = "$PSScriptRoot\artifacts",
    [int]$DurationSec = 10,
    [int]$Fps = 30
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$ffprobe = Get-ChildItem -Path E:\Yumlog\.tools -Recurse -Filter ffprobe.exe -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName

function Invoke-Measured {
    param([string]$Label, [string[]]$Arguments)

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Exe
    foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $proc = [System.Diagnostics.Process]::new()
    $proc.StartInfo = $psi

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    [void]$proc.Start()

    # PeakWorkingSet64 is unavailable once the process exits, so sample it live
    # on a background thread while the main thread drains the output pipes.
    $peakBytes = 0L
    $sampler = [powershell]::Create()
    [void]$sampler.AddScript({
            param($targetId)
            $peak = 0L
            while ($true) {
                try {
                    $p = [System.Diagnostics.Process]::GetProcessById($targetId)
                    if ($p.HasExited) { break }
                    $p.Refresh()
                    if ($p.WorkingSet64 -gt $peak) { $peak = $p.WorkingSet64 }
                }
                catch { break }
                Start-Sleep -Milliseconds 100
            }
            $peak
        }).AddArgument($proc.Id)
    $samplerHandle = $sampler.BeginInvoke()

    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()
    $sw.Stop()

    try { $peakBytes = [int64](@($sampler.EndInvoke($samplerHandle))[-1]) } catch { $peakBytes = 0L }
    $sampler.Dispose()

    [pscustomobject]@{
        Label    = $Label
        ExitCode = $proc.ExitCode
        WallMs   = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
        CpuMs    = [math]::Round($proc.TotalProcessorTime.TotalMilliseconds, 1)
        PeakWsMb = [math]::Round($peakBytes / 1MB, 1)
        Stdout   = $stdout
        Stderr   = $stderr
    }
}

function Get-PacingStats {
    <#
        Frame pacing comes from the timing layer's presentation timestamps.
        Comparing real intervals with the nominal interval exposes judder that
        a frame count alone hides.
    #>
    param([string]$MdkgPath, [int]$Fps)

    if (-not (Test-Path $MdkgPath)) { return $null }

    $json = & $Exe runbook --input $MdkgPath 2>$null | ConvertFrom-Json
    $nominalTicks = [math]::Round(10000000.0 / $Fps)

    $pts = @($json.steps | ForEach-Object { [int64]$_.ptsTicks })
    if ($pts.Count -lt 3) { return $null }

    $gaps = for ($i = 1; $i -lt $pts.Count; $i++) { $pts[$i] - $pts[$i - 1] }
    $stats = $gaps | Measure-Object -Average -Maximum -Minimum
    $jitter = $gaps | ForEach-Object { [math]::Abs($_ - $nominalTicks) }

    [pscustomobject]@{
        NominalIntervalMs = [math]::Round($nominalTicks / 10000.0, 3)
        MeanIntervalMs    = [math]::Round($stats.Average / 10000.0, 3)
        MinIntervalMs     = [math]::Round($stats.Minimum / 10000.0, 3)
        MaxIntervalMs     = [math]::Round($stats.Maximum / 10000.0, 3)
        MeanJitterMs      = [math]::Round((($jitter | Measure-Object -Average).Average) / 10000.0, 3)
        MaxJitterMs       = [math]::Round((($jitter | Measure-Object -Maximum).Maximum) / 10000.0, 3)
        SampleCount       = $pts.Count
    }
}

function Get-ProbeStats {
    param([string]$Mp4)

    if (-not $ffprobe -or -not (Test-Path $Mp4)) { return $null }

    $raw = & $ffprobe -v error -show_entries stream=codec_name,codec_tag_string,codec_type,width,height,pix_fmt `
        -show_entries format=duration,bit_rate,nb_streams -of json $Mp4 2>$null | ConvertFrom-Json

    $video = $raw.streams | Where-Object { $_.codec_type -eq 'video' } | Select-Object -First 1
    $data = $raw.streams | Where-Object { $_.codec_type -eq 'data' } | Select-Object -First 1

    [pscustomobject]@{
        Codec         = $video.codec_name
        Tag           = $video.codec_tag_string
        Width         = $video.width
        Height        = $video.height
        PixFmt        = $video.pix_fmt
        StreamCount   = $raw.format.nb_streams
        MetadataTrack = if ($data) { $data.codec_tag_string } else { "none" }
        DurationSec   = [math]::Round([double]$raw.format.duration, 3)
        BitrateMbps   = [math]::Round([double]$raw.format.bit_rate / 1MB, 2)
    }
}

$results = [System.Collections.Generic.List[object]]::new()

foreach ($backend in @('auto', 'ffmpeg')) {
    $mp4 = Join-Path $OutDir "baseline-$backend.mp4"
    $mdkg = [System.IO.Path]::ChangeExtension($mp4, '.mdkg')
    Remove-Item $mp4, $mdkg -ErrorAction SilentlyContinue

    Write-Host "Recording backend=$backend ..."
    $run = Invoke-Measured -Label "record/$backend" -Arguments @(
        'record', '--out', $mp4, '--duration', "$DurationSec", '--fps', "$Fps", '--backend', $backend)

    $reported = if ($run.ExitCode -eq 0) { $run.Stdout | ConvertFrom-Json } else { $null }
    $sizeMb = if (Test-Path $mp4) { [math]::Round((Get-Item $mp4).Length / 1MB, 2) } else { 0 }

    $results.Add([pscustomobject]@{
        Backend          = $backend
        ExitCode         = $run.ExitCode
        WallMs           = $run.WallMs
        CpuMs            = $run.CpuMs
        CpuPercentOfWall = if ($run.WallMs -gt 0) { [math]::Round(100.0 * $run.CpuMs / $run.WallMs, 1) } else { 0 }
        PeakWsMb         = $run.PeakWsMb
        FileSizeMb       = $sizeMb
        ReportedBackend  = $reported.backend
        Lossless         = $reported.lossless
        FrameCount       = $reported.frameCount
        DroppedFrames    = $reported.droppedFrameCount
        MetadataSamples  = $reported.metadataSampleCount
        Probe            = Get-ProbeStats -Mp4 $mp4
        Pacing           = Get-PacingStats -MdkgPath $mdkg -Fps $Fps
        Error            = if ($run.ExitCode -ne 0) { ($run.Stderr -split "`n" | Select-Object -First 3) -join ' ' } else { "" }
    })
}

$baseline = [pscustomobject]@{
    CapturedUtc = (Get-Date).ToUniversalTime().ToString('o')
    Machine     = @{
        Os   = [System.Environment]::OSVersion.VersionString
        Cpus = [System.Environment]::ProcessorCount
        Gpu  = (Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name) -join '; '
    }
    Settings    = @{ DurationSec = $DurationSec; Fps = $Fps }
    Runs        = $results
}

$jsonPath = Join-Path $OutDir 'baseline.json'
$baseline | ConvertTo-Json -Depth 8 | Set-Content $jsonPath
Write-Host "`nWrote $jsonPath"
$baseline
