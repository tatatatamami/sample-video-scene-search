# Configuration Guide / 設定ガイド

This document explains how to configure the application for local development and production use.  
このドキュメントでは、ローカル開発および本番環境での設定方法を説明します。

---

## Required Settings / 必要な設定値

| Key | Description | Example |
|-----|-------------|---------|
| `AzureAIFoundry:Endpoint` | Azure AI Foundry Agent API endpoint URL | `https://your-resource.services.ai.azure.com/api/projects/your-project/applications/your-agent/protocols/openai/responses?api-version=2025-11-15-preview` |
| `AzureAIFoundry:Scope` | Authentication scope | `https://cognitiveservices.azure.com/.default` |

---

## Method 1: User Secrets (Recommended for local development) / 方法1: User Secrets（推奨）

```bash
cd VideoSceneSearch
dotnet user-secrets init
dotnet user-secrets set "AzureAIFoundry:Endpoint" "https://your-endpoint"
dotnet user-secrets set "AzureAIFoundry:Scope" "https://cognitiveservices.azure.com/.default"

# Verify / 確認
dotnet user-secrets list

# Remove / 削除
dotnet user-secrets clear
```

---

## Method 2: Environment Variables / 方法2: 環境変数

### Windows (PowerShell)
```powershell
# Temporary (current session only) / 一時的（現在のセッションのみ）
$env:AzureAIFoundry__Endpoint = "https://your-endpoint"
$env:AzureAIFoundry__Scope    = "https://cognitiveservices.azure.com/.default"

# Permanent / 永続的
[System.Environment]::SetEnvironmentVariable('AzureAIFoundry__Endpoint', 'https://your-endpoint', 'User')
[System.Environment]::SetEnvironmentVariable('AzureAIFoundry__Scope', 'https://cognitiveservices.azure.com/.default', 'User')
```

### macOS / Linux
```bash
export AzureAIFoundry__Endpoint="https://your-endpoint"
export AzureAIFoundry__Scope="https://cognitiveservices.azure.com/.default"
```

> **Note:** Use double underscores `__` (not `:`) as the separator for environment variable names.  
> **注意:** 環境変数名の区切り文字は `:` ではなく `__`（ダブルアンダースコア）を使用してください。

---

## Method 3: launchSettings.json (Visual Studio / dotnet run)

Edit `Properties/launchSettings.json` and add to the `environmentVariables` section:

```json
{
  "environmentVariables": {
    "ASPNETCORE_ENVIRONMENT": "Development",
    "AzureAIFoundry__Endpoint": "https://your-endpoint",
    "AzureAIFoundry__Scope": "https://cognitiveservices.azure.com/.default"
  }
}
```

> **Warning:** `launchSettings.json` is excluded from Git (`.gitignore`). Do not commit secrets.

---

## Method 4: appsettings.Development.json / 方法4: appsettings.Development.json

```json
{
  "AzureAIFoundry": {
    "Endpoint": "https://your-endpoint",
    "Scope": "https://cognitiveservices.azure.com/.default"
  }
}
```

> **Warning:** `appsettings.Development.json` is excluded from Git (`.gitignore`). Do not commit secrets.

---

## Configuration Priority / 設定の優先順位（ASP.NET Core）

Settings are loaded in this order (later overrides earlier):

1. `appsettings.json`
2. `appsettings.{Environment}.json`
3. User Secrets (Development only)
4. Environment Variables
5. Command-line arguments

---

## Batch Pipeline Settings / バッチパイプラインの設定

The batch analysis pipeline (`Batch/`) also requires an Azure OpenAI endpoint.

### Option A: Environment Variable
```powershell
$env:AZURE_OPENAI_ENDPOINT = "https://your-resource.services.ai.azure.com"
```

### Option B: Pass as parameter
```powershell
.\run-batch.ps1 -InputDir "input\MyVideo" -OutputDir "output\MyVideo" `
    -SchemaFile "FeldSchema_sample.json" -VideoTitle "My Video" `
    -ResourceEndpoint "https://your-resource.services.ai.azure.com"
```

Authentication uses `az login` (Azure CLI). Run `az login` before executing batch scripts.

---

## Troubleshooting / トラブルシューティング

**Settings not loading / 設定が反映されない場合:**
- Verify environment variable separator is `__` (double underscore), not `:`
- Restart the application after changes
- Run `dotnet user-secrets list` to verify User Secrets

**Authentication errors / 認証エラーが発生する場合:**
```bash
az login
az account show
az account set --subscription "your-subscription-id"
```
