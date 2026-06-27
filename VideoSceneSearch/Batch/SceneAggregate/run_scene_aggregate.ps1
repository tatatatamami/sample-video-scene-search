<#
.SYNOPSIS
    SceneAggregate パイプラインを一括実行するラッパースクリプト。

.DESCRIPTION
    extract_scene_facts.py → build_knowledge.py → upload_to_vectorstore.py
    の順に実行します。

    build_knowledge.py は Canonical Scene Knowledge をメモリ上で 1 回生成し、
    --unit keyframe （デフォルト）: キーフレーム单位でベクターストアに登録
    --unit scene               : シーン単位でベクターストアに登録

.PARAMETER Unit
    ドキュメント単位。"keyframe" または "scene"。デフォルト: keyframe

.PARAMETER InsightsFile
    Video Indexer の Insights JSON ファイルパス。

.PARAMETER CuOutputDir
    ContentUnderstanding バッチの出力フォルダ（KeyFrameThumbnail_*.json が格納されたフォルダ）。

.PARAMETER OutputDir
    中間ファイルおよび最終ナレッジ JSON の出力先フォルダ。

.PARAMETER VectorStoreId
    Azure AI Foundry のベクターストア ID（例: vs_XXXX）。

.PARAMETER BaseUrl
    Azure AI Foundry のベース URL（例: https://<resource>.services.ai.azure.com/api/projects/<project>/openai/v1）。

.PARAMETER SkipUpload
    このスイッチを指定すると upload_to_vectorstore.py をスキップします（ドライラン用）。

.EXAMPLE
    # キーフレーム単位（デフォルト）
    .\run_scene_aggregate.ps1 `
        --InsightsFile "input\_Insights\minecraft_insights.json" `
        --CuOutputDir  "..\ContentUnderstanding\output\マイクラ" `
        --OutputDir    "output\マイクラ" `
        --VectorStoreId "vs_XXXX" `
        --BaseUrl "https://<resource>.services.ai.azure.com/api/projects/<project>/openai/v1"

    # シーン単位
    .\run_scene_aggregate.ps1 `
        --Unit scene `
        --InsightsFile "input\_Insights\minecraft_insights.json" `
        --CuOutputDir  "..\ContentUnderstanding\output\マイクラ" `
        --OutputDir    "output\マイクラ" `
        --VectorStoreId "vs_XXXX" `
        --BaseUrl "https://<resource>.services.ai.azure.com/api/projects/<project>/openai/v1"

    # アップロードをスキップして中間ファイルだけ生成
    .\run_scene_aggregate.ps1 `
        --InsightsFile "input\_Insights\minecraft_insights.json" `
        --CuOutputDir  "..\ContentUnderstanding\output\マイクラ" `
        --OutputDir    "output\マイクラ" `
        --SkipUpload
#>

[CmdletBinding()]
param(
    [ValidateSet("keyframe", "scene")]
    [string]$Unit = "keyframe",

    [Parameter(Mandatory = $true)]
    [string]$InsightsFile,

    [Parameter(Mandatory = $true)]
    [string]$CuOutputDir,

    [Parameter(Mandatory = $true)]
    [string]$OutputDir,

    [string]$VectorStoreId,
    [string]$BaseUrl,

    [switch]$SkipUpload
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# 前提チェック
# ---------------------------------------------------------------------------
if (-not $SkipUpload) {
    if (-not $VectorStoreId) {
        Write-Error "--VectorStoreId は --SkipUpload なしで必須です。"
        exit 1
    }
    if (-not $BaseUrl) {
        Write-Error "--BaseUrl は --SkipUpload なしで必須です。"
        exit 1
    }
}

# スクリプトディレクトリを基点にする
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $ScriptDir

try {
    # 出力ディレクトリを作成
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

    $SceneFactsJson   = Join-Path $OutputDir "scene_facts.json"
    $SceneDocsJson    = Join-Path $OutputDir "scene_docs.json"
    $KeyframeDocsJson = Join-Path $OutputDir "keyframe_docs.json"

    Write-Host ""
    Write-Host "=== SceneAggregate パイプライン ===" -ForegroundColor Cyan
    Write-Host "  単位           : $Unit"
    Write-Host "  Insights       : $InsightsFile"
    Write-Host "  CU 出力フォルダ: $CuOutputDir"
    Write-Host "  出力フォルダ   : $OutputDir"
    if (-not $SkipUpload) {
        Write-Host "  ベクターストア : $VectorStoreId"
    }
    Write-Host ""

    # -----------------------------------------------------------------------
    # Step 1: extract_scene_facts.py
    # -----------------------------------------------------------------------
    Write-Host "[1/3] extract_scene_facts.py を実行中..." -ForegroundColor Yellow
    python extract_scene_facts.py `
        --input  $InsightsFile `
        --output $SceneFactsJson
    if ($LASTEXITCODE -ne 0) { throw "extract_scene_facts.py が失敗しました (exit $LASTEXITCODE)" }
    Write-Host "  → $SceneFactsJson" -ForegroundColor Green

    # -----------------------------------------------------------------------
    # Step 2: build_knowledge.py（シーン・キーフレームを 1 回のパスで生成）
    # -----------------------------------------------------------------------
    Write-Host "[2/3] build_knowledge.py を実行中..." -ForegroundColor Yellow
    python build_knowledge.py `
        --scene-facts     $SceneFactsJson `
        --cu-output       $CuOutputDir `
        --scene-output    $SceneDocsJson `
        --keyframe-output $KeyframeDocsJson
    if ($LASTEXITCODE -ne 0) { throw "build_knowledge.py が失敗しました (exit $LASTEXITCODE)" }
    Write-Host "  → $SceneDocsJson" -ForegroundColor Green
    Write-Host "  → $KeyframeDocsJson" -ForegroundColor Green

    # -----------------------------------------------------------------------
    # 最終アップロード対象ファイルを決定
    # -----------------------------------------------------------------------
    $UploadFile = if ($Unit -eq "keyframe") { $KeyframeDocsJson } else { $SceneDocsJson }

    # -----------------------------------------------------------------------
    # Step 3: upload_to_vectorstore.py
    # -----------------------------------------------------------------------
    $UploadStep = 3
    if ($SkipUpload) {
        Write-Host "[$UploadStep] upload_to_vectorstore.py をスキップしました (--SkipUpload)" -ForegroundColor DarkGray
        Write-Host "  アップロード対象ファイル: $UploadFile"
    } else {
        Write-Host "[$UploadStep] upload_to_vectorstore.py を実行中..." -ForegroundColor Yellow
        python upload_to_vectorstore.py `
            --file             $UploadFile `
            --vector-store-id  $VectorStoreId `
            --base-url         $BaseUrl
        if ($LASTEXITCODE -ne 0) { throw "upload_to_vectorstore.py が失敗しました (exit $LASTEXITCODE)" }
        Write-Host "  → ベクターストア $VectorStoreId にアップロード完了" -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "=== 完了 ===" -ForegroundColor Cyan

} finally {
    Pop-Location
}
