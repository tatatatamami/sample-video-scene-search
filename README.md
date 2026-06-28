# Video Scene Search / 動画シーン検索

Azure AI Foundry Hosted Agent と Azure AI Search を活用した、動画のシーン・キーフレームを自然言語で検索できるデモアプリです。

## 概要

動画を Azure Video Indexer で解析し、各キーフレームを Azure AI Content Understanding で構造化分析します。結果を Azure AI Search の統合インデックスに登録することで、自然言語の質問に対して最も関連性の高いシーンを返します。

```
Azure AI Video Indexer
  ↓ シーン・字幕・人物・OCR・キーフレーム抽出
Azure AI Content Understanding
  ↓ キーフレーム画像の構造化分析
build_knowledge.py
  ↓ scene_docs.json / keyframe_docs.json
Azure OpenAI Embeddings
  ↓
Azure AI Search 統合インデックス（video-scenes）
  ↓ BM25 + HNSW のハイブリッド検索
ASP.NET Core Web Application
  ↓ 検索結果をコンテキストとして渡す
Microsoft Foundry Hosted Agent
```

アーキテクチャの詳細（ナレッジ設計・検索フロー・エージェントの役割）は [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) を参照してください。

## 技術スタック

| コンポーネント | 技術 |
|---|---|
| Web アプリ | ASP.NET Core 8 Razor Pages |
| AI エージェント | Azure AI Foundry Hosted Agent |
| AI クライアント | HttpClient（Responses API 直接呼び出し） |
| キーフレーム解析 | Azure AI Content Understanding（`prebuilt-image` ベースアナライザー） |
| 動画解析 | Azure Video Indexer |
| 検索インデックス | Azure AI Search（BM25 + HNSW ハイブリッド + セマンティックランカー） |
| Embedding | Azure OpenAI text-embedding-3-small |
| 認証 | Azure.Identity / DefaultAzureCredential |

---

## 前提条件

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- [Azure CLI](https://docs.microsoft.com/cli/azure/install-azure-cli)（`az login` 済み）
- [azd (Azure Developer CLI)](https://aka.ms/azd)（Foundry Agent デプロイ用）
- Python 3.9+（バッチ処理用）
- Azure AI Foundry プロジェクト
- Azure AI Search サービス
- Azure AI Content Understanding 対応の Azure AI サービスリソース

---

## セットアップ

### 1. リポジトリのクローン

```bash
git clone https://github.com/tatatatamami/sample-video-scene-search.git
cd sample-video-scene-search
```

### 2. Foundry Hosted Agent のデプロイ

`video-scene-search/` に azd プロジェクトがあります。

```bash
cd video-scene-search
azd auth login
azd provision
azd deploy
```

デプロイ後、エージェントのエンドポイント URL を控えておきます：

```
https://{resource}.services.ai.azure.com/api/projects/{project}/agents/video-scene-search/endpoint/protocols/openai?api-version=v1
```

### 3. Web アプリの設定

`VideoSceneSearch/appsettings.Development.json` を作成します（`.gitignore` 対象のため git には含まれません）：

```json
{
  "AzureAIFoundry": {
    "Endpoint": "https://YOUR-RESOURCE.services.ai.azure.com/api/projects/YOUR-PROJECT/agents/video-scene-search/endpoint/protocols/openai?api-version=v1",
    "ModelDeploymentName": "gpt-4.1"
  }
}
```

または環境変数で設定する場合（`:` の区切りは `__` に変換）：

```powershell
$env:AzureAIFoundry__Endpoint              = "https://..."
$env:AzureAIFoundry__ModelDeploymentName   = "gpt-4.1"
```

> **認証**: Web アプリは `DefaultAzureCredential` を使用します（API キー不要）。ローカル開発では `az login` 済みであれば追加設定は不要です。Azure 上（App Service など）では Managed Identity を割り当てることで `az login` なしで動作します。

### 4. RBAC の設定

各コンポーネントが動作するために、以下の RBAC ロールを割り当ててください。

| コンポーネント | 対象リソース | 必要なロール |
|-------------|------------|------------|
| Web アプリ（`DefaultAzureCredential`） | Azure AI Foundry | `Azure AI Developer` |
| バッチスクリプト（`az account get-access-token`） | Azure AI Search | `Search Index Data Contributor` |
| バッチスクリプト（`az account get-access-token`） | Azure OpenAI | `Cognitive Services OpenAI User` |
| `analyze_keyframes.py`（`DefaultAzureCredential`） | Azure AI サービス | `Cognitive Services User` |

> Hosted Agent の Azure AI Search・Azure OpenAI へのアクセスは `infra/` の Bicep でプロビジョニング済みです。

- **ローカル開発**: `az login` で認証した ID に上記ロールを付与してください
- **Azure デプロイ**: Managed Identity に上記ロールを付与してください（`az login` 不要）

### 5. Web アプリの起動

```bash
cd VideoSceneSearch
dotnet run
```

`http://localhost:5062` にアクセスして動作確認できます。

---

## バッチ処理パイプライン

検索インデックスを構築するためのスクリプト群です。

```
VideoSceneSearch/Batch/
├── ContentUnderstanding/
│   ├── analyze_keyframes.py       # Azure AI Content Understanding でキーフレーム解析
│   ├── FeldSchema_sample.json     # フィールドスキーマ（サンプル）
│   ├── input/
│   │   └── _KeyFrameThumbnail/    # Video Indexer から取得したキーフレーム画像（.jpg）
│   └── output/                    # 解析結果 JSON（gitignore）
└── SceneAggregate/
    ├── extract_scene_facts.py     # Insights JSON からシーン情報を抽出
    ├── build_knowledge.py         # シーン・キーフレームドキュメント生成
    ├── upload_to_aisearch.py      # Azure AI Search へアップロード
    ├── run_scene_aggregate.ps1    # パイプライン一括実行スクリプト
    ├── face_name_aliases.json.sample  # 顔認識エイリアス設定サンプル
    ├── input/
    │   └── _Insights/             # Video Indexer の Insights JSON（gitignore）
    └── output/                    # 生成ドキュメント（gitignore）
```

### Step 1: キーフレーム画像の解析（Azure AI Content Understanding）

Azure AI Content Understanding を使用して、Video Indexer が抽出したキーフレーム画像を構造化分析します。

- **サービス**: Azure AI Content Understanding
- **基底アナライザー**: `prebuilt-image`
- **completion model**: Analyzer 定義（`FeldSchema_*.json`）で指定するモデル（例: `gpt-4.1`）
- **入力**: Video Indexer が抽出したキーフレーム画像（`.jpg`）
- **出力**: キーフレームごとの構造化 JSON（`KeyFrameThumbnail_*.json`）

```powershell
cd VideoSceneSearch/Batch/ContentUnderstanding

python analyze_keyframes.py `
    --input-dir   "input/_KeyFrameThumbnail" `
    --output-dir  "output/YourVideo" `
    --schema-file "FeldSchema_sample.json" `
    --endpoint    "https://YOUR-AI-RESOURCE.cognitiveservices.azure.com" `
    --analyzer-id "keyframe-scene-analyzer"
```

`FeldSchema_sample.json` をコピーして動画の内容に合わせてカスタマイズしてください。

> **認証**: `DefaultAzureCredential` を使用します。ローカル実行時は `az login` 済みであれば追加設定は不要です。

### Step 2: シーン情報の抽出とナレッジドキュメント生成

Video Indexer の Insights と Content Understanding の出力を統合して、検索ドキュメントを生成します。

```bash
cd VideoSceneSearch/Batch/SceneAggregate

# Step 2a: Insights から構造化シーン情報を抽出
python extract_scene_facts.py \
    --input  "input/_Insights/your_video_insights.json" \
    --output "output/your-video/scene_facts.json"

# Step 2b: scene_docs.json と keyframe_docs.json を生成
python build_knowledge.py \
    --scene-facts     "output/your-video/scene_facts.json" \
    --cu-output       "../ContentUnderstanding/output/YourVideo" \
    --scene-output    "output/your-video/scene_docs.json" \
    --keyframe-output "output/your-video/keyframe_docs.json"
```

### Step 3: Azure AI Search 統合インデックスへのアップロード

`scene_docs.json` と `keyframe_docs.json` を同一インデックス（`video-scenes`）へ、単位ごとに分けてアップロードします。`documentType` フィールドで `scene` / `keyframe` を区別できます。

**keyframe を登録:**

```bash
python upload_to_aisearch.py \
    --file               "output/your-video/keyframe_docs.json" \
    --search-endpoint    "https://YOUR-SEARCH-SERVICE.search.windows.net" \
    --index-name         "video-scenes" \
    --embedding-endpoint "https://YOUR-AI-RESOURCE.services.ai.azure.com" \
    --embedding-deployment "text-embedding-3-small"
```

**scene を登録（同じインデックス名を指定）:**

```bash
python upload_to_aisearch.py \
    --file               "output/your-video/scene_docs.json" \
    --search-endpoint    "https://YOUR-SEARCH-SERVICE.search.windows.net" \
    --index-name         "video-scenes" \
    --embedding-endpoint "https://YOUR-AI-RESOURCE.services.ai.azure.com" \
    --embedding-deployment "text-embedding-3-small"
```

> **認証**: `upload_to_aisearch.py` は `az account get-access-token` 経由でトークンを取得します。事前に `az login` が必要です。

#### run_scene_aggregate.ps1 による一括実行

Step 2〜3 をまとめて実行するラッパースクリプトです。

```powershell
cd VideoSceneSearch/Batch/SceneAggregate

# keyframe を登録
.\run_scene_aggregate.ps1 `
    -Unit keyframe `
    -InsightsFile        "input\_Insights\your_video_insights.json" `
    -CuOutputDir         "..\ContentUnderstanding\output\YourVideo" `
    -OutputDir           "output\your-video" `
    -SearchEndpoint      "https://YOUR-SEARCH-SERVICE.search.windows.net" `
    -KeyframeIndexName   "video-scenes" `
    -EmbeddingEndpoint   "https://YOUR-AI-RESOURCE.services.ai.azure.com" `
    -EmbeddingDeployment "text-embedding-3-small"

# scene を登録（同じインデックス名を指定して統合インデックスに追加）
.\run_scene_aggregate.ps1 `
    -Unit scene `
    -InsightsFile        "input\_Insights\your_video_insights.json" `
    -CuOutputDir         "..\ContentUnderstanding\output\YourVideo" `
    -OutputDir           "output\your-video" `
    -SearchEndpoint      "https://YOUR-SEARCH-SERVICE.search.windows.net" `
    -SceneIndexName      "video-scenes" `
    -EmbeddingEndpoint   "https://YOUR-AI-RESOURCE.services.ai.azure.com" `
    -EmbeddingDeployment "text-embedding-3-small"
```

### Step 4: 動画マッピングの登録

`VideoSceneSearch/videomapping.json` でエージェントが返す `videoId` と実際の動画ファイルをマッピングします。

```json
{
  "VideoMapping": {
    "video-id-1": {
      "title": "動画タイトル",
      "file": "/videos/local-video.mp4",
      "thumbnail": "/videos/thumbnails/thumb.jpg"
    },
    "video-id-2": {
      "title": "外部ストレージの動画",
      "file": "https://your-storage.blob.core.windows.net/videos/video.mp4",
      "thumbnail": ""
    }
  }
}
```

ローカル動画は `VideoSceneSearch/wwwroot/videos/` に配置します（`.mp4` は `.gitignore` 対象）。

---

## プロジェクト構成

```
sample-video-scene-search/
├── VideoSceneSearch/                    # ASP.NET Core 8 Web アプリ
│   ├── Program.cs                       # アプリエントリポイント・DI 設定
│   ├── Pages/Index.cshtml               # 検索 UI（Razor Pages）
│   ├── Services/FoundryAgentClient.cs   # Foundry Hosted Agent クライアント
│   ├── Models/                          # データモデル
│   ├── videomapping.json                # 動画マッピング設定
│   ├── appsettings.json                 # 基本設定（プレースホルダー）
│   ├── appsettings.Development.json     # ローカル開発設定（gitignore）
│   └── Batch/
│       ├── ContentUnderstanding/        # Azure AI Content Understanding キーフレーム解析
│       └── SceneAggregate/              # ナレッジ統合・AI Search 登録
└── video-scene-search/                  # Foundry Hosted Agent（azd プロジェクト）
    ├── azure.yaml                       # azd サービス定義
    ├── infra/                           # Bicep インフラ定義
    └── src/video-scene-search/
        ├── Program.cs                   # エージェント指示・ロジック
        └── agent.yaml                   # エージェント設定
```

---

## 設定リファレンス

| 設定キー | 説明 | 例 |
|---------|------|-----|
| `AzureAIFoundry:Endpoint` | Foundry Hosted Agent のエンドポイント URL | `https://YOUR-RESOURCE.services.ai.azure.com/api/projects/YOUR-PROJECT/agents/video-scene-search/endpoint/protocols/openai?api-version=v1` |
| `AzureAIFoundry:ModelDeploymentName` | エージェントが使用するモデルデプロイ名 | `gpt-4.1` |

設定の優先順位（後が優先）：`appsettings.json` → `appsettings.Development.json` → 環境変数 → `launchSettings.json`

---

## 現在の制約・未実装事項

このプロジェクトは PoC（概念実証）レベルの実装です。以下の点に注意してください。

- **`visiblePeople` は現在未実装**: インデックスにフィールドは存在しますが、常に空配列となります
- **人物フィルター**: Azure AI Search インデックスには `scenePeople` フィールドが `filterable` として定義されていますが、Toolbox MCP による検索クエリへの自動適用は未実装です
- **scene / keyframe のルーティング**: クエリ内容に応じた `documentType` の絞り込みは Hosted Agent の指示（system instructions）に基づいて行われます
- **アップロード処理のリトライ**: `upload_to_aisearch.py` の 429 対応・リトライ処理は PoC レベルです
- **エージェントの回答**: Hosted Agent が返す `documentId`・`videoId`・`startMs`/`endMs` を Web アプリが解析してシーン結果を構築します。Agent が空または不正な値を返した場合、そのシーンは表示されません
- **エンドユーザー認証・認可**: 未実装
- **動画のアップロード・加工・インデックス自動作成**: 未実装

---

## ライセンス

MIT

## 貢献

Issue や Pull Request を歓迎します！
