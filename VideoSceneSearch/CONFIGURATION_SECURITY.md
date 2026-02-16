# Configuration Guide

This document explains how to securely configure the application for local development and testing.

## ?? Security Best Practices

**IMPORTANT**: Never commit sensitive information (API keys, endpoints, secrets) to Git repositories.

### Recommended Configuration Methods (in order of security):
1. **User Secrets** (most secure for local development)
2. **Environment Variables**
3. **Azure Key Vault** (for production)

## Method 1: User Secrets (Recommended)

User Secrets provide secure local storage for sensitive configuration.

### Setup Steps

1. Navigate to project directory:

```bash
cd VideoSceneSearch
dotnet user-secrets init
```

2. Add your configuration:

```bash
dotnet user-secrets set "AzureAIFoundry:Endpoint" "https://YOUR-RESOURCE.services.ai.azure.com/api/projects/YOUR-PROJECT/applications/YOUR-APP/protocols/openai/responses?api-version=2025-11-15-preview"
dotnet user-secrets set "AzureAIFoundry:Scope" "https://ml.azure.com/.default"
# Optional: Add API Key (if not using Entra ID)
dotnet user-secrets set "AzureAIFoundry:ApiKey" "YOUR-API-KEY"
```

3. Verify configuration:

```bash
dotnet user-secrets list
```

4. Remove secrets (if needed):

```bash
dotnet user-secrets remove "AzureAIFoundry:Endpoint"
# Or clear all
dotnet user-secrets clear
```

## Method 2: Environment Variables

### Windows (PowerShell)

Temporary (current session only):
```powershell
$env:AzureAIFoundry__Endpoint = "https://YOUR-ENDPOINT"
$env:AzureAIFoundry__Scope = "https://ml.azure.com/.default"
```

Permanent:
```powershell
[System.Environment]::SetEnvironmentVariable('AzureAIFoundry__Endpoint', 'https://YOUR-ENDPOINT', 'User')
[System.Environment]::SetEnvironmentVariable('AzureAIFoundry__Scope', 'https://ml.azure.com/.default', 'User')
```

### macOS / Linux

Temporary:
```bash
export AzureAIFoundry__Endpoint="https://YOUR-ENDPOINT"
export AzureAIFoundry__Scope="https://ml.azure.com/.default"
```

Permanent (add to ~/.bashrc or ~/.zshrc):
```bash
echo 'export AzureAIFoundry__Endpoint="https://YOUR-ENDPOINT"' >> ~/.bashrc
source ~/.bashrc
```

## Method 3: appsettings.Development.json (Local Only)

?? **WARNING**: This file should be in `.gitignore`. Never commit it to Git.

1. Copy the sample file:
```bash
cp appsettings.Development.json.sample appsettings.Development.json
```

2. Edit `appsettings.Development.json` with your actual values:
```json
{
  "AzureAIFoundry": {
    "Endpoint": "https://YOUR-RESOURCE.services.ai.azure.com/...",
    "ApiKey": "YOUR-API-KEY",
    "Scope": "https://ml.azure.com/.default"
  }
}
```

## Configuration Priority

ASP.NET Core loads configuration in this order (later sources override earlier):
1. `appsettings.json` (base configuration, committed to Git)
2. `appsettings.Development.json` (excluded from Git)
3. User Secrets (Development environment only)
4. Environment Variables
5. Command-line arguments

## Verification

Run the application and check the logs to verify configuration is loaded correctly:

```bash
dotnet run
```

Look for log messages indicating successful configuration loading.

## For Production

Use Azure Key Vault or Azure App Configuration for production deployments. Never use `appsettings.json` or environment variables for production secrets.
