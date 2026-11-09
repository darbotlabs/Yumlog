[CmdletBinding()]
param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\apps\Yumlog.APIM.Service\Yumlog.APIM.Service.fsproj'),
    [string]$BaseUrl = "http://127.0.0.1:5119",
    [string]$ApiKey = "local-roundtrip-key",
    [int]$TimeoutSec = 30,
    [string]$OutDir = (Join-Path $PSScriptRoot '..\Scratchpad\apim-roundtrip')
)

$ErrorActionPreference = "Stop"

$uri = [Uri]$BaseUrl
if (-not $uri.IsLoopback -or $uri.Scheme -ne 'http' -or $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or $uri.UserInfo) {
    throw 'BaseUrl must be an HTTP loopback origin, for example http://127.0.0.1:5119.'
}
$BaseUrl = $BaseUrl.TrimEnd('/')
if ([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners().Port -contains $uri.Port) {
    throw "Port $($uri.Port) is already in use; choose an unused loopback port."
}
if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    throw 'ApiKey must not be empty.'
}
$projectDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $ProjectPath).Path
$assemblyPath = Join-Path $projectDirectory 'bin\Release\net11.0-windows\Yumlog.APIM.Service.dll'
if (-not (Test-Path -LiteralPath $assemblyPath)) {
    throw "Build the APIM project in Release before running this test: $ProjectPath"
}
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$artifactPath = Join-Path $OutDir "apim-roundtrip-$stamp.json"
$previousApiKey = $env:YUMLOG_APIM_API_KEY
$process = $null
try {
    $env:YUMLOG_APIM_API_KEY = $ApiKey
    # Launch the built host directly so the PID we stop is the actual server, not dotnet run.
    $process = Start-Process -FilePath "dotnet" -ArgumentList @("`"$assemblyPath`"", '--urls', $BaseUrl) -WorkingDirectory $projectDirectory -PassThru -WindowStyle Hidden
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSec)
    $health = $null
    do {
        try {
            $health = Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec 2
            break
        } catch {
            Start-Sleep -Milliseconds 500
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    if (-not $health) {
        throw "APIM service did not become healthy at $BaseUrl/health within $TimeoutSec seconds."
    }
    $version = Invoke-RestMethod -Uri "$BaseUrl/version"
    if ($version.processId -ne $process.Id) {
        throw 'The responding APIM service is not the process started by this test.'
    }

    $headers = @{ "X-Yumlog-APIM-Key" = $ApiKey }
    $capabilities = Invoke-RestMethod -Uri "$BaseUrl/capabilities" -Headers $headers
    $resources = Invoke-RestMethod -Uri "$BaseUrl/acp/resources" -Headers $headers
    $tools = Invoke-RestMethod -Uri "$BaseUrl/acp/tools" -Headers $headers
    $prompts = Invoke-RestMethod -Uri "$BaseUrl/acp/prompts" -Headers $headers
    $routes = Invoke-RestMethod -Uri "$BaseUrl/apim/routes" -Headers $headers

    $resourceReadBody = @{
        uri = "acp://capabilities"
        auth = @{ scheme = "api-key"; token = $ApiKey }
    } | ConvertTo-Json -Depth 10
    $resourceRead = Invoke-RestMethod -Method Post -Uri "$BaseUrl/acp/resources/read" -Headers $headers -ContentType "application/json" -Body $resourceReadBody

    $promptBody = @{
        name = "apim.route.design"
        arguments = @{ intent = "OCR APIM smoke"; backend = "Yumlog.Native" }
        auth = @{ scheme = "api-key"; token = $ApiKey }
    } | ConvertTo-Json -Depth 10
    $promptGet = Invoke-RestMethod -Method Post -Uri "$BaseUrl/acp/prompts/get" -Headers $headers -ContentType "application/json" -Body $promptBody

    $toolBody = @{
        name = "apim.route.invoke"
        arguments = @{ route = "/apim/routes/invoke"; payload = "roundtrip" }
        auth = @{ scheme = "api-key"; token = $ApiKey }
    } | ConvertTo-Json -Depth 10
    $toolCall = Invoke-RestMethod -Method Post -Uri "$BaseUrl/acp/tools/call" -Headers $headers -ContentType "application/json" -Body $toolBody

    $routeBody = @{
        route = "/apim/routes/invoke"
        method = "POST"
        arguments = @{ probe = "route" }
        auth = @{ scheme = "api-key"; token = $ApiKey }
    } | ConvertTo-Json -Depth 10
    $routeInvoke = Invoke-RestMethod -Method Post -Uri "$BaseUrl/apim/routes/invoke" -Headers $headers -ContentType "application/json" -Body $routeBody

    $unauthorizedPassed = $false
    try {
        Invoke-RestMethod -Uri "$BaseUrl/acp/resources" -Headers @{ "X-Yumlog-APIM-Key" = "wrong" } -TimeoutSec 2 | Out-Null
    } catch {
        if ($_.Exception.Response.StatusCode.value__ -eq 401) {
            $unauthorizedPassed = $true
        }
    }

    $result = [ordered]@{
        baseUrl = $BaseUrl
        serviceProcessId = $process.Id
        healthStatus = $health.status
        capabilitiesStatus = $capabilities.status
        resourceCount = @($resources.payload.resources).Count
        toolCount = @($tools.payload.tools).Count
        promptCount = @($prompts.payload.prompts).Count
        routeCount = @($routes.payload.routes).Count
        resourceReadKind = $resourceRead.envelope.kind
        promptText = $promptGet.payload.messages[0].content
        toolStatus = $toolCall.payload.status
        routeStatus = $routeInvoke.payload.status
        unauthorizedRejected = $unauthorizedPassed
        artifactPath = $artifactPath
    }

    $result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $artifactPath -Encoding UTF8
    if ($health.status -ne 'healthy' -or $capabilities.status -ne 'foundation' -or
        $result.resourceCount -ne 7 -or $result.toolCount -ne 8 -or $result.promptCount -ne 5 -or $result.routeCount -ne 4 -or
        $resourceRead.envelope.kind -ne 'acp.resource.read.result' -or $promptGet.envelope.kind -ne 'acp.prompt.get.result' -or
        $toolCall.payload.status -ne 'accepted' -or $toolCall.payload.forwarded -ne $false -or
        $routeInvoke.payload.status -ne 'accepted' -or $routeInvoke.payload.executed -ne $false -or -not $unauthorizedPassed) {
        throw "APIM round-trip assertions failed. See $artifactPath"
    }
    [PSCustomObject]$result
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    $env:YUMLOG_APIM_API_KEY = $previousApiKey
}
