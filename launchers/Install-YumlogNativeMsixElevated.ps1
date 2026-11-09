[CmdletBinding()]
param(
    [string]$PackagePath = "S:\Yumlog\dist\Yumlog.Native\DarbotLabs.Yumlog.Native_0.1.2.0_x64.msix",
    [string]$CertificatePath = "S:\Yumlog\dist\Yumlog.Native\DarbotLabs.Yumlog.Native.cer",
    [string]$LogPath = "S:\Yumlog\dist\Yumlog.Native\install-elevated.log",
    [switch]$Reinstall
)

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Path (Split-Path -Parent $LogPath) -Force | Out-Null
Start-Transcript -Path $LogPath -Force | Out-Null
try {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This script must run elevated so the MSIX signing certificate can be trusted machine-wide."
    }

    if (-not (Test-Path -LiteralPath $PackagePath)) {
        throw "MSIX package not found: $PackagePath"
    }
    if (-not (Test-Path -LiteralPath $CertificatePath)) {
        throw "Signing certificate not found: $CertificatePath"
    }

    certutil.exe -addstore TrustedPeople $CertificatePath
    if ($LASTEXITCODE -ne 0) {
        throw "certutil TrustedPeople import failed with exit code $LASTEXITCODE."
    }

    certutil.exe -addstore Root $CertificatePath
    if ($LASTEXITCODE -ne 0) {
        throw "certutil Root import failed with exit code $LASTEXITCODE."
    }

    if ($Reinstall) {
        Get-AppxPackage -Name "DarbotLabs.Yumlog.Native" -ErrorAction SilentlyContinue | Remove-AppxPackage
    }

    Add-AppxPackage -Path (Resolve-Path -LiteralPath $PackagePath).Path

    $package = Get-AppxPackage -Name "DarbotLabs.Yumlog.Native" -ErrorAction Stop
    $aliasPath = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\Yumlog.Native.exe"

    [PSCustomObject]@{
        Installed         = $true
        Name              = $package.Name
        Version           = $package.Version
        PackageFamilyName = $package.PackageFamilyName
        InstallLocation   = $package.InstallLocation
        AliasPath         = $aliasPath
        AliasExists       = Test-Path -LiteralPath $aliasPath
    } | Format-List
}
finally {
    Stop-Transcript | Out-Null
}
