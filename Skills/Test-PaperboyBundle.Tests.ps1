# Pester tests for Paperboy bundle utilities.

BeforeAll {
    $script:testDir = Join-Path ([System.IO.Path]::GetTempPath()) ("paperboy-tests-" + [System.Guid]::NewGuid().ToString('N'))
    $script:bundlePath = Join-Path $script:testDir "sample.paperboy.zip"
    $script:unpackDir = Join-Path $script:testDir "unpacked"

    . "$PSScriptRoot\Paperboy.Bundle.ps1"

    New-Item -ItemType Directory -Path $script:testDir -Force | Out-Null
    Set-Content -Path (Join-Path $script:testDir "hello.txt") -Value "hello paperboy" -Encoding UTF8
}

AfterAll {
    Remove-Item -LiteralPath $script:testDir -Recurse -Force -ErrorAction SilentlyContinue
}

Describe "Paperboy bundle utilities" {
    It "Creates a bundle with a manifest" {
        $result = New-PaperboyBundle -Path (Join-Path $script:testDir "hello.txt") -OutFile $script:bundlePath -Force

        (Test-Path $script:bundlePath) | Should -Be $true
        $result.files | Should -Be 1
        $result.manifest.totalFiles | Should -Be 1
    }

    It "Lists a bundle manifest" {
        $manifest = Get-PaperboyBundleManifest -Path $script:bundlePath

        $manifest.totalFiles | Should -Be 1
        $manifest.entries[0].bundlePath | Should -Be "payload/hello.txt"
    }

    It "Expands a bundle payload" {
        $result = Expand-PaperboyBundle -Path $script:bundlePath -Destination $script:unpackDir -Force

        $result.files | Should -Be 1
        (Test-Path (Join-Path $script:unpackDir "payload\hello.txt")) | Should -Be $true
    }
}
