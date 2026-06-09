@echo off
setlocal

:: Resolve script directory
set "YUMLOG_ROOT=%~dp0"
set "YUMLOG_ROOT=%YUMLOG_ROOT:~0,-1%"

:: Auto-set FFMPEG_PATH if not already set
if not defined FFMPEG_PATH (
    if exist "%YUMLOG_ROOT%\.tools\ffmpeg\bin\ffmpeg.exe" (
        set "FFMPEG_PATH=%YUMLOG_ROOT%\.tools\ffmpeg\bin\ffmpeg.exe"
    )
)

:: Fast path: use snap.exe for capture if available
if /i "%~1"=="capture" (
    if exist "%YUMLOG_ROOT%\.tools\snap\snap.exe" (
        :: Shift past the 'capture' verb and forward remaining args
        shift
        "%YUMLOG_ROOT%\.tools\snap\snap.exe" %1 %2 %3 %4 %5 %6 %7 %8 %9
        goto :eof
    )
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%YUMLOG_ROOT%\launchers\yumlog.ps1" %*

endlocal
