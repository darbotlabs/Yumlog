[CmdletBinding()]
param(
    [string]$ServiceName = 'Yumlog.APIM.Service',
    [int]$TimeoutSec = 30,
    [switch]$IgnoreMissing
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Uninstall-YumlogAPIMService.ps1 must be run from an elevated PowerShell session.'
    }
}

Assert-Administrator

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    if ($IgnoreMissing) {
        Write-Host "Yumlog APIM service is not installed: $ServiceName"
        return
    }

    throw "Yumlog APIM service is not installed: $ServiceName"
}

if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds($TimeoutSec))
}

& sc.exe delete $ServiceName | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "sc.exe delete failed with exit code $LASTEXITCODE."
}

Write-Host "Yumlog APIM service uninstalled: $ServiceName"
