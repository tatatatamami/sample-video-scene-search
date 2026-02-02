# テスト環境の設定方法

このドキュメントでは、ローカル開発環境でテストするための環境変数の設定方法を説明します。

## 方法1: User Secrets（推奨）

機密情報を安全に管理するために、User Secretsを使用します。

### 設定手順

1. プロジェクトディレクトリで以下のコマンドを実行：

```bash
cd VideoSceneSearch
dotnet user-secrets init
```

2. 必要な設定値を追加：

```bash
dotnet user-secrets set "AzureAIFoundry:Endpoint" "https://se3-tamamiihori-1-1-resource.services.ai.azure.com/api/projects/se3-tamamiihori-1-1/applications/test2/protocols/openai/responses?api-version=2025-11-15-preview"
dotnet user-secrets set "AzureAIFoundry:Scope" "https://cognitiveservices.azure.com/.default"
```

3. 設定を確認：

```bash
dotnet user-secrets list
```

4. 設定を削除する場合：

```bash
dotnet user-secrets remove "AzureAIFoundry:Endpoint"
# または、すべてクリア
dotnet user-secrets clear
```

## 方法2: 環境変数

### Windows (PowerShell)

一時的に設定（現在のセッションのみ）：
```powershell
$env:AzureAIFoundry__Endpoint = "https://your-foundry-endpoint.azure.ai/agent/response"
$env:AzureAIFoundry__Scope = "https://cognitiveservices.azure.com/.default"
```

永続的に設定：
```powershell
[System.Environment]::SetEnvironmentVariable('AzureAIFoundry__Endpoint', 'https://your-foundry-endpoint.azure.ai/agent/response', 'User')
[System.Environment]::SetEnvironmentVariable('AzureAIFoundry__Scope', 'https://cognitiveservices.azure.com/.default', 'User')
```

### Windows (コマンドプロンプト)

```cmd
set AzureAIFoundry__Endpoint=https://your-foundry-endpoint.azure.ai/agent/response
set AzureAIFoundry__Scope=https://cognitiveservices.azure.com/.default
```

### macOS / Linux

一時的に設定：
```bash
export AzureAIFoundry__Endpoint="https://your-foundry-endpoint.azure.ai/agent/response"
export AzureAIFoundry__Scope="https://cognitiveservices.azure.com/.default"
```

永続的に設定（~/.bashrc または ~/.zshrc に追加）：
```bash
echo 'export AzureAIFoundry__Endpoint="https://your-foundry-endpoint.azure.ai/agent/response"' >> ~/.bashrc
echo 'export AzureAIFoundry__Scope="https://cognitiveservices.azure.com/.default"' >> ~/.bashrc
source ~/.bashrc
```

## 方法3: launchSettings.json

Visual Studioまたは`dotnet run`でデバッグ実行する場合、`Properties/launchSettings.json`ファイルの環境変数セクションを編集します：

```json
{
  "environmentVariables": {
    "ASPNETCORE_ENVIRONMENT": "Development",
    "AzureAIFoundry__Endpoint": "https://your-foundry-endpoint.azure.ai/agent/response",
    "AzureAIFoundry__Scope": "https://cognitiveservices.azure.com/.default"
  }
}
```

**注意**: このファイルはGitにコミットされる可能性があるため、機密情報は入れないでください。

## 方法4: appsettings.Development.json

開発環境専用の設定は`appsettings.Development.json`に記載できます：

```json
{
  "AzureAIFoundry": {
    "Endpoint": "https://your-foundry-endpoint.azure.ai/agent/response",
    "Scope": "https://cognitiveservices.azure.com/.default"
  }
}
```

**注意**: このファイルが`.gitignore`に含まれていることを確認してください。

## 設定の優先順位

ASP.NET Coreの設定は以下の順序で読み込まれ、後のものが優先されます：

1. `appsettings.json`
2. `appsettings.{Environment}.json`（例: `appsettings.Development.json`）
3. User Secrets（Development環境のみ）
4. 環境変数
5. コマンドライン引数

## 必要な設定値

| 設定キー | 説明 | 例 |
|---------|------|-----|
| `AzureAIFoundry:Endpoint` | Azure AI Foundry Agent APIのエンドポイントURL | `https://your-foundry-endpoint.azure.ai/agent/response` |
| `AzureAIFoundry:Scope` | 認証スコープ | `https://cognitiveservices.azure.com/.default` |

## 設定の確認

アプリケーションを起動して、設定が正しく読み込まれているか確認してください：

```bash
cd VideoSceneSearch
dotnet run
```

ログに設定エラーが表示されていないことを確認します。

## トラブルシューティング

### 設定が反映されない場合

1. 環境変数名の区切り文字を確認（`:` ではなく `__` を使用）
2. アプリケーションを再起動
3. Visual Studioを使用している場合は、ソリューションを閉じて再度開く
4. `dotnet user-secrets list`で設定を確認

### 認証エラーが発生する場合

Azure CLIで認証されていることを確認：

```bash
az login
az account show
```

必要に応じて、適切なサブスクリプションを選択：

```bash
az account set --subscription "your-subscription-id"
```
