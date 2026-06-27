<#
.SYNOPSIS
    SceneAggregate パイプラインを一括実行するラッパースクリプト。

.DESCRIPTION
    extract_scene_facts.py → build_knowledge.py → upload_to_aisearch.py
    の順に実行します。

    build_knowledge.py は Canonical Scene Knowledge をメモリ上で 1 回生成し、
    --unit keyframe （デフォルト）: キーフレーム単位で AI Search インデックスに登録
    --unit scene               : シーン単位で AI Search インデックスに登録

    【再実行時の運用ルール】
    upload_to_aisearch.py は mergeOrUpload を使用するため、再実行時は同一 ID のドキュメントを
    上書きします。インデックスを完全にリセットしたい場合は Azure ポータルからインデックスを
    削除してから再実行してください。

.PARAMETER Unit
    ドキュメント単位。"keyframe" または "scene"。デフォルト: keyframe

.PARAMETER InsightsFile
    Video Indexer の Insights JSON ファイルパス。

.PARAMETER CuOutputDir
    ContentUnderstanding バッチの出力フォルダ（KeyFrameThumbnail_*.json が格納されたフォルダ）。

.PARAMETER OutputDir
    中間ファイルおよび最終ナレッジ JSON の出力先フォルダ。

.PARAMETER SearchEndpoint
    Azure AI Search エンドポイント（例: https://<name>.search.windows.net）。

.PARAMETER KeyframeIndexName
    キーフレーム単位アップロード先の AI Search インデックス名（例: video-scenes-keyframe）。
    --Unit keyframe（デフォルト）で使用される。

.PARAMETER SceneIndexName
    シーン単位アップロード先の AI Search インデックス名（例: video-scenes-scene）。
    --Unit scene で使用される。

.PARAMETER EmbeddingEndpoint
    Azure OpenAI エンドポイント（例: https://<resource>.services.ai.azure.com）。
    Embedding モデルを呼び出すために使用します。

.PARAMETER EmbeddingDeployment
    Embedding モデルのデプロイメント名（デフォルト: text-embedding-3-small）。

.PARAMETER SkipUpload
    このスイッチを指定すると upload_to_vectorstore.py をスキップします（ドライラン用）。

.EXAMPLE
    # キーフレーム単位（デフォルト）
    .\run_scene_aggregate.ps1 `
        --InsightsFile        "input\_Insights\minecraft_insights.json" `
        --CuOutputDir         "..\ContentUnderstanding\output\マイクラ" `
        --OutputDir           "output\マイクラ" `
        --SearchEndpoint      "https://<name>.search.windows.net" `
        --KeyframeIndexName   "video-scenes-keyframe" `
        --EmbeddingEndpoint   "https://<resource>.services.ai.azure.com" `
        --EmbeddingDeployment "text-embedding-3-small"

    # シーン単位
    .\run_scene_aggregate.ps1 `
        --Unit scene `
        --InsightsFile        "input\_Insights\minecraft_insights.json" `
        --CuOutputDir         "..\ContentUnderstanding\output\マイクラ" `
        --OutputDir           "output\マイクラ" `
        --SearchEndpoint      "https://<name>.search.windows.net" `
        --SceneIndexName      "video-scenes-scene" `
        --EmbeddingEndpoint   "https://<resource>.services.ai.azure.com" `
        --EmbeddingDeployment "text-embedding-3-small"

    # ★ 推奨: 統合インデックス（scene と keyframe を同一インデックスへ登録）
    # KeyframeIndexName と SceneIndexName を同じ値にすることで統合インデックスになります。
    # アプリ側は AzureAISearch:IndexName に同じ名前を指定してください。
    # documentType フィールドで scene / keyframe を区別できます。

    # -- Step 1: keyframe を登録 --
    .\run_scene_aggregate.ps1 `
        -Unit keyframe `
        -InsightsFile        "input\_Insights\minecraft_insights.json" `
        -CuOutputDir         "..\ContentUnderstanding\output\マイクラ" `
        -OutputDir           "output\マイクラ" `
        -SearchEndpoint      "https://<name>.search.windows.net" `
        -KeyframeIndexName   "video-scenes" `
        -EmbeddingEndpoint   "https://<resource>.services.ai.azure.com" `
        -EmbeddingDeployment "text-embedding-3-small"

    # -- Step 2: scene を登録（同じインデックス名を指定）--
    .\run_scene_aggregate.ps1 `
        -Unit scene `
        -InsightsFile        "input\_Insights\minecraft_insights.json" `
        -CuOutputDir         "..\ContentUnderstanding\output\マイクラ" `
        -OutputDir           "output\マイクラ" `
        -SearchEndpoint      "https://<name>.search.windows.net" `
        -SceneIndexName      "video-scenes" `
        -EmbeddingEndpoint   "https://<resource>.services.ai.azure.com" `
        -EmbeddingDeployment "text-embedding-3-small"

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

    [string]$SearchEndpoint,
    [string]$KeyframeIndexName,
    [string]$SceneIndexName,
    [string]$EmbeddingEndpoint,
    [string]$EmbeddingDeployment = "text-embedding-3-small",

    [switch]$SkipUpload
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# Unit に応じたアップロード先インデックス名を決定
# ---------------------------------------------------------------------------
$TargetIndexName = if ($Unit -eq "keyframe") { $KeyframeIndexName } else { $SceneIndexName }

# ---------------------------------------------------------------------------
# 前提チェック
# ---------------------------------------------------------------------------
if (-not $SkipUpload) {
    if (-not $TargetIndexName) {
        $requiredParam = if ($Unit -eq "keyframe") { "KeyframeIndexName" } else { "SceneIndexName" }
        Write-Error "--$requiredParam は Unit=$Unit のとき --SkipUpload なしで必須です。"
        exit 1
    }
    if (-not $SearchEndpoint) {
        Write-Error "--SearchEndpoint は --SkipUpload なしで必須です。"
        exit 1
    }
    if (-not $EmbeddingEndpoint) {
        Write-Error "--EmbeddingEndpoint は --SkipUpload なしで必須です（--skip-vectorization を使う場合は upload_to_aisearch.py を直接実行してください）。"
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
        Write-Host "  Search エンドポイント: $SearchEndpoint"
        Write-Host "  インデックス         : $TargetIndexName"
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
    # Step 3: upload_to_aisearch.py
    # -----------------------------------------------------------------------
    $UploadStep = 3
    if ($SkipUpload) {
        Write-Host "[$UploadStep] upload_to_aisearch.py をスキップしました (--SkipUpload)" -ForegroundColor DarkGray
        Write-Host "  アップロード対象ファイル: $UploadFile"
    } else {
        Write-Host "[$UploadStep] upload_to_aisearch.py を実行中..." -ForegroundColor Yellow
        python upload_to_aisearch.py `
            --file                 $UploadFile `
            --search-endpoint      $SearchEndpoint `
            --index-name           $TargetIndexName `
            --embedding-endpoint   $EmbeddingEndpoint `
            --embedding-deployment $EmbeddingDeployment
        if ($LASTEXITCODE -ne 0) { throw "upload_to_aisearch.py が失敗しました (exit $LASTEXITCODE)" }
        Write-Host "  → インデックス '$TargetIndexName' にアップロード完了" -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "=== 完了 ===" -ForegroundColor Cyan

} finally {
    Pop-Location
}
