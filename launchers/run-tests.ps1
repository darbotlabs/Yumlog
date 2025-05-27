# Final version - Pester test launcher for darbot.yumlog
[CmdletBinding()]
param(
    [string] $Path = "$PSScriptRoot\..\Skills\Test-ScreenSkills.Tests.ps1",
    [ValidateSet('None','Minimal','Normal','Detailed','Diagnostic')]
    [string] $Verbosity = 'Detailed'
)

# Get absolute path
$absolutePath = Resolve-Path $Path -ErrorAction SilentlyContinue
if (-not $absolutePath) {
    Write-Error "Test file not found: $Path"
    exit 1
}

# Show test info
Write-Host "Running tests with Pester" -ForegroundColor Cyan
Write-Host "Test path: $absolutePath" -ForegroundColor Yellow

# Create command to run in regular PowerShell
$script = @"
# Set console color 
`$host.UI.RawUI.ForegroundColor = 'White'
`$host.UI.RawUI.BackgroundColor = 'Black'
Clear-Host

Write-Host "Executing Pester Tests..." -ForegroundColor Cyan

# Run the tests with Pester 3.4.0
`$results = Invoke-Pester -Script '$absolutePath' -PassThru

Write-Host ""
Write-Host "Test Results:" -ForegroundColor Yellow
Write-Host "  Passed: `$(`$results.PassedCount)" -ForegroundColor Green
Write-Host "  Failed: `$(`$results.FailedCount)" -ForegroundColor Red
Write-Host "  Skipped: `$(`$results.SkippedCount)" -ForegroundColor Gray
Write-Host "  Pending: `$(`$results.PendingCount)" -ForegroundColor Cyan
Write-Host "  Time: `$(`$results.Time) seconds" -ForegroundColor White

# Exit with appropriate code
if (`$results.FailedCount -gt 0) {
    exit 1
} else {
    exit 0
}
"@

# Create temp file
$tempFile = Join-Path $env:TEMP "pester_run_$([Guid]::NewGuid().ToString()).ps1"
Set-Content -Path $tempFile -Value $script -Force

# Execute with Windows PowerShell
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $tempFile

# Extract exit code
$testsPassed = $LASTEXITCODE -eq 0

# Clean up
Remove-Item $tempFile -Force -ErrorAction SilentlyContinue

# Return summary
if ($testsPassed) {
    Write-Host "`nTests completed successfully." -ForegroundColor Green
    exit 0
} else {
    Write-Host "`nTests failed." -ForegroundColor Red
    exit 1
}
