<#
.SYNOPSIS
    Paperboy bundle utilities for tossing lightweight file bundles.
.DESCRIPTION
    Creates .paperboy.zip archives with a manifest containing source paths,
    bundle paths, sizes, timestamps, and SHA-256 hashes. The bundle can be
    listed, expanded, or copied ("tossed") to a destination.
.EXAMPLE
    New-PaperboyBundle -Path .\Skills, .\README.md -OutFile .\dist\skills.paperboy.zip
.EXAMPLE
    Get-PaperboyBundleManifest -Path .\dist\skills.paperboy.zip
.EXAMPLE
    Expand-PaperboyBundle -Path .\dist\skills.paperboy.zip -Destination .\out
#>

function Get-PaperboyBundleItem {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [string[]]$Path,

        [switch]$IncludeHidden
    )

    $items = [System.Collections.Generic.List[System.IO.FileInfo]]::new()

    foreach ($inputPath in $Path) {
        if (-not (Test-Path -LiteralPath $inputPath)) {
            throw "Path not found: $inputPath"
        }

        $resolved = Resolve-Path -LiteralPath $inputPath
        foreach ($resolvedPath in $resolved) {
            $item = Get-Item -LiteralPath $resolvedPath.ProviderPath -Force:$IncludeHidden
            if ($item.PSIsContainer) {
                $children = Get-ChildItem -LiteralPath $item.FullName -File -Recurse -Force:$IncludeHidden
                foreach ($child in $children) {
                    $items.Add($child)
                }
            } else {
                $items.Add($item)
            }
        }
    }

    return $items | Sort-Object FullName -Unique
}

function Get-PaperboyRelativePath {
    param(
        [Parameter(Mandatory=$true)]
        [System.IO.FileInfo]$File,

        [Parameter(Mandatory=$true)]
        [string[]]$Roots
    )

    foreach ($root in $Roots) {
        $rootItem = Get-Item -LiteralPath $root -Force
        if ($rootItem.PSIsContainer) {
            $rootFull = $rootItem.FullName.TrimEnd('\')
            if ($File.FullName.StartsWith($rootFull + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
                $relative = $File.FullName.Substring($rootFull.Length).TrimStart('\')
                return Join-Path $rootItem.Name $relative
            }
        } elseif ($File.FullName -eq $rootItem.FullName) {
            return $rootItem.Name
        }
    }

    return $File.Name
}

function New-PaperboyBundle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [string[]]$Path,

        [string]$OutFile = "",

        [ValidateSet('Fastest', 'NoCompression', 'Optimal')]
        [string]$CompressionLevel = 'Optimal',

        [string]$TossTo = "",

        [switch]$IncludeHidden,

        [switch]$Force
    )

    $ErrorActionPreference = 'Stop'

    if (-not $OutFile) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $OutFile = Join-Path (Get-Location) "paperboy-$stamp.paperboy.zip"
    }

    $outFull = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutFile)
    if ((Test-Path -LiteralPath $outFull) -and -not $Force) {
        throw "Output already exists: $outFull. Use -Force to overwrite."
    }

    $outDir = Split-Path -Parent $outFull
    if ($outDir -and -not (Test-Path -LiteralPath $outDir)) {
        New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    }

    $rootPaths = foreach ($inputPath in $Path) {
        (Resolve-Path -LiteralPath $inputPath).ProviderPath
    }

    $files = @(Get-PaperboyBundleItem -Path $Path -IncludeHidden:$IncludeHidden)
    if ($files.Count -eq 0) {
        throw "No files found to bundle."
    }

    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("paperboy-" + [System.Guid]::NewGuid().ToString('N'))
    $payloadRoot = Join-Path $tempRoot 'payload'
    New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null

    try {
        $entries = [System.Collections.Generic.List[object]]::new()
        $usedBundlePaths = @{}
        $totalBytes = [int64]0

        foreach ($file in $files) {
            if ($file.FullName -eq $outFull) {
                continue
            }

            $relative = Get-PaperboyRelativePath -File $file -Roots $rootPaths
            $bundlePath = ($relative -replace '\\', '/')

            if ($usedBundlePaths.ContainsKey($bundlePath)) {
                $hashPrefix = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.Substring(0, 8).ToLowerInvariant()
                $name = [System.IO.Path]::GetFileNameWithoutExtension($bundlePath)
                $ext = [System.IO.Path]::GetExtension($bundlePath)
                $dir = [System.IO.Path]::GetDirectoryName($bundlePath)
                $bundlePath = if ($dir) { "$($dir -replace '\\','/')/$name-$hashPrefix$ext" } else { "$name-$hashPrefix$ext" }
            }

            $usedBundlePaths[$bundlePath] = $true
            $dest = Join-Path $payloadRoot ($bundlePath -replace '/', '\')
            $destDir = Split-Path -Parent $dest
            if (-not (Test-Path -LiteralPath $destDir)) {
                New-Item -ItemType Directory -Path $destDir -Force | Out-Null
            }

            Copy-Item -LiteralPath $file.FullName -Destination $dest -Force

            $hash = Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
            $totalBytes += $file.Length
            $entries.Add([ordered]@{
                bundlePath = "payload/$bundlePath"
                sourcePath = $file.FullName
                length = $file.Length
                lastWriteTimeUtc = $file.LastWriteTimeUtc.ToString('o')
                sha256 = $hash.Hash
            })
        }

        $manifest = [ordered]@{
            schema = 'https://darbotlabs.github.io/yumlog/paperboy-bundle/v1'
            id = "pb-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
            createdAtUtc = (Get-Date).ToUniversalTime().ToString('o')
            compressionLevel = $CompressionLevel
            totalFiles = $entries.Count
            totalBytes = $totalBytes
            entries = $entries
        }

        $manifestPath = Join-Path $tempRoot 'paperboy-manifest.json'
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

        if (Test-Path -LiteralPath $outFull) {
            Remove-Item -LiteralPath $outFull -Force
        }

        Compress-Archive -Path (Join-Path $tempRoot '*') -DestinationPath $outFull -CompressionLevel $CompressionLevel -Force

        $bundleInfo = Get-Item -LiteralPath $outFull
        $result = [ordered]@{
            bundle = $bundleInfo.FullName
            files = $entries.Count
            sourceBytes = $totalBytes
            bundleBytes = $bundleInfo.Length
            compressionRatio = if ($totalBytes -gt 0) { [math]::Round($bundleInfo.Length / $totalBytes, 4) } else { 0 }
            manifest = $manifest
            tossedTo = $null
        }

        if ($TossTo) {
            $result.tossedTo = Toss-PaperboyBundle -Path $bundleInfo.FullName -Destination $TossTo -Force:$Force
        }

        return [PSCustomObject]$result
    } finally {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Get-PaperboyBundleManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Bundle not found: $Path"
    }

    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("paperboy-list-" + [System.Guid]::NewGuid().ToString('N'))
    try {
        Expand-Archive -LiteralPath $Path -DestinationPath $tempRoot -Force
        $manifestPath = Join-Path $tempRoot 'paperboy-manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath)) {
            throw "Bundle does not contain paperboy-manifest.json: $Path"
        }
        return Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    } finally {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Expand-PaperboyBundle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [string]$Path,

        [Parameter(Mandatory=$true)]
        [string]$Destination,

        [switch]$Force
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Bundle not found: $Path"
    }

    if ((Test-Path -LiteralPath $Destination) -and -not $Force) {
        $existing = Get-ChildItem -LiteralPath $Destination -Force -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($existing) {
            throw "Destination is not empty: $Destination. Use -Force to overwrite."
        }
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Expand-Archive -LiteralPath $Path -DestinationPath $Destination -Force:$Force

    $manifest = Get-Content -LiteralPath (Join-Path $Destination 'paperboy-manifest.json') -Raw | ConvertFrom-Json
    return [PSCustomObject]@{
        destination = (Resolve-Path -LiteralPath $Destination).ProviderPath
        files = $manifest.totalFiles
        manifest = $manifest
    }
}

function Toss-PaperboyBundle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [string]$Path,

        [Parameter(Mandatory=$true)]
        [string]$Destination,

        [switch]$Force
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Bundle not found: $Path"
    }

    $source = Get-Item -LiteralPath $Path
    $destinationPath = $Destination

    if (Test-Path -LiteralPath $Destination) {
        $destItem = Get-Item -LiteralPath $Destination
        if ($destItem.PSIsContainer) {
            $destinationPath = Join-Path $destItem.FullName $source.Name
        }
    } elseif ($Destination.EndsWith('\') -or $Destination.EndsWith('/')) {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
        $destinationPath = Join-Path $Destination $source.Name
    } else {
        $parent = Split-Path -Parent $Destination
        if ($parent -and -not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
    }

    if ((Test-Path -LiteralPath $destinationPath) -and -not $Force) {
        throw "Destination already exists: $destinationPath. Use -Force to overwrite."
    }

    Copy-Item -LiteralPath $source.FullName -Destination $destinationPath -Force:$Force
    return (Resolve-Path -LiteralPath $destinationPath).ProviderPath
}

if ($MyInvocation.Line -match 'Import-Module') {
    Export-ModuleMember -Function New-PaperboyBundle, Get-PaperboyBundleManifest, Expand-PaperboyBundle, Toss-PaperboyBundle
}
