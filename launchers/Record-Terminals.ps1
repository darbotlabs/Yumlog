<#
.SYNOPSIS
    Paperboy Terminal Recorder -- finds terminal windows, records them with
    FFmpeg, extracts bisection frames, and detects frame-to-frame changes.
.DESCRIPTION
    Uses WinAPI to locate terminal windows, Paperboy to generate extraction
    timestamps, and FFmpeg to record and extract. Produces a manifest JSON
    that the adaptive card viewer consumes.
.EXAMPLE
    .\Record-Terminals.ps1 -DurationSec 10 -Depth 4
    .\Record-Terminals.ps1 -WindowTitle "PowerShell" -DurationSec 15
#>
[CmdletBinding()]
param(
    [int]$DurationSec = 8,
    [int]$Depth = 4,
    [int]$Fps = 15,
    [string]$WindowTitle,
    [string]$OutDir = "E:\Yumlog\bisect-test\paperboy-session",
    [string]$FFmpegPath = "E:\Yumlog\.tools\ffmpeg\bin\ffmpeg.exe"
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\Skills\Paperboy.ps1"

# ── Step 1: Find terminal windows via WinAPI ──
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
public class WinFinder {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int count);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static List<Dictionary<string,object>> FindWindows(string filter) {
        var results = new List<Dictionary<string,object>>();
        EnumWindows((hWnd, lp) => {
            if (!IsWindowVisible(hWnd)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, 512);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;
            // Skip offscreen/minimized windows
            RECT r; GetWindowRect(hWnd, out r);
            if (r.Left < -10000) return true;
            // Filter
            if (!string.IsNullOrEmpty(filter) && title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                return true;
            uint pid; GetWindowThreadProcessId(hWnd, out pid);
            var d = new Dictionary<string,object>();
            d["title"] = title; d["left"] = r.Left; d["top"] = r.Top;
            d["right"] = r.Right; d["bottom"] = r.Bottom; d["pid"] = (int)pid;
            d["width"] = r.Right - r.Left; d["height"] = r.Bottom - r.Top;
            results.Add(d);
            return true;
        }, IntPtr.Zero);
        return results;
    }
}
"@

# Find terminals
$filter = if ($WindowTitle) { $WindowTitle } else { $null }
$allWindows = [WinFinder]::FindWindows($filter)

# If no filter, pick terminal-like windows
if (-not $WindowTitle) {
    $terminals = $allWindows | Where-Object {
        $t = $_.title
        $t -match 'Terminal|PowerShell|pwsh|cmd\.exe|Copilot|copilot|Clippy'
    }
} else {
    $terminals = $allWindows
}

if ($terminals.Count -eq 0) {
    Write-Warning "No matching windows found. Recording full primary monitor."
    $terminals = @(@{ title = "Desktop"; left = 0; top = 0; right = 1920; bottom = 1080; width = 1920; height = 1080; pid = 0 })
}

Write-Host "=== Paperboy Terminal Recorder ===" -ForegroundColor Cyan
Write-Host "Found $($terminals.Count) target window(s):"
foreach ($w in $terminals) {
    Write-Host "  $($w.title) | ($($w.left),$($w.top)) ${$w.width}x$($w.height)" -ForegroundColor DarkCyan
}

# ── Step 2: Setup output ──
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
New-Item -ItemType Directory -Path "$OutDir\frames" -Force | Out-Null

$manifest = @{
    timestamp    = (Get-Date -Format 'o')
    duration     = $DurationSec
    depth        = $Depth
    fps          = $Fps
    windows      = @()
}

# ── Step 3: Record each window ──
$winIndex = 0
foreach ($win in $terminals) {
    $winIndex++
    $title = $win.title
    $safeTitle = ($title -replace '[^\w\-]', '_').Substring(0, [math]::Min($title.Length, 40))
    $videoFile = "$OutDir\recording_${winIndex}_${safeTitle}.mp4"
    $frameDir = "$OutDir\frames\win${winIndex}"
    New-Item -ItemType Directory -Path $frameDir -Force | Out-Null

    # Ensure even dimensions
    $ox = [math]::Max(0, [int]$win.left)
    $oy = [math]::Max(0, [int]$win.top)
    $w = [int]$win.width - ([int]$win.width % 2)
    $h = [int]$win.height - ([int]$win.height % 2)

    if ($w -lt 100 -or $h -lt 100) {
        Write-Warning "Window '$title' too small (${w}x${h}), skipping."
        continue
    }

    Write-Host "`nRecording [$winIndex] '$title' for ${DurationSec}s..." -ForegroundColor Yellow
    Write-Host "  Region: offset=($ox,$oy) size=${w}x${h}"

    $recArgs = "-y -f gdigrab -framerate $Fps -offset_x $ox -offset_y $oy -video_size ${w}x${h} -t $DurationSec -i desktop -c:v libx264 -preset ultrafast `"$videoFile`""
    $proc = Start-Process -FilePath $FFmpegPath -ArgumentList $recArgs -NoNewWindow -Wait -PassThru `
        -RedirectStandardError "$env:TEMP\pb_rec_err.txt" -RedirectStandardOutput "$env:TEMP\pb_rec_out.txt"

    if ($proc.ExitCode -ne 0 -or -not (Test-Path $videoFile)) {
        Write-Warning "Recording failed for '$title'"
        continue
    }

    $videoSize = (Get-Item $videoFile).Length
    Write-Host "  Saved: $videoFile ($([math]::Round($videoSize/1KB)) KB)" -ForegroundColor Green

    # ── Step 4: Paperboy frame extraction ──
    $pb = New-Paperboy -Lo 0 -Hi $DurationSec
    $stops = $pb.EnumerateAll($Depth)
    Write-Host "  Extracting $($stops.Count) Paperboy frames (depth $Depth)..."

    $frameFiles = @()
    $extractSw = [System.Diagnostics.Stopwatch]::StartNew()

    foreach ($i in 0..($stops.Count - 1)) {
        $s = $stops[$i]
        $t = [math]::Round($s.Pos, 2)
        $pad = "{0:D3}" -f $i
        $addr = $s.Path
        $fName = "frame_${pad}_${t}s_${addr}.png"
        $fPath = Join-Path $frameDir $fName

        $extractArgs = "-y -ss $t -i `"$videoFile`" -frames:v 1 -q:v 2 `"$fPath`""
        Start-Process -FilePath $FFmpegPath -ArgumentList $extractArgs -NoNewWindow -Wait -PassThru `
            -RedirectStandardError "$env:TEMP\pb_ext_err.txt" -RedirectStandardOutput "$env:TEMP\pb_ext_out.txt" | Out-Null

        if (Test-Path $fPath) {
            $frameFiles += @{
                file    = "frames/win${winIndex}/$fName"
                time    = $t
                address = $addr
                depth   = $s.Depth
                index   = $i
                sizeKB  = [math]::Round((Get-Item $fPath).Length / 1KB, 1)
            }
        }
    }
    $extractSw.Stop()
    Write-Host "  Extracted $($frameFiles.Count) frames in $([math]::Round($extractSw.Elapsed.TotalSeconds, 1))s"

    # ── Step 5: Frame change detection ──
    # Compare file sizes as a fast proxy for visual change.
    # Large size delta between adjacent frames = likely content change.
    $changes = @()
    for ($i = 1; $i -lt $frameFiles.Count; $i++) {
        $prev = $frameFiles[$i - 1]
        $curr = $frameFiles[$i]
        $sizeDelta = [math]::Abs($curr.sizeKB - $prev.sizeKB)
        $pctChange = if ($prev.sizeKB -gt 0) { [math]::Round($sizeDelta / $prev.sizeKB * 100, 1) } else { 0 }
        if ($pctChange -gt 5) {
            $changes += @{
                fromIndex   = $i - 1
                toIndex     = $i
                fromTime    = $prev.time
                toTime      = $curr.time
                fromAddress = $prev.address
                toAddress   = $curr.address
                pctChange   = $pctChange
                type        = if ($pctChange -gt 30) { "major" } elseif ($pctChange -gt 15) { "moderate" } else { "minor" }
            }
        }
    }
    Write-Host "  Detected $($changes.Count) frame change(s)"

    $manifest.windows += @{
        index      = $winIndex
        title      = $title
        video      = "recording_${winIndex}_${safeTitle}.mp4"
        region     = @{ x = $ox; y = $oy; w = $w; h = $h }
        frames     = $frameFiles
        changes    = $changes
        frameCount = $frameFiles.Count
        videoSizeKB = [math]::Round($videoSize / 1KB, 1)
    }
}

# ── Step 6: Write manifest ──
$manifestPath = "$OutDir\manifest.json"
$manifest | ConvertTo-Json -Depth 10 | Set-Content $manifestPath -Encoding UTF8
Write-Host "`nManifest written to $manifestPath" -ForegroundColor Cyan
Write-Host "Total windows: $($manifest.windows.Count)" -ForegroundColor Cyan
Write-Host "Total frames:  $(($manifest.windows | ForEach-Object { $_.frameCount } | Measure-Object -Sum).Sum)" -ForegroundColor Cyan
Write-Host "Done." -ForegroundColor Green
