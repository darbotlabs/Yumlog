Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Capture-Screens {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$false)]
        [int]$Fps = 1,
        [Parameter(Mandatory=$false)]
        [int]$DurationSec = 1,
        [Parameter(Mandatory=$false)]
        [string]$OutDir = "./screenshots"
    )
    $ErrorActionPreference = 'Stop'
    if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

    $frameCount = [Math]::Max(1, $Fps * $DurationSec)
    $intervalMs = if ($Fps -gt 0) { [int](1000 / $Fps) } else { 1000 }
    $timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $resolvedDir = (Resolve-Path $OutDir).Path

    $allScreens = [System.Windows.Forms.Screen]::AllScreens
    $combined = [System.Windows.Forms.SystemInformation]::VirtualScreen

    for ($i = 1; $i -le $frameCount; $i++) {
        $bmp = New-Object System.Drawing.Bitmap($combined.Width, $combined.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($combined.Location, [System.Drawing.Point]::Empty, $combined.Size)
        $outFile = Join-Path $resolvedDir ("screenshot_{0}_{1:D3}.png" -f $timestamp, $i)
        $bmp.Save($outFile)
        $g.Dispose()
        $bmp.Dispose()
        if ($i -lt $frameCount) { Start-Sleep -Milliseconds $intervalMs }
    }

    Write-Host "Screenshots saved to $OutDir"
}

# Only export if we're being imported as a module
if ($MyInvocation.Line -match 'Import-Module') {
    Export-ModuleMember -Function Capture-Screens
}
