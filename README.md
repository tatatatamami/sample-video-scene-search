# Video Scene Search / 動画シーン検索

Azure AI Foundry と GPT-4.1 Vision を活用した、動画のキーフレームを自然言語で検索できるデモアプリです。

## 概要

動画を Azure Video Indexer で解析し、各シーンのキーフレームを GPT-4.1 Vision で詳細説明。結果を Azure AI Foundry のベクターストアに登録することで、エージェントが自然言語の質問に対して最も関連性の高いシーンを返します。

```
動画ファイル
    ↓
Azure Video Indexer（キーフレーム抽出・文字起こし・人物認識）
    ↓
GPT-4.1 Vision（キーフレーム画像の詳細説明を生成）
    ↓
Azure AI Foundry ベクターストア（検索インデックス）
    ↓
ASP.NET Core Razor Pages（チャット UI で自然言語検索）
```

## 技術スタック

| コンポーネント | 技術 |
|---|---|
| Web アプリ | ASP.NET Core 8 Razor Pages |
| AI エージェント | Azure AI Foundry Hosted Agent |
| AI クライアント | OpenAI .NET SDK v2 (ResponsesClient) |
| 画像解析 | Azure OpenAI GPT-4.1 Vision |
| 動画解析 | Azure Video Indexer |
| ベクター検索 | Azure AI Foundry Vector Store |
| 認証 | Azure.Identity / DefaultAzureCredential |

---

## 前提条件

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- [Azure CLI](https://docs.microsoft.com/cli/azure/install-azure-cli)（`az login` 済み）
- [azd (Azure Developer CLI)](https://aka.ms/azd)（Foundry Agent デプロイ用）
- Python 3.9+（バッチ処理用）
- Azure AI Foundry プロジェクト

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
    "Endpoint": "https://{resource}.services.ai.azure.com/api/projects/{project}/agents/video-scene-search/endpoint/protocols/openai?api-version=v1",
    "ModelDeploymentName": "gpt-4.1"
  }
}
```

または環境変数で設定する場合（`:` の区切りは `__` に変換）：

```powershell
$env:AzureAIFoundry__Endpoint = "https://..."
$env:AzureAIFoundry__ModelDeploymentName = "gpt-4.1"
```

> **認証**: Microsoft Entra ID を使用します（API キー不要）。`az login` 済みであれば追加設定は不要です。

### 4. Web アプリの起動

```bash
cd VideoSceneSearch
dotnet run
```

`http://localhost:5062` にアクセスして動作確認できます。

---

## バッチ処理パイプライン

エージェントが検索に使う知識ベースを構築するためのスクリプト群です。

### Step 1: ContentUnderstanding（キーフレーム画像の解析）

Azure Video Indexer から取得したキーフレーム画像を GPT-4.1 Vision で解析します。

```
VideoSceneSearch/Batch/ContentUnderstanding/
├── run-batch.ps1          # 実行スクリプト
├── FeldSchema.json        # 抽出フィールド定義
├── FeldSchema_sample.json # サンプルフィールド定義
├── input/
│   └── _KeyFrameThumbnail/  # Video Indexer から取得したキーフレーム画像（.jpg）
└── output/                  # 解析結果 JSON（gitignore）
```

```powershell
cd VideoSceneSearch/Batch/ContentUnderstanding

# run-batch.ps1 内の設定値を編集してから実行
# $ResourceEndpoint: Azure OpenAI エンドポイント
# $DeploymentName: GPT-4.1 デプロイ名
# $InputDir: キーフレーム画像フォルダ

.\run-batch.ps1
```

### Step 2: SceneAggregate（ナレッジ構築・ベクターストア登録）

ContentUnderstanding の出力と Video Indexer の Insights を統合し、ベクターストアにアップロードします。

```
VideoSceneSearch/Batch/SceneAggregate/
├── scene_aggregate.py          # シーン情報の統合
├── build_knowledge.py          # ナレッジドキュメント生成
├── build_keyframe_knowledge.py # キーフレーム単位のナレッジ生成
├── upload_to_vectorstore.py    # ベクターストアへのアップロード
├── face_name_aliases.json.sample  # 顔認識エイリアス設定サンプル
├── input/
│   └── _Insights/              # Video Indexer の Insights JSON（gitignore）
└── output/                     # 生成ドキュメント（gitignore）
```

```bash
cd VideoSceneSearch/Batch/SceneAggregate
python scene_aggregate.py
python build_knowledge.py
python upload_to_vectorstore.py
```

---

## 動画マッピング設定

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
│       ├── ContentUnderstanding/        # GPT-4.1 Vision キーフレーム解析
│       └── SceneAggregate/              # ナレッジ統合・ベクターストア登録
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
| `AzureAIFoundry:Endpoint` | Foundry Hosted Agent のエンドポイント URL（`?api-version=v1` 必須） | `https://{resource}.services.ai.azure.com/api/projects/{project}/agents/{agent}/endpoint/protocols/openai?api-version=v1` |
| `AzureAIFoundry:ModelDeploymentName` | モデルデプロイ名 | `gpt-4.1` |

設定の優先順位（後が優先）：`appsettings.json` → `appsettings.Development.json` → 環境変数 → `launchSettings.json`

---

## バッチパイプライン（動画の登録）

### Step 1: キーフレーム画像の解析（GPT-4.1 Vision）

```powershell
cd VideoSceneSearch/Batch/ContentUnderstanding

.\run-batch.ps1 `
    -InputDir "input\YourVideoKeyFrames" `
    -OutputDir "output\YourVideo" `
    -SchemaFile "FeldSchema_sample.json" `
    -VideoTitle "Your Video" `
    -ResourceEndpoint "https://your-resource.services.ai.azure.com"
```

`FeldSchema_sample.json` をコピーして動画の内容に合わせてカスタマイズしてください。

### Step 2: ナレッジドキュメント生成

```bash
cd VideoSceneSearch/Batch/SceneAggregate

python build_knowledge.py \
    --scene-facts output/your-video/scene_facts.json \
    --cu-output ../ContentUnderstanding/output/YourVideo \
    --scene-output output/your-video/scene_docs.json \
    --keyframe-output output/your-video/keyframe_docs.json
```

### Step 3: ベクターストアへのアップロード

```bash
python upload_to_vectorstore.py \
    --file output/your-video/keyframe_docs.json \
    --vector-store-id vs_your_vector_store_id
```

### Step 4: 動画マッピングの登録

`VideoSceneSearch/videomapping.json` に動画 ID とファイルパスを追加します：

```json
{
  "videos": {
    "your-video-id": {
      "title": "動画タイトル",
      "file": "/videos/your-video.mp4",
      "thumbnail": ""
    }
  }
}
```

---

## プロジェクト構造

```
VideoSceneSearch/
├── Pages/                          # Razor Pages UI
├── Services/                       # Foundry Agent クライアント
├── Models/                         # データモデル
├── wwwroot/videos/                 # 動画ファイル置き場（.gitignore 対象）
├── videomapping.json               # 動画IDとファイルパスのマッピング
├── appsettings.json                # アプリ設定（シークレット除く）
├── CONFIGURATION.md                # 設定ガイド
└── Batch/
    ├── ContentUnderstanding/
    │   ├── run-batch.ps1           # 汎用バッチ実行スクリプト
    │   ├── FeldSchema_sample.json  # フィールドスキーマ（サンプル）
    │   ├── input/                  # キーフレーム画像置き場（.gitignore 対象）
    │   └── output/                 # 解析結果（.gitignore 対象）
    └── SceneAggregate/
        ├── build_knowledge.py      # シーン単位ドキュメント生成
        ├── build_keyframe_knowledge.py  # キーフレーム単位に変換
        ├── upload_to_vectorstore.py     # ベクターストアへアップロード
        ├── face_name_aliases.json.sample # 人物名エイリアス設定例
        └── output/                 # 生成ドキュメント（.gitignore 対象）
```

---

<a name="english"></a>

# Video Scene Search

A demo application for searching video scenes using natural language, powered by Azure AI Foundry and GPT-4.1 Vision.

## Overview

Videos are analyzed by Azure Video Indexer to extract keyframes. GPT-4.1 Vision generates detailed descriptions of each keyframe, which are indexed in a vector store. An AI agent returns the most relevant scenes in response to natural language queries.

## Features

- Natural language video scene search
- Azure AI Foundry Agent integration (File Search / Vector Store)
- **Microsoft Entra ID authentication** (no API key required — uses `az login` or Managed Identity)
- Play video at the matched scene timestamp

## Tech Stack

| Component | Technology |
|---|---|
| Web App | ASP.NET Core 8 Razor Pages |
| AI Agent | Azure AI Foundry Agent (File Search) |
| Image Analysis | Azure OpenAI GPT-4.1 Vision |
| Video Analysis | Azure Video Indexer |
| Vector Search | Azure AI Foundry Vector Store |
| Auth | Azure.Identity / DefaultAzureCredential |

## Prerequisites

- .NET 8 SDK
- Azure CLI (logged in with `az login`)
- Azure AI Foundry project with an agent created
- Python 3.9+ (for batch processing)

## Quick Start

```bash
git clone https://github.com/your-username/video-scene-search.git
cd video-scene-search/VideoSceneSearch

dotnet user-secrets set "AzureAIFoundry:Endpoint" "https://your-endpoint"
dotnet user-secrets set "AzureAIFoundry:Scope" "https://cognitiveservices.azure.com/.default"

dotnet run
```

See [CONFIGURATION.md](VideoSceneSearch/CONFIGURATION.md) for full options.

## Batch Pipeline

### 1. Analyze keyframes

```powershell
.\run-batch.ps1 -InputDir "input\MyVideo" -OutputDir "output\MyVideo" `
    -SchemaFile "FeldSchema_sample.json" -VideoTitle "My Video" `
    -ResourceEndpoint "https://your-resource.services.ai.azure.com"
```

### 2. Build knowledge documents

```bash
python build_knowledge.py --scene-facts ... --cu-output ... --scene-output ... --keyframe-output ...
```

### 3. Upload to vector store

```bash
python upload_to_vectorstore.py --file keyframe_docs.json --vector-store-id vs_xxx
```

### 4. Register video mapping

Add your video to `videomapping.json`:

```json
{
  "videos": {
    "your-video-id": {
      "title": "Your Video Title",
      "file": "/videos/your-video.mp4",
      "thumbnail": ""
    }
  }
}
```

## License

MIT

## セットアップ

### 前提条件

1. .NET 8 SDK がインストールされていること
2. Azure CLI がインストールされていること（ローカル開発時）
3. Azure AI Foundry でエージェントが作成・発行済みであること
4. 必要な Azure 権限があること

### インストール手順

1. **リポジトリのクローン**

```bash
git clone https://github.com/tatatatamami/sample-video-scene-search.git
cd sample-video-scene-search
```

2. **設定ファイルの作成**

`VideoSceneSearch/appsettings.json` を編集し、Azure AI Foundry のエンドポイントを設定します：

```json
{
  "AzureAIFoundry": {
    "Endpoint": "https://your-foundry-project.azure.ai/agent/response/YOUR_AGENT_ID",
    "Scope": "https://cognitiveservices.azure.com/.default"
  }
}
```

3. **Azure CLI でログイン**（ローカル開発時）

```bash
az login
```

4. **アプリケーションのビルドと実行**

```bash
cd VideoSceneSearch
dotnet restore
dotnet run
```

アプリケーションは `http://localhost:5000` で起動します。

## 動画マッピングの設定

`Pages/Index.cshtml` の JavaScript セクションで、動画 ID と URL のマッピングを設定します：

```javascript
const videoMapping = {
    'video1': 'https://your-storage.blob.core.windows.net/videos/video1.mp4',
    'video2': 'https://your-storage.blob.core.windows.net/videos/video2.mp4',
    // 追加の動画マッピング
};
```

## 使い方

1. ブラウザで `http://localhost:5000` にアクセス
2. 検索ボックスに自然言語でクエリを入力（例：「会議のシーン」「製品デモ」）
3. 「検索」ボタンをクリック
4. 検索結果が JSON 形式と視覚的なカードで表示されます
5. 「動画を再生」ボタンをクリックして、指定されたタイムスタンプから動画を再生

## プロジェクト構造

```
VideoSceneSearch/
├── Models/
│   ├── AzureAIFoundrySettings.cs    # Azure AI Foundry 設定
│   ├── SearchRequest.cs              # 検索リクエストモデル
│   └── SceneSearchResult.cs          # 検索結果モデル
├── Services/
│   └── FoundryAgentClient.cs         # Foundry Agent クライアント
├── Pages/
│   ├── Index.cshtml                  # メインページ UI
│   ├── Index.cshtml.cs               # ページモデル
│   └── _ViewImports.cshtml           # Razor Pages 設定
├── wwwroot/
│   └── css/
│       └── site.css                  # スタイルシート
├── Program.cs                        # アプリケーションエントリポイント
├── appsettings.json                  # 設定ファイル
└── VideoSceneSearch.csproj           # プロジェクトファイル
```

## Azure へのデプロイ

### App Service へのデプロイ

1. **App Service の作成**

```bash
az webapp create \
  --name your-app-name \
  --resource-group your-resource-group \
  --plan your-app-service-plan \
  --runtime "DOTNET|8.0"
```

2. **Managed Identity の有効化**

```bash
az webapp identity assign \
  --name your-app-name \
  --resource-group your-resource-group
```

3. **Azure AI Foundry への権限付与**

Managed Identity に対して、Azure AI Foundry リソースへの適切な権限（Cognitive Services User など）を付与します。

4. **アプリケーションのデプロイ**

```bash
dotnet publish -c Release
az webapp deployment source config-zip \
  --name your-app-name \
  --resource-group your-resource-group \
  --src ./bin/Release/net8.0/publish.zip
```

5. **App Settings の設定**

```bash
az webapp config appsettings set \
  --name your-app-name \
  --resource-group your-resource-group \
  --settings AzureAIFoundry__Endpoint="https://your-foundry-project.azure.ai/agent/response/YOUR_AGENT_ID"
```

## トラブルシューティング

### 認証エラー

- `az login` が正しく実行されているか確認
- Azure ポータルで Managed Identity に適切な権限が付与されているか確認
- `Scope` 設定が正しいか確認（デフォルト: `https://cognitiveservices.azure.com/.default`）

### 動画が再生されない

- `videoMapping` に正しい動画 URL が設定されているか確認
- 動画ファイルへのアクセス権限があるか確認
- ブラウザのコンソールでエラーメッセージを確認

### エージェントからのレスポンスエラー

- Azure AI Foundry のエンドポイント URL が正しいか確認
- エージェントが正しくデプロイされているか確認
- ログを確認してエラーの詳細を把握

## 対象外（Non-Goals）

このプロジェクトでは以下は実装していません：

- Azure AI Search を直接呼び出す実装
- エンドユーザー認証・認可
- 動画のアップロード・加工・インデックス作成
- SAS トークン発行
- サムネイル生成

## ライセンス

MIT License

## 貢献

Issue や Pull Request を歓迎します！
