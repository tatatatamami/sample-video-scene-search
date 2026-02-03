# 開発とデプロイのガイド

## ローカル開発環境のセットアップ

### 前提条件

1. .NET 8 SDK
2. Azure CLI
3. Azure AI Foundry で作成済みのエージェント

### 設定手順

1. **Azure CLI でログイン**

```bash
az login
```

2. **設定ファイルの編集**

`VideoSceneSearch/appsettings.json` を編集：

```json
{
  "AzureAIFoundry": {
    "Endpoint": "https://your-project.azure.ai/agent/response/your-agent-id",
    "Scope": "https://cognitiveservices.azure.com/.default"
  }
}
```

3. **アプリケーションの実行**

```bash
cd VideoSceneSearch
dotnet restore
dotnet run
```

ブラウザで `http://localhost:5000` にアクセスしてください。

## Azure App Service へのデプロイ

### 1. App Service の作成

```bash
# リソースグループの作成
az group create --name rg-video-search --location japaneast

# App Service Plan の作成
az appservice plan create \
  --name plan-video-search \
  --resource-group rg-video-search \
  --sku B1 \
  --is-linux

# Web App の作成
az webapp create \
  --name app-video-search-unique \
  --resource-group rg-video-search \
  --plan plan-video-search \
  --runtime "DOTNET|8.0"
```

### 2. Managed Identity の設定

```bash
# システム割り当てマネージド ID の有効化
az webapp identity assign \
  --name app-video-search-unique \
  --resource-group rg-video-search

# 出力された principalId をメモ
```

### 3. Azure AI Foundry への権限付与

Azure Portal で以下を実施：

1. Azure AI Foundry リソースに移動
2. 「アクセス制御 (IAM)」を選択
3. 「ロールの割り当ての追加」をクリック
4. 「Cognitive Services User」ロールを選択
5. Managed Identity を選択し、先ほどの principalId を指定

または Azure CLI で：

```bash
# Azure AI Foundry リソースの ID を取得
FOUNDRY_ID=$(az cognitiveservices account show \
  --name your-foundry-resource \
  --resource-group your-foundry-rg \
  --query id -o tsv)

# Managed Identity の principalId を取得
PRINCIPAL_ID=$(az webapp identity show \
  --name app-video-search-unique \
  --resource-group rg-video-search \
  --query principalId -o tsv)

# ロールの割り当て
az role assignment create \
  --assignee $PRINCIPAL_ID \
  --role "Cognitive Services User" \
  --scope $FOUNDRY_ID
```

### 4. アプリケーション設定の構成

```bash
az webapp config appsettings set \
  --name app-video-search-unique \
  --resource-group rg-video-search \
  --settings \
    AzureAIFoundry__Endpoint="https://your-project.azure.ai/agent/response/your-agent-id" \
    AzureAIFoundry__Scope="https://cognitiveservices.azure.com/.default"
```

### 5. アプリケーションのデプロイ

```bash
cd VideoSceneSearch

# 発行
dotnet publish -c Release -o ./publish

# ZIP ファイルの作成
cd publish
zip -r ../publish.zip .
cd ..

# デプロイ
az webapp deployment source config-zip \
  --name app-video-search-unique \
  --resource-group rg-video-search \
  --src publish.zip
```

### 6. デプロイの確認

```bash
# アプリケーションの URL を取得
az webapp show \
  --name app-video-search-unique \
  --resource-group rg-video-search \
  --query defaultHostName -o tsv
```

ブラウザで表示された URL にアクセスしてください。

## 動画マッピングのカスタマイズ

Azure Blob Storage に動画を配置している場合：

1. `Pages/Index.cshtml` を編集
2. `videoMapping` オブジェクトを更新：

```javascript
const videoMapping = {
    'video1': 'https://yourstorageaccount.blob.core.windows.net/videos/video1.mp4',
    'video2': 'https://yourstorageaccount.blob.core.windows.net/videos/video2.mp4',
    // SAS トークンが必要な場合
    'video3': 'https://yourstorageaccount.blob.core.windows.net/videos/video3.mp4?sp=r&st=...',
};
```

## トラブルシューティング

### 認証エラー

**エラー**: "DefaultAzureCredential failed to retrieve a token"

**解決方法**:
- ローカル開発: `az login` を実行
- Azure: Managed Identity が有効化されており、適切な権限があることを確認

### ログの確認

App Service のログストリーミング：

```bash
az webapp log tail \
  --name app-video-search-unique \
  --resource-group rg-video-search
```

### アプリケーション設定の確認

```bash
az webapp config appsettings list \
  --name app-video-search-unique \
  --resource-group rg-video-search
```

## CI/CD パイプライン（GitHub Actions 例）

`.github/workflows/deploy.yml`:

```yaml
name: Deploy to Azure App Service

on:
  push:
    branches: [ main ]

jobs:
  build-and-deploy:
    runs-on: ubuntu-latest
    
    steps:
    - uses: actions/checkout@v3
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: '8.0.x'
    
    - name: Build
      run: |
        cd VideoSceneSearch
        dotnet restore
        dotnet build -c Release
        dotnet publish -c Release -o ./publish
    
    - name: Deploy to Azure
      uses: azure/webapps-deploy@v2
      with:
        app-name: 'app-video-search-unique'
        publish-profile: ${{ secrets.AZURE_WEBAPP_PUBLISH_PROFILE }}
        package: VideoSceneSearch/publish
```

## セキュリティベストプラクティス

1. **appsettings.json に機密情報を含めない**
   - 環境変数または Azure App Configuration を使用

2. **HTTPS を強制する**
   ```bash
   az webapp update \
     --name app-video-search-unique \
     --resource-group rg-video-search \
     --https-only true
   ```

3. **最小権限の原則**
   - Managed Identity には必要最小限の権限のみを付与

4. **動画 URL の保護**
   - 可能な場合は SAS トークンを使用
   - 有効期限を設定

## パフォーマンスの最適化

### App Service のスケーリング

```bash
# スケールアップ
az appservice plan update \
  --name plan-video-search \
  --resource-group rg-video-search \
  --sku P1V2

# オートスケール設定
az monitor autoscale create \
  --resource-group rg-video-search \
  --resource app-video-search-unique \
  --resource-type Microsoft.Web/sites \
  --name autoscale-video-search \
  --min-count 1 \
  --max-count 3 \
  --count 1
```

### Application Insights の有効化

```bash
# Application Insights の作成
az monitor app-insights component create \
  --app insights-video-search \
  --location japaneast \
  --resource-group rg-video-search

# インストルメンテーションキーの取得
INSTRUMENTATION_KEY=$(az monitor app-insights component show \
  --app insights-video-search \
  --resource-group rg-video-search \
  --query instrumentationKey -o tsv)

# App Service に設定
az webapp config appsettings set \
  --name app-video-search-unique \
  --resource-group rg-video-search \
  --settings APPINSIGHTS_INSTRUMENTATIONKEY=$INSTRUMENTATION_KEY
```

## コスト管理

- **App Service**: B1 プラン（約 1,500円/月）からスタート
- **Application Insights**: 最初の 5GB は無料
- **Azure AI Foundry**: 使用量ベースの課金

不要なリソースの削除：

```bash
az group delete --name rg-video-search --yes --no-wait
```
