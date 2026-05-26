param(
    [Parameter(Position=0, Mandatory=$true)]
    [ValidateSet('pack', 'list', 'unpack', 'toss')]
    [string]$Action,

    [Parameter(Position=1)]
    [string[]]$Path = @(),

    [string]$OutFile = "",
    [string]$Destination = "",
    [string]$TossTo = "",

    [ValidateSet('Fastest', 'NoCompression', 'Optimal')]
    [string]$CompressionLevel = 'Optimal',

    [switch]$IncludeHidden,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\Skills\Paperboy.Bundle.ps1"

switch ($Action) {
    'pack' {
        if (-not $Path -or $Path.Count -eq 0) {
            throw "pack requires at least one -Path value."
        }
        New-PaperboyBundle -Path $Path -OutFile $OutFile -CompressionLevel $CompressionLevel -TossTo $TossTo -IncludeHidden:$IncludeHidden -Force:$Force
    }
    'list' {
        if (-not $Path -or $Path.Count -ne 1) {
            throw "list requires exactly one bundle path."
        }
        Get-PaperboyBundleManifest -Path $Path[0]
    }
    'unpack' {
        if (-not $Path -or $Path.Count -ne 1) {
            throw "unpack requires exactly one bundle path."
        }
        if (-not $Destination) {
            throw "unpack requires -Destination."
        }
        Expand-PaperboyBundle -Path $Path[0] -Destination $Destination -Force:$Force
    }
    'toss' {
        if (-not $Path -or $Path.Count -ne 1) {
            throw "toss requires exactly one bundle path."
        }
        if (-not $Destination) {
            throw "toss requires -Destination."
        }
        Toss-PaperboyBundle -Path $Path[0] -Destination $Destination -Force:$Force
    }
}
