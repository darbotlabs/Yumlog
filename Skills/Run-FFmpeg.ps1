function Run-FFmpeg {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [string[]]$Arguments
    )
    $ffmpegPath = $env:FFMPEG_PATH
    if (-not $ffmpegPath) {
        # Auto-discover from .tools relative to this script
        $localFfmpeg = Join-Path $PSScriptRoot "..\\.tools\\ffmpeg\\bin\\ffmpeg.exe"
        if (Test-Path $localFfmpeg) {
            $ffmpegPath = (Resolve-Path $localFfmpeg).Path
        } else {
            $ffmpegPath = "ffmpeg" # Last resort: assume on PATH
        }
    }
    Write-Verbose "Invoking: $ffmpegPath $($Arguments -join ' ')" -Verbose:$($VerbosePreference -eq 'Continue')
    $process = Start-Process -FilePath $ffmpegPath -ArgumentList $Arguments -NoNewWindow -Wait -PassThru -RedirectStandardError ffmpeg_error.log -RedirectStandardOutput ffmpeg_output.log
    if ($process.ExitCode -ne 0) {
        $err = Get-Content ffmpeg_error.log
        throw "FFmpeg failed with exit code $($process.ExitCode): $err"
    }
}

# Only export if we're being imported as a module
if ($MyInvocation.Line -match 'Import-Module') {
    Export-ModuleMember -Function Run-FFmpeg
}
