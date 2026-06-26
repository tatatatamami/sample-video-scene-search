# =============================================================================
# GPT-4.1 Vision - Batch Image Analysis Script
# (Azure AI Content Understanding の代替: GPT-4.1 Vision + FeldSchema)
# =============================================================================
# Usage:
#   .\run-batch.ps1
#   .\run-batch.ps1 -Force   # 既処理済みファイルも再処理する
# =============================================================================

param(
    [switch]$Force
)

# -----------------------------------------------------------------------------
# 設定
# -----------------------------------------------------------------------------
$ResourceEndpoint   = "https://se3-tamamiihori-1-1-resource.services.ai.azure.com"
$DeploymentName     = "gpt-4.1"
$ApiVersion         = "2025-01-01-preview"
$InputDir           = "$PSScriptRoot\input\_KeyFrameThumbnail"
$OutputDir          = "$PSScriptRoot\output"
$SchemaFile         = "$PSScriptRoot\FeldSchema.json"
$RequestDelayMs     = 200     # レート制限対策（ミリ秒）
$TokenRefreshEvery  = 50      # N件ごとにトークン更新

# -----------------------------------------------------------------------------
# 認証トークン取得（Azure CLI）
# -----------------------------------------------------------------------------
function Get-AccessToken {
    $token = az account get-access-token --resource "https://cognitiveservices.azure.com" --query accessToken -o tsv 2>$null
    if (-not $token) {
        Write-Error "Azure CLI の認証に失敗しました。'az login' を実行してください。"
        exit 1
    }
    return $token
}

# -----------------------------------------------------------------------------
# システムプロンプト生成（FeldSchema.jsonから構築）
# -----------------------------------------------------------------------------
function Build-SystemPrompt {
    $schema = Get-Content $SchemaFile -Raw | ConvertFrom-Json
    $fields = $schema.fieldSchema.fields

    $fieldDescriptions = @()
    foreach ($fieldName in $fields.PSObject.Properties.Name) {
        $field = $fields.$fieldName
        $fieldDescriptions += "- `"$fieldName`": $($field.description)"
    }

    $fieldList = $fieldDescriptions -join "`n"

    $prompt  = "You are an image analysis assistant. Analyze the provided image and return ONLY a valid JSON object with NO extra text, NO markdown, NO code blocks.`n`n"
    $prompt += "Required fields:`n$fieldList`n`n"
    $prompt += "IMPORTANT: Output ONLY the JSON object. No markdown, no code fences, no extra text. Follow each field description exactly."
    return $prompt
}

# -----------------------------------------------------------------------------
# 1件の画像を分析する関数（GPT-4.1 Vision）
# -----------------------------------------------------------------------------
function Invoke-VisionAnalysis {
    param(
        [string]$ImagePath,
        [string]$Token,
        [string]$SystemPrompt
    )

    # 画像を Base64 エンコード
    $imageBytes  = [System.IO.File]::ReadAllBytes($ImagePath)
    $base64Image = [System.Convert]::ToBase64String($imageBytes)
    $fileName    = [System.IO.Path]::GetFileName($ImagePath)

    $url = "$ResourceEndpoint/openai/deployments/$DeploymentName/chat/completions?api-version=$ApiVersion"

    $body = @{
        messages = @(
            @{
                role    = "system"
                content = $SystemPrompt
            },
            @{
                role    = "user"
                content = @(
                    @{
                        type      = "text"
                        text      = "Analyze this image. The filename is: $fileName"
                    },
                    @{
                        type      = "image_url"
                        image_url = @{
                            url    = "data:image/jpeg;base64,$base64Image"
                            detail = "high"
                        }
                    }
                )
            }
        )
        max_tokens      = 800
        temperature     = 0
        response_format = @{ type = "json_object" }
    } | ConvertTo-Json -Depth 10 -Compress

    $headers = @{
        "Authorization" = "Bearer $Token"
        "Content-Type"  = "application/json"
    }

    try {
        $response = Invoke-RestMethod -Uri $url -Method POST -Headers $headers -Body $body
        $content  = $response.choices[0].message.content
        # JSON検証
        $parsed = $content | ConvertFrom-Json
        return @{
            success = $true
            result  = @{
                imagePath = $fileName
                analysis  = $parsed
                usage     = $response.usage
            }
        }
    } catch {
        return @{ success = $false; error = $_.Exception.Message }
    }
}

# =============================================================================
# メイン処理
# =============================================================================
Write-Host "=== GPT-4.1 Vision バッチ画像分析 ===" -ForegroundColor Cyan
Write-Host "入力ディレクトリ : $InputDir"
Write-Host "出力ディレクトリ : $OutputDir"
Write-Host "モデル           : $DeploymentName"
Write-Host ""

# スキーマファイル確認
if (-not (Test-Path $SchemaFile)) {
    Write-Error "スキーマファイルが見つかりません: $SchemaFile"
    exit 1
}

# 入力ファイル一覧取得
$images = Get-ChildItem -Path $InputDir -Include "*.jpg","*.jpeg","*.png" -File
Write-Host "対象画像数: $($images.Count) 件" -ForegroundColor Green

if ($images.Count -eq 0) {
    Write-Warning "処理対象の画像が見つかりません: $InputDir"
    exit 0
}

# 出力ディレクトリ作成
New-Item -ItemType Directory -Force $OutputDir | Out-Null

# システムプロンプト生成
Write-Host "スキーマからプロンプト生成中..." -NoNewline
$systemPrompt = Build-SystemPrompt
Write-Host " 完了" -ForegroundColor Green

# アクセストークン取得
Write-Host "アクセストークン取得中..." -NoNewline
$token = Get-AccessToken
Write-Host " 完了" -ForegroundColor Green

# 処理済み・未処理を仕分け
$pending = @()
$skipped = 0
foreach ($img in $images) {
    $outFile = Join-Path $OutputDir "$($img.BaseName).json"
    if (-not $Force -and (Test-Path $outFile)) {
        $skipped++
    } else {
        $pending += $img
    }
}

if ($skipped -gt 0) {
    Write-Host "スキップ（処理済み）: $skipped 件  ※再処理するには -Force オプション" -ForegroundColor Yellow
}
Write-Host "処理対象: $($pending.Count) 件`n"

if ($pending.Count -eq 0) {
    Write-Host "すべて処理済みです。" -ForegroundColor Green
    exit 0
}

# バッチ処理（進捗表示）
$success = 0
$failure = 0
$totalTokens = 0
$total = $pending.Count

for ($i = 0; $i -lt $total; $i++) {
    $img     = $pending[$i]
    $outFile = Join-Path $OutputDir "$($img.BaseName).json"
    $pct     = [int](($i / $total) * 100)

    Write-Progress -Activity "GPT-4.1 Vision 分析中" `
        -Status "[$($i+1)/$total] $($img.Name)" `
        -PercentComplete $pct

    $res = Invoke-VisionAnalysis -ImagePath $img.FullName -Token $token -SystemPrompt $systemPrompt

    if ($res.success) {
        $res.result | ConvertTo-Json -Depth 10 | Set-Content -Path $outFile -Encoding UTF8
        $totalTokens += $res.result.usage.total_tokens
        $success++
        Write-Host "  ✓ [$($i+1)/$total] $($img.Name)  (tokens: $($res.result.usage.total_tokens))" -ForegroundColor Green
    } else {
        $failure++
        Write-Host "  ✗ [$($i+1)/$total] $($img.Name) → $($res.error)" -ForegroundColor Red
        @{ file = $img.Name; error = $res.error; timestamp = (Get-Date -Format "yyyy-MM-dd HH:mm:ss") } `
            | ConvertTo-Json | Add-Content -Path (Join-Path $OutputDir "_errors.log") -Encoding UTF8
    }

    # レート制限対策
    if ($RequestDelayMs -gt 0) {
        Start-Sleep -Milliseconds $RequestDelayMs
    }

    # トークンを定期的に更新
    if (($i + 1) % $TokenRefreshEvery -eq 0 -and ($i + 1) -lt $total) {
        Write-Host "  [トークン更新中...]" -ForegroundColor DarkGray
        $token = Get-AccessToken
    }
}

Write-Progress -Activity "GPT-4.1 Vision 分析中" -Completed

Write-Host ""
Write-Host "=== 完了 ===" -ForegroundColor Cyan
Write-Host "成功      : $success 件" -ForegroundColor Green
Write-Host "失敗      : $failure 件" -ForegroundColor $(if ($failure -gt 0) { "Red" } else { "Green" })
Write-Host "合計トークン: $totalTokens"
Write-Host "出力先    : $OutputDir"


# -----------------------------------------------------------------------------
# 認証トークン取得（Azure CLI）
# -----------------------------------------------------------------------------
function Get-AccessToken {
    $token = az account get-access-token --resource "https://cognitiveservices.azure.com" --query accessToken -o tsv 2>$null
    if (-not $token) {
        Write-Error "Azure CLI の認証に失敗しました。'az login' を実行してください。"
        exit 1
    }
    return $token
}

# -----------------------------------------------------------------------------
# 1件の画像を分析する関数
# -----------------------------------------------------------------------------
function Invoke-ContentUnderstanding {
    param(
        [string]$ImagePath,
        [string]$Token
    )

    $fileName = [System.IO.Path]::GetFileName($ImagePath)

    # 画像を Base64 エンコード
    $imageBytes  = [System.IO.File]::ReadAllBytes($ImagePath)
    $base64Image = [System.Convert]::ToBase64String($imageBytes)
    $ext         = [System.IO.Path]::GetExtension($ImagePath).TrimStart(".").ToLower()
    $mimeType    = if ($ext -eq "jpg" -or $ext -eq "jpeg") { "image/jpeg" } else { "image/png" }

    $analyzeUrl = "$ResourceEndpoint/contentunderstanding/analyzers/${AnalyzerId}:analyze?api-version=$ApiVersion"

    $body = @{
        url = "data:$mimeType;base64,$base64Image"
    } | ConvertTo-Json -Compress

    $headers = @{
        "Authorization" = "Bearer $Token"
        "Content-Type"  = "application/json"
    }

    # 分析リクエスト送信
    try {
        $response = Invoke-WebRequest -Uri $analyzeUrl -Method POST -Headers $headers -Body $body -UseBasicParsing
    } catch {
        return @{ success = $false; error = $_.Exception.Message }
    }

    # 202 Accepted → Operation-Location でポーリング
    if ($response.StatusCode -eq 202) {
        $operationUrl = $response.Headers["Operation-Location"]
        if (-not $operationUrl) {
            return @{ success = $false; error = "Operation-Location ヘッダーが見つかりません" }
        }

        for ($i = 0; $i -lt $MaxPollingRetry; $i++) {
            Start-Sleep -Seconds $PollingIntervalSec
            try {
                $poll = Invoke-RestMethod -Uri $operationUrl -Method GET -Headers $headers
            } catch {
                return @{ success = $false; error = "ポーリングエラー: $($_.Exception.Message)" }
            }

            $status = $poll.status
            if ($status -eq "succeeded") {
                return @{ success = $true; result = $poll }
            } elseif ($status -eq "failed") {
                return @{ success = $false; error = "分析失敗: $($poll.error | ConvertTo-Json)" }
            }
            # running / notStarted → 継続ポーリング
        }
        return @{ success = $false; error = "タイムアウト（ポーリング上限到達）" }
    }

    # 200 OK で即結果が返る場合
    if ($response.StatusCode -eq 200) {
        $result = $response.Content | ConvertFrom-Json
        return @{ success = $true; result = $result }
    }

    return @{ success = $false; error = "予期しないステータス: $($response.StatusCode)" }
}

# =============================================================================
# メイン処理
# =============================================================================
Write-Host "=== Azure AI Content Understanding バッチ処理 ===" -ForegroundColor Cyan
Write-Host "入力ディレクトリ : $InputDir"
Write-Host "出力ディレクトリ : $OutputDir"
Write-Host "Analyzer ID      : $AnalyzerId"
Write-Host ""

# 入力ファイル一覧取得
$images = Get-ChildItem -Path $InputDir -Include "*.jpg","*.jpeg","*.png" -File
Write-Host "対象画像数: $($images.Count) 件" -ForegroundColor Green

if ($images.Count -eq 0) {
    Write-Warning "処理対象の画像が見つかりません: $InputDir"
    exit 0
}

# 出力ディレクトリ作成
New-Item -ItemType Directory -Force $OutputDir | Out-Null

# アクセストークン取得
Write-Host "アクセストークン取得中..." -NoNewline
$token = Get-AccessToken
Write-Host " 完了" -ForegroundColor Green

# 処理済み・未処理を仕分け
$pending = @()
$skipped = 0
foreach ($img in $images) {
    $outFile = Join-Path $OutputDir "$($img.BaseName).json"
    if (-not $Force -and (Test-Path $outFile)) {
        $skipped++
    } else {
        $pending += $img
    }
}

if ($skipped -gt 0) {
    Write-Host "スキップ（処理済み）: $skipped 件  ※再処理するには -Force オプションを指定" -ForegroundColor Yellow
}
Write-Host "処理対象: $($pending.Count) 件`n"

if ($pending.Count -eq 0) {
    Write-Host "すべて処理済みです。" -ForegroundColor Green
    exit 0
}

# バッチ処理（進捗表示）
$success = 0
$failure = 0
$total   = $pending.Count

for ($i = 0; $i -lt $total; $i++) {
    $img     = $pending[$i]
    $outFile = Join-Path $OutputDir "$($img.BaseName).json"
    $pct     = [int](($i / $total) * 100)

    Write-Progress -Activity "Content Understanding 分析中" `
        -Status "[$($i+1)/$total] $($img.Name)" `
        -PercentComplete $pct

    $res = Invoke-ContentUnderstanding -ImagePath $img.FullName -Token $token

    if ($res.success) {
        $res.result | ConvertTo-Json -Depth 20 | Set-Content -Path $outFile -Encoding UTF8
        $success++
        Write-Host "  ✓ [$($i+1)/$total] $($img.Name)" -ForegroundColor Green
    } else {
        $failure++
        Write-Host "  ✗ [$($i+1)/$total] $($img.Name) → $($res.error)" -ForegroundColor Red
        # エラーログを出力
        @{ file = $img.Name; error = $res.error } `
            | ConvertTo-Json | Add-Content -Path (Join-Path $OutputDir "_errors.log") -Encoding UTF8
    }

    # トークンを60枚ごとに更新（有効期限対策）
    if (($i + 1) % 60 -eq 0 -and ($i + 1) -lt $total) {
        Write-Host "  [トークン更新中...]" -ForegroundColor DarkGray
        $token = Get-AccessToken
    }
}

Write-Progress -Activity "Content Understanding 分析中" -Completed

Write-Host ""
Write-Host "=== 完了 ===" -ForegroundColor Cyan
Write-Host "成功: $success 件" -ForegroundColor Green
Write-Host "失敗: $failure 件" -ForegroundColor $(if ($failure -gt 0) { "Red" } else { "Green" })
Write-Host "出力先: $OutputDir"
