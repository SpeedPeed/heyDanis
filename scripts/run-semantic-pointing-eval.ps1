param(
    [string]$FixtureDir = (Join-Path $PSScriptRoot "..\tests\Clicky.Tests\Fixtures\pointing"),
    [string]$OutDir = (Join-Path $env:APPDATA "Clicky\point-debug\semantic-eval")
)

$ErrorActionPreference = "Stop"

function Read-ClickySecrets {
    $path = Join-Path $env:APPDATA "Clicky\secrets.bin"
    if (-not (Test-Path $path)) {
        throw "Missing Clicky secrets file: $path"
    }

    Add-Type -AssemblyName System.Security
    $encrypted = [System.IO.File]::ReadAllBytes($path)
    $plain = [System.Security.Cryptography.ProtectedData]::Unprotect(
        $encrypted,
        $null,
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser)

    $json = [System.Text.Encoding]::UTF8.GetString($plain)
    return $json | ConvertFrom-Json
}

function Get-PointFromResponse($text) {
    if ($text -match "\[POINT:none\]") {
        return $null
    }

    if ($text -match "\[POINT:(\d+)\s*,\s*(\d+)") {
        return @{ x = [int]$Matches[1]; y = [int]$Matches[2] }
    }

    return $null
}

function Test-PointInPolygon($point, $polygon) {
    if ($null -eq $point -or $polygon.Count -lt 3) {
        return $false
    }

    $inside = $false
    for ($i = 0; $i -lt $polygon.Count; $i++) {
        $j = if ($i -eq 0) { $polygon.Count - 1 } else { $i - 1 }
        $pi = $polygon[$i]
        $pj = $polygon[$j]
        $crossesY = (($pi.y -gt $point.y) -ne ($pj.y -gt $point.y))
        if ($crossesY) {
            $xAtY = (($pj.x - $pi.x) * ($point.y - $pi.y) / ($pj.y - $pi.y)) + $pi.x
            if ($point.x -lt $xAtY) {
                $inside = -not $inside
            }
        }
    }

    return $inside
}

function Test-FixtureResult($fixture, $response) {
    $point = Get-PointFromResponse $response
    if ($null -eq $point) {
        $passes = $fixture.expectedOutcome -eq "PointOrNone" -or $fixture.expectedOutcome -eq "NoneRequired"
        return @{ passed = $passes; reason = if ($passes) { "no point allowed" } else { "point required but model returned none" }; point = $null }
    }

    foreach ($region in $fixture.distractorRegions) {
        if (Test-PointInPolygon $point $region.polygon) {
            return @{ passed = $false; reason = "point landed in distractor region '$($region.name)'"; point = $point }
        }
    }

    if ($fixture.expectedOutcome -eq "NoneRequired") {
        return @{ passed = $false; reason = "fixture requires no point"; point = $point }
    }

    foreach ($region in $fixture.allowedRegions) {
        if (Test-PointInPolygon $point $region.polygon) {
            return @{ passed = $true; reason = "point landed in allowed region '$($region.name)'"; point = $point }
        }
    }

    return @{ passed = $false; reason = "point missed all allowed regions"; point = $point }
}

function Invoke-AnthropicFixture($apiKey, $model, $fixture, $imagePath) {
    $bytes = [System.IO.File]::ReadAllBytes($imagePath)
    $base64 = [Convert]::ToBase64String($bytes)
    $body = @{
        model = $model
        max_tokens = 512
        messages = @(
            @{
                role = "user"
                content = @(
                    @{ type = "text"; text = "Return a brief answer ending with [POINT:x,y:label:screen1] or [POINT:none]. Target: $($fixture.targetText)" },
                    @{ type = "image"; source = @{ type = "base64"; media_type = "image/png"; data = $base64 } }
                )
            }
        )
    } | ConvertTo-Json -Depth 20

    $headers = @{
        "x-api-key" = $apiKey
        "anthropic-version" = "2023-06-01"
        "content-type" = "application/json"
    }

    $response = Invoke-RestMethod -Method Post -Uri "https://api.anthropic.com/v1/messages" -Headers $headers -Body $body
    return ($response.content | ForEach-Object { $_.text }) -join ""
}

function Invoke-ZaiFixture($apiKey, $model, $fixture, $imagePath) {
    $bytes = [System.IO.File]::ReadAllBytes($imagePath)
    $base64 = [Convert]::ToBase64String($bytes)
    $body = @{
        model = $model
        max_tokens = 512
        messages = @(
            @{
                role = "user"
                content = @(
                    @{ type = "text"; text = "Return a brief answer ending with [POINT:x,y:label:screen1] or [POINT:none]. Target: $($fixture.targetText)" },
                    @{ type = "image_url"; image_url = @{ url = "data:image/png;base64,$base64" } }
                )
            }
        )
    } | ConvertTo-Json -Depth 20

    $headers = @{
        "Authorization" = "Bearer $apiKey"
        "Content-Type" = "application/json"
    }

    $response = Invoke-RestMethod -Method Post -Uri "https://api.z.ai/api/paas/v4/chat/completions" -Headers $headers -Body $body
    return $response.choices[0].message.content
}

$settingsPath = Join-Path $env:APPDATA "Clicky\settings.json"
if (-not (Test-Path $settingsPath)) {
    throw "Missing Clicky settings file: $settingsPath"
}

$settings = Get-Content -Raw $settingsPath | ConvertFrom-Json
$secrets = Read-ClickySecrets
$provider = $settings.LlmProvider
$model = $settings.LlmModel

if ([string]::IsNullOrWhiteSpace($model)) {
    $model = if ($provider -eq "zai") { "glm-4.6v" } else { "claude-sonnet-4-6" }
}

$apiKey = if ($provider -eq "zai") { $secrets.zai_api_key } else { $secrets.anthropic_api_key }
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    throw "Missing API key for configured provider '$provider'."
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$catalog = Get-Content -Raw (Join-Path $FixtureDir "fixtures.json") | ConvertFrom-Json
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$report = @()

foreach ($fixture in $catalog.fixtures) {
    $imagePath = Join-Path $FixtureDir $fixture.imagePath
    Write-Host "Evaluating $($fixture.id) with $provider/$model"

    $started = Get-Date
    $raw = if ($provider -eq "zai") {
        Invoke-ZaiFixture $apiKey $model $fixture $imagePath
    } else {
        Invoke-AnthropicFixture $apiKey $model $fixture $imagePath
    }
    $elapsedMs = [int]((Get-Date) - $started).TotalMilliseconds
    $eval = Test-FixtureResult $fixture $raw

    $report += [pscustomobject]@{
        provider = $provider
        model = $model
        fixtureId = $fixture.id
        targetText = $fixture.targetText
        returnedTag = if ($raw -match "\[POINT:[^\]]+\]") { $Matches[0] } else { "" }
        passed = $eval.passed
        reason = $eval.reason
        latencyMs = $elapsedMs
        rawResponse = $raw
    }
}

$jsonPath = Join-Path $OutDir "semantic-pointing-$timestamp.json"
$csvPath = Join-Path $OutDir "semantic-pointing-$timestamp.csv"
$report | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $jsonPath
$report | Export-Csv -NoTypeInformation -Encoding UTF8 $csvPath

Write-Host "Wrote $jsonPath"
Write-Host "Wrote $csvPath"
