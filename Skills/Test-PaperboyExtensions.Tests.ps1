# Pester tests for the Paperboy discoverable capability/extension contract.

BeforeDiscovery {
    $script:mcpExe = @(
        (Join-Path $PSScriptRoot '..\apps\Paperboy.Mcp\bin\Release\net11.0-windows\Paperboy.Mcp.exe'),
        (Join-Path $PSScriptRoot '..\apps\Paperboy.Mcp\bin\Debug\net11.0-windows\Paperboy.Mcp.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}

BeforeAll {
    $script:repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    $script:appRoot = Join-Path $script:repoRoot 'apps\Paperboy.Desktop'
    $script:extensionsRoot = Join-Path $script:appRoot 'Assets\Extensions'
    $script:schemaPath = Join-Path $script:extensionsRoot 'paperboy.extension.schema.json'
    $script:cardsPath = Join-Path $script:appRoot 'Assets\AgentCards\paperboy.cards.json'

    $script:extensionKinds = @('app', 'agent', 'tool', 'skill', 'workflow', 'connector', 'plugin', 'extension', 'mcp', 'api')
    $script:capabilityKinds = @('tool', 'skill', 'workflow', 'agent', 'connector', 'resource', 'prompt', 'api')
    $script:transports = @('process', 'http', 'mcp-stdio', 'manual')

    $script:manifestFiles = @(
        Get-ChildItem -Path $script:extensionsRoot -Filter '*.extension.json' -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notlike '*.schema.json' }
    )

    $script:mcpExe = @(
        (Join-Path $script:repoRoot 'apps\Paperboy.Mcp\bin\Release\net11.0-windows\Paperboy.Mcp.exe'),
        (Join-Path $script:repoRoot 'apps\Paperboy.Mcp\bin\Debug\net11.0-windows\Paperboy.Mcp.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    function script:Invoke-PaperboyMcp {
        param([Parameter(Mandatory)][object[]]$Requests)

        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $script:mcpExe
        $psi.UseShellExecute = $false
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true

        $proc = [System.Diagnostics.Process]::Start($psi)
        $enc = [System.Text.UTF8Encoding]::new($false)
        $stdin = $proc.StandardInput.BaseStream
        $stdout = $proc.StandardOutput.BaseStream
        $responses = @()

        try {
            foreach ($request in $Requests) {
                $json = $request | ConvertTo-Json -Depth 12 -Compress
                $body = $enc.GetBytes($json)
                $header = $enc.GetBytes("Content-Length: $($body.Length)`r`n`r`n")
                $stdin.Write($header, 0, $header.Length)
                $stdin.Write($body, 0, $body.Length)
                $stdin.Flush()

                $headerBytes = [System.Collections.Generic.List[byte]]::new()
                while ($true) {
                    $b = $stdout.ReadByte()
                    if ($b -lt 0) { throw 'Paperboy MCP server closed stdout unexpectedly.' }
                    $headerBytes.Add([byte]$b)
                    $n = $headerBytes.Count
                    if ($n -ge 4 -and $headerBytes[$n - 4] -eq 13 -and $headerBytes[$n - 3] -eq 10 -and $headerBytes[$n - 2] -eq 13 -and $headerBytes[$n - 1] -eq 10) { break }
                }

                $headerText = $enc.GetString($headerBytes.ToArray())
                if ($headerText -notmatch 'Content-Length:\s*(\d+)') { throw "Missing Content-Length header: $headerText" }

                $length = [int]$Matches[1]
                $buffer = New-Object byte[] $length
                $read = 0
                while ($read -lt $length) {
                    $count = $stdout.Read($buffer, $read, $length - $read)
                    if ($count -le 0) { break }
                    $read += $count
                }

                $responses += ($enc.GetString($buffer, 0, $read) | ConvertFrom-Json)
            }
        }
        finally {
            $stdin.Close()
            if (-not $proc.WaitForExit(5000)) { $proc.Kill() }
            $proc.Dispose()
        }

        return $responses
    }
}

Describe "Paperboy extension manifest schema" {
    It "Ships a parsable manifest schema" {
        (Test-Path $script:schemaPath) | Should -Be $true
        { Get-Content -LiteralPath $script:schemaPath -Raw | ConvertFrom-Json } | Should -Not -Throw
    }

    It "Declares the Paperboy extension contract" {
        $schema = Get-Content -LiteralPath $script:schemaPath -Raw | ConvertFrom-Json

        $schema.'$id' | Should -Be 'https://darbotlabs.github.io/yumlog/paperboy-extension.schema.json'
        $schema.required | Should -Contain 'schemaVersion'
        $schema.required | Should -Contain 'id'
        $schema.required | Should -Contain 'kind'
        $schema.properties.kind.enum | Should -Be $script:extensionKinds
        $schema.'$defs'.capability.properties.transport.enum | Should -Be $script:transports
    }
}

Describe "Paperboy extension manifests" {
    It "Ships at least one app-trusted manifest" {
        $script:manifestFiles.Count | Should -BeGreaterThan 0
    }

    It "Declares required package metadata" {
        foreach ($file in $script:manifestFiles) {
            $manifest = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json

            $manifest.schemaVersion | Should -Not -BeNullOrEmpty
            $manifest.id | Should -Match '^[A-Za-z0-9][A-Za-z0-9._-]*$'
            $manifest.name | Should -Not -BeNullOrEmpty
            $manifest.version | Should -Not -BeNullOrEmpty
            $script:extensionKinds | Should -Contain $manifest.kind
        }
    }

    It "Declares valid capability transports" {
        foreach ($file in $script:manifestFiles) {
            $manifest = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json

            foreach ($capability in @($manifest.capabilities)) {
                $capability.id | Should -Match '^[A-Za-z0-9][A-Za-z0-9._-]*$'
                $script:capabilityKinds | Should -Contain $capability.kind
                $script:transports | Should -Contain $capability.transport

                if ($capability.transport -eq 'process') {
                    $capability.command | Should -Not -BeNullOrEmpty
                }

                if ($capability.transport -eq 'http') {
                    [Uri]::IsWellFormedUriString($capability.url, [UriKind]::Absolute) | Should -Be $true
                }
            }
        }
    }

    It "Keeps capability ids unique across shipped manifests" {
        $ids = @()
        foreach ($file in $script:manifestFiles) {
            $manifest = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            $ids += @($manifest.capabilities | ForEach-Object { $_.id })
        }

        ($ids | Sort-Object -Unique).Count | Should -Be $ids.Count
    }
}

Describe "Paperboy agent-card catalog" {
    It "Publishes the paperboy.extensions card" {
        $cards = Get-Content -LiteralPath $script:cardsPath -Raw | ConvertFrom-Json
        $card = $cards | Where-Object { $_.id -eq 'paperboy.extensions' }

        $card | Should -Not -BeNullOrEmpty
        $card.role | Should -Be 'capability-registry'
        $card.actions | Should -Contain 'listCapabilities'
        $card.actions | Should -Contain 'invokeCapability'
    }
}

Describe "Paperboy MCP capability surface" {
    It "Advertises capability and extension tools" -Skip:(-not $script:mcpExe) {
        $responses = script:Invoke-PaperboyMcp @(
            @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'pester'; version = '1.0' } } },
            @{ jsonrpc = '2.0'; id = 2; method = 'tools/list'; params = @{} }
        )

        $toolNames = @($responses[1].result.tools | ForEach-Object { $_.name })
        $toolNames | Should -Contain 'paperboy.capabilities.list'
        $toolNames | Should -Contain 'paperboy.extensions.list'
        $toolNames | Should -Contain 'paperboy.extensions.describe'
        $toolNames | Should -Contain 'paperboy.extensions.invoke'

        # Existing tool contract must stay intact.
        $toolNames | Should -Contain 'paperboy.bundle.pack'
        $toolNames | Should -Contain 'paperboy.cards.list'

        $extensionMetadata = $responses[0].result.capabilities.experimental.paperboy.extensions
        $extensionMetadata.schemaVersion | Should -Be '1.0'
        $extensionMetadata.manifestPattern | Should -Be '*.extension.json'
        $extensionMetadata.externalInvocationAllowed | Should -Be $false
    }

    It "Exposes capability and extension resources" -Skip:(-not $script:mcpExe) {
        $responses = script:Invoke-PaperboyMcp @(
            @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'pester'; version = '1.0' } } },
            @{ jsonrpc = '2.0'; id = 2; method = 'resources/list'; params = @{} },
            @{ jsonrpc = '2.0'; id = 3; method = 'resources/read'; params = @{ uri = 'paperboy://extensions/schema' } }
        )

        $uris = @($responses[1].result.resources | ForEach-Object { $_.uri })
        $uris | Should -Contain 'paperboy://capabilities'
        $uris | Should -Contain 'paperboy://extensions'
        $uris | Should -Contain 'paperboy://extensions/schema'
        $uris | Should -Contain 'paperboy://cards'

        $responses[2].result.contents[0].mimeType | Should -Be 'application/schema+json'
        ($responses[2].result.contents[0].text | ConvertFrom-Json).'$id' | Should -Be 'https://darbotlabs.github.io/yumlog/paperboy-extension.schema.json'
    }

    It "Indexes shipped extension capabilities" -Skip:(-not $script:mcpExe) {
        $responses = script:Invoke-PaperboyMcp @(
            @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'pester'; version = '1.0' } } },
            @{ jsonrpc = '2.0'; id = 2; method = 'tools/call'; params = @{ name = 'paperboy.capabilities.list'; arguments = @{ includeBuiltIn = $false; refresh = $true } } },
            @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'paperboy.extensions.list'; arguments = @{} } }
        )

        $index = $responses[1].result.content[0].text | ConvertFrom-Json
        $index.schemaVersion | Should -Be '1.0'
        $index.count | Should -BeGreaterThan 0
        @($index.capabilities | ForEach-Object { $_.id }) | Should -Contain 'yumlog.apim.status'

        $extensions = $responses[2].result.content[0].text | ConvertFrom-Json
        @($extensions.extensions | ForEach-Object { $_.id }) | Should -Contain 'yumlog.local-bridges'
        @($extensions.diagnostics).Count | Should -Be 0
    }

    It "Refuses to invoke descriptive transports" -Skip:(-not $script:mcpExe) {
        $responses = script:Invoke-PaperboyMcp @(
            @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'pester'; version = '1.0' } } },
            @{ jsonrpc = '2.0'; id = 2; method = 'tools/call'; params = @{ name = 'paperboy.extensions.invoke'; arguments = @{ capabilityId = 'yumlog.paperboy.mcpStdio' } } },
            @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'paperboy.extensions.invoke'; arguments = @{ capabilityId = 'yumlog.native.ocr' } } }
        )

        $responses[1].error | Should -Not -BeNullOrEmpty
        $responses[1].error.message | Should -BeLike '*not invocable*'

        $responses[2].error | Should -Not -BeNullOrEmpty
        $responses[2].error.message | Should -BeLike "*requires argument 'imagePath'*"
    }

    It "Keeps paperboy.cards.list backward compatible" -Skip:(-not $script:mcpExe) {
        $responses = script:Invoke-PaperboyMcp @(
            @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'pester'; version = '1.0' } } },
            @{ jsonrpc = '2.0'; id = 2; method = 'tools/call'; params = @{ name = 'paperboy.cards.list'; arguments = @{} } },
            @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'paperboy.cards.list'; arguments = @{ includeExtensions = $true } } }
        )

        $defaultIds = @(($responses[1].result.content[0].text | ConvertFrom-Json) | ForEach-Object { $_.id })
        $mergedIds = @(($responses[2].result.content[0].text | ConvertFrom-Json) | ForEach-Object { $_.id })

        $defaultIds | Should -Contain 'paperboy.payload'
        $defaultIds | Should -Not -Contain 'paperboy.connector.yumlog-bridges'
        $mergedIds | Should -Contain 'paperboy.connector.yumlog-bridges'
        $mergedIds.Count | Should -BeGreaterThan $defaultIds.Count
    }
}
