# =============================================================================
# Batch Image Analysis Script
# =============================================================================
# Usage:
#   .\run-batch.ps1 -InputDir "input\MyVideoKeyFrames" -OutputDir "output\MyVideo" -SchemaFile "FeldSchema_sample.json"
#   .\run-batch.ps1 -InputDir "..." -OutputDir "..." -SchemaFile "..." -VideoTitle "My Video"
#   .\run-batch.ps1 ... -Force   # re-process already completed files
#
# Required: az login (Azure CLI) - uses DefaultAzureCredential (no API key needed)
# =============================================================================
param(
    [switch]$Force,
    [string]$InputDir    = "$PSScriptRoot\input\_KeyFrameThumbnail",
    [string]$OutputDir   = "$PSScriptRoot\output",
    [string]$SchemaFile  = "$PSScriptRoot\FeldSchema_sample.json",
    [string]$VideoTitle  = "video",
    [string]$ResourceEndpoint = $env:AZURE_OPENAI_ENDPOINT
)

$DeploymentName    = "gpt-4.1"
$ApiVersion        = "2025-01-01-preview"
$RequestDelayMs    = 200
$TokenRefreshEvery = 50

if (-not $ResourceEndpoint) {
    Write-Error "ResourceEndpoint is required. Pass -ResourceEndpoint or set AZURE_OPENAI_ENDPOINT environment variable."
    exit 1
}

# -----------------------------------------------------------------------------
function Get-AccessToken {
    $t = az account get-access-token --resource "https://cognitiveservices.azure.com" --query accessToken -o tsv 2>$null
    if (-not $t) { Write-Error "az login required"; exit 1 }
    return $t
}

# -----------------------------------------------------------------------------
function Build-SystemPrompt {
    $schema = Get-Content $SchemaFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $fields = $schema.fieldSchema.fields
    $lines = @("Analyze the provided image and return ONLY a valid JSON object. No markdown, no code fences, no extra text.")
    $lines += ""
    $lines += "Required JSON fields and rules:"
    foreach ($name in $fields.PSObject.Properties.Name) {
        $desc = $fields.$name.description
        $lines += "  $name : $desc"
    }
    $lines += ""
    $lines += "Return ONLY the JSON object."
    return $lines -join "`n"
}

# -----------------------------------------------------------------------------
function Invoke-VisionAnalysis {
    param([string]$ImagePath, [string]$Token, [string]$SystemPrompt)

    $fileName = [System.IO.Path]::GetFileName($ImagePath)
    $bytes    = [System.IO.File]::ReadAllBytes($ImagePath)
    $b64      = [System.Convert]::ToBase64String($bytes)

    $url = "$ResourceEndpoint/openai/deployments/$DeploymentName/chat/completions?api-version=$ApiVersion"

    $bodyObj = @{
        messages = @(
            @{ role = "system"; content = $SystemPrompt },
            @{
                role = "user"
                content = @(
                    @{ type = "text"; text = "Analyze this image. Filename: $fileName" },
                    @{ type = "image_url"; image_url = @{ url = "data:image/jpeg;base64,$b64"; detail = "high" } }
                )
            }
        )
        max_tokens      = 800
        temperature     = 0
        response_format = @{ type = "json_object" }
    }

    $body    = $bodyObj | ConvertTo-Json -Depth 10 -Compress
    $headers = @{ "Authorization" = "Bearer $Token"; "Content-Type" = "application/json" }

    try {
        $resp  = Invoke-WebRequest -Uri $url -Method POST -Headers $headers -Body $body -UseBasicParsing
        # Fix encoding: PowerShell 5.x decodes HTTP body as Latin-1 by default
        # Re-encode as UTF-8 to restore Japanese characters correctly
        $bytes = [System.Text.Encoding]::GetEncoding("iso-8859-1").GetBytes($resp.Content)
        $json  = [System.Text.Encoding]::UTF8.GetString($bytes)
        $data    = $json | ConvertFrom-Json
        $content = $data.choices[0].message.content
        $parsed  = $content | ConvertFrom-Json
        return @{ success = $true; result = @{ imagePath = $fileName; analysis = $parsed; usage = $data.usage } }
    } catch {
        return @{ success = $false; error = $_.Exception.Message }
    }
}

# =============================================================================
# Main
# =============================================================================
Write-Host "=== GPT-4.1 Vision Batch ===" -ForegroundColor Cyan
Write-Host "VideoTitle: $VideoTitle"
Write-Host "Input     : $InputDir"
Write-Host "Output    : $OutputDir"
Write-Host "Schema    : $SchemaFile"
Write-Host "Model     : $DeploymentName"
Write-Host "Endpoint  : $ResourceEndpoint"
Write-Host ""

if (-not (Test-Path $SchemaFile)) { Write-Error "Schema not found: $SchemaFile"; exit 1 }
if (-not (Test-Path $InputDir))   { Write-Error "Input directory not found: $InputDir`nPlease place keyframe images in: $InputDir"; exit 1 }

$images = @(Get-ChildItem -LiteralPath $InputDir -File | Where-Object { $_.Extension -match '\.(jpg|jpeg|png)$' })
Write-Host "Images found: $($images.Count)" -ForegroundColor Green
if ($images.Count -eq 0) { exit 0 }

New-Item -ItemType Directory -Force $OutputDir | Out-Null

Write-Host "Building prompt..." -NoNewline
$systemPrompt = Build-SystemPrompt
Write-Host " done" -ForegroundColor Green

Write-Host "Getting access token..." -NoNewline
$token = Get-AccessToken
Write-Host " done" -ForegroundColor Green

$pending = @()
$skipped = 0
foreach ($img in $images) {
    $out = Join-Path $OutputDir "$($img.BaseName).json"
    if ((-not $Force) -and (Test-Path $out)) { $skipped++ } else { $pending += $img }
}
if ($skipped -gt 0) { Write-Host "Skipped (done): $skipped  (use -Force to reprocess)" -ForegroundColor Yellow }
Write-Host "To process: $($pending.Count)`n"
if ($pending.Count -eq 0) { Write-Host "All done." -ForegroundColor Green; exit 0 }

$ok      = 0
$ng      = 0
$ttokens = 0
$total   = $pending.Count

for ($i = 0; $i -lt $total; $i++) {
    $img = $pending[$i]
    $out = Join-Path $OutputDir "$($img.BaseName).json"
    $pct = [int](($i / $total) * 100)
    Write-Progress -Activity "Analyzing..." -Status "[$($i+1)/$total] $($img.Name)" -PercentComplete $pct

    $res = Invoke-VisionAnalysis -ImagePath $img.FullName -Token $token -SystemPrompt $systemPrompt

    if ($res.success) {
        $json = $res.result | ConvertTo-Json -Depth 10
        [System.IO.File]::WriteAllText($out, $json, [System.Text.UTF8Encoding]::new($false))
        $ttokens += $res.result.usage.total_tokens
        $ok++
        Write-Host "  OK [$($i+1)/$total] $($img.Name)  tokens:$($res.result.usage.total_tokens)" -ForegroundColor Green
    } else {
        $ng++
        Write-Host "  NG [$($i+1)/$total] $($img.Name) -> $($res.error)" -ForegroundColor Red
        $errLog  = Join-Path $OutputDir "_errors.log"
        $errJson = [PSCustomObject]@{ file = $img.Name; error = $res.error; time = (Get-Date -Format "yyyy-MM-dd HH:mm:ss") } | ConvertTo-Json
        [System.IO.File]::AppendAllText($errLog, $errJson + "`n", [System.Text.UTF8Encoding]::new($false))
    }

    if ($RequestDelayMs -gt 0) { Start-Sleep -Milliseconds $RequestDelayMs }

    if ((($i + 1) % $TokenRefreshEvery -eq 0) -and (($i + 1) -lt $total)) {
        Write-Host "  [Refreshing token...]" -ForegroundColor DarkGray
        $token = Get-AccessToken
    }
}

Write-Progress -Activity "Analyzing..." -Completed
Write-Host ""
Write-Host "=== Complete ===" -ForegroundColor Cyan
Write-Host "Success: $ok" -ForegroundColor Green
Write-Host "Failed : $ng" -ForegroundColor $(if ($ng -gt 0) { "Red" } else { "Green" })
Write-Host "Tokens : $ttokens"
Write-Host "Output : $OutputDir"
