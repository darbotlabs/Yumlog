[CmdletBinding()]
param(
    [string]$ServiceName = 'Yumlog.APIM.Service',
    [string]$HealthUrl = 'http://127.0.0.1:5118/health',
    [int]$TimeoutSec = 30,
    [switch]$SkipHealthCheck
)

$ErrorActionPreference = 'Stop'

$service = Get-Service -Name $ServiceName -ErrorAction Stop
if ($service.Status -ne 'Running') {
    Start-Service -Name $ServiceName
    $service.WaitForStatus('Running', [TimeSpan]::FromSeconds($TimeoutSec))
}

Write-Host "$ServiceName status: $((Get-Service -Name $ServiceName).Status)"

if ($SkipHealthCheck) {
    return
}

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSec)
do {
    try {
        $health = Invoke-RestMethod -Uri $HealthUrl -TimeoutSec 2
        Write-Host "Health: $($health.status) at $($health.utc)"
        return
    } catch {
        Start-Sleep -Seconds 1
    }
} while ([DateTimeOffset]::UtcNow -lt $deadline)

Write-Warning "Service started, but health endpoint did not respond before timeout: $HealthUrl"
