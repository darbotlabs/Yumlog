[CmdletBinding()]
param(
    [string]$ProjectPath = "S:\Yumlog\apps\Yumlog.Native\Yumlog.Native.fsproj",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("win-x64")]
    [string]$RuntimeIdentifier = "win-x64",
    [string]$OutputRoot = "S:\Yumlog\dist\Yumlog.Native",
    [switch]$NoSign,
    [switch]$ForceNewCertificate
)

$ErrorActionPreference = "Stop"

function Get-ProjectProperty {
    param([xml]$Project, [string]$Name)

    foreach ($group in $Project.Project.PropertyGroup) {
        $node = $group.ChildNodes | Where-Object {
            $_.NodeType -eq "Element" -and $_.Name -eq $Name
        } | Select-Object -First 1
        if ($node) {
            return $node.InnerText
        }
    }

    return $null
}

function Find-WindowsSdkTool {
    param([string]$ToolName)

    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "${env:ProgramFiles}\Windows Kits\10\bin"
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

    foreach ($root in $roots) {
        $tool = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\$ToolName" } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ($tool) {
            return $tool
        }
    }

    $command = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    throw "Could not find $ToolName. Install the Windows SDK packaging tools."
}

function Get-CodeSigningCertificate {
    param(
        [string]$Subject,
        [switch]$ForceNew
    )

    if (-not $ForceNew) {
        $existing = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1
        if ($existing) {
            return $existing
        }
    }

    return New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -FriendlyName "Yumlog Native MSIX Code Signing" `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy Exportable `
        -KeyUsage DigitalSignature `
        -HashAlgorithm SHA256
}

$projectFullPath = (Resolve-Path -LiteralPath $ProjectPath).Path
$projectDirectory = Split-Path -Parent $projectFullPath
$manifestSource = Join-Path $projectDirectory "Package.appxmanifest"

if (-not (Test-Path -LiteralPath $manifestSource)) {
    throw "Package manifest not found: $manifestSource"
}

[xml]$project = Get-Content -LiteralPath $projectFullPath -Raw
$targetFramework = Get-ProjectProperty -Project $project -Name "TargetFramework"
$assemblyName = Get-ProjectProperty -Project $project -Name "AssemblyName"
if (-not $assemblyName) {
    $assemblyName = [IO.Path]::GetFileNameWithoutExtension($projectFullPath)
}

[xml]$manifest = Get-Content -LiteralPath $manifestSource -Raw
$identity = $manifest.Package.Identity
$packageIdentityName = $identity.Name
$packageVersion = $identity.Version
$publisher = $identity.Publisher

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$publishDirectory = Join-Path $projectDirectory "bin\$Configuration\$targetFramework\$RuntimeIdentifier\publish"
$layoutDirectory = Join-Path $OutputRoot "layout"
$packageFileName = "{0}_{1}_{2}.msix" -f $packageIdentityName, $packageVersion, "x64"
$packagePath = Join-Path $OutputRoot $packageFileName

dotnet publish $projectFullPath `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:PublishSingleFile=false `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $publishDirectory)) {
    throw "Publish directory not found: $publishDirectory"
}

if (Test-Path -LiteralPath $layoutDirectory) {
    Remove-Item -LiteralPath $layoutDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $layoutDirectory -Force | Out-Null

Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $layoutDirectory -Recurse -Force
Copy-Item -LiteralPath $manifestSource -Destination (Join-Path $layoutDirectory "AppxManifest.xml") -Force

$assetsSource = Join-Path $projectDirectory "Assets"
if (Test-Path -LiteralPath $assetsSource) {
    $assetsDestination = Join-Path $layoutDirectory "Assets"
    New-Item -ItemType Directory -Path $assetsDestination -Force | Out-Null
    Copy-Item -Path (Join-Path $assetsSource "*") -Destination $assetsDestination -Recurse -Force
}

$exePath = Join-Path $layoutDirectory "$assemblyName.exe"
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Packaged executable not found in layout: $exePath"
}

$makeappx = Find-WindowsSdkTool -ToolName "makeappx.exe"
if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}
& $makeappx pack /d $layoutDirectory /p $packagePath /o
if ($LASTEXITCODE -ne 0) {
    throw "makeappx failed with exit code $LASTEXITCODE."
}

$certificateThumbprint = $null
if (-not $NoSign) {
    $signtool = Find-WindowsSdkTool -ToolName "signtool.exe"
    $certificate = Get-CodeSigningCertificate -Subject $publisher -ForceNew:$ForceNewCertificate
    $certificateThumbprint = $certificate.Thumbprint

    $certificatePath = Join-Path $OutputRoot "$packageIdentityName.cer"
    Export-Certificate -Cert $certificate -FilePath $certificatePath | Out-Null
    Import-Certificate -FilePath $certificatePath -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
    & certutil.exe -user -addstore Root $certificatePath | Out-Null

    & $signtool sign /fd SHA256 /sha1 $certificateThumbprint $packagePath
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed with exit code $LASTEXITCODE."
    }
}

[PSCustomObject]@{
    PackageIdentityName = $packageIdentityName
    Version             = $packageVersion
    Publisher           = $publisher
    PackagePath         = $packagePath
    LayoutDirectory     = $layoutDirectory
    PublishDirectory    = $publishDirectory
    Certificate         = $certificateThumbprint
}
