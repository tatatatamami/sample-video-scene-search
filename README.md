# sample-video-scene-search

🎬 動画シーン検索 UI（最小構成）- Azure AI Foundry Agent 利用 / .NET 8 C# / Entra ID 認証

## 概要

このプロジェクトは、Azure AI Foundry 上で作成された「動画シーン検索エージェント」を呼び出し、複数動画メタデータを横断検索して結果を UI に表示するシステムです。

### 主な機能

- 自然言語による動画シーン検索
- Azure AI Foundry Agent との連携（応答 API エンドポイント）
- **Microsoft Entra ID 認証**（API キー不要）
- JSON レスポンスの表示
- 動画の再生とタイムスタンプによるシーク機能

## 技術スタック

- **.NET 8**
- **ASP.NET Core** (Minimal API + Razor Pages)
- **Azure.Identity** (DefaultAzureCredential)
- **HttpClientFactory**
- HTML5 `<video>` タグ

## アーキテクチャ

```
Browser
  ↓
ASP.NET Core (.NET 8)
  ├─ Razor Pages UI
  ├─ Minimal API (/api/scene-search)
  └─ Foundry Agent Client（Entra ID）
        ↓
Azure AI Foundry Agent（応答 API エンドポイント）
```

## 認証方式

⚠️ **重要**: API キーは使用しません。Microsoft Entra ID（OAuth2 Bearer トークン）による認証が必須です。

### トークン取得方法

- **ローカル開発**: `az login` で Azure CLI 認証
- **Azure 上**: Managed Identity を使用

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
