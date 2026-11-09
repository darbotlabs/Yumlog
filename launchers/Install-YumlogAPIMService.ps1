[CmdletBinding()]
param(
    [string]$ServiceName = 'Yumlog.APIM.Service',
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\apps\Yumlog.APIM.Service\Yumlog.APIM.Service.fsproj'),
    [string]$PublishDir = (Join-Path $PSScriptRoot '..\apps\Yumlog.APIM.Service\publish\service'),
    [string]$Urls = 'http://127.0.0.1:5118',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Install-YumlogAPIMService.ps1 must be run from an elevated PowerShell session.'
    }
}

Assert-Administrator

$ProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
$PublishDir = [IO.Path]::GetFullPath($PublishDir)
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null

dotnet publish $ProjectPath -c $Configuration -r win-x64 --self-contained false -o $PublishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exePath = Join-Path $PublishDir 'Yumlog.APIM.Service.exe'
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Expected service executable was not created: $exePath"
}

$binPath = "`"$exePath`" --urls $Urls"
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($existing) {
    & sc.exe config $ServiceName binPath= $binPath start= auto DisplayName= 'Yumlog APIM Service'
} else {
    & sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= 'Yumlog APIM Service'
}

if ($LASTEXITCODE -ne 0) {
    throw "sc.exe failed with exit code $LASTEXITCODE."
}

& sc.exe description $ServiceName 'Local Yumlog APIM Service foundation.'

if (-not $NoStart) {
    & (Join-Path $PSScriptRoot 'Start-YumlogAPIMService.ps1') -ServiceName $ServiceName
}

Write-Host "Yumlog APIM service installed: $ServiceName"
Write-Host "Executable: $exePath"
Write-Host "URLs: $Urls"
