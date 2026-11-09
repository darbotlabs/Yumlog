[CmdletBinding()]
param(
    [string]$ServiceName = 'Yumlog.APIM.Service',
    [int]$TimeoutSec = 30
)

$ErrorActionPreference = 'Stop'

$service = Get-Service -Name $ServiceName -ErrorAction Stop
if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds($TimeoutSec))
}

Write-Host "$ServiceName status: $((Get-Service -Name $ServiceName).Status)"
