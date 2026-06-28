# Video Scene Search Agent

A hosted agent for natural language video scene search, built on the [Agent Framework](https://github.com/microsoft/agent-framework) with the Responses protocol in C#.

The agent receives a user query, autonomously calls Azure AI Search via Foundry Toolbox (MCP), and returns a structured JSON response with matching scenes, timestamps, and evidence.

**Architecture:**
```
Web App  →  Hosted Agent  →  Toolbox MCP  →  Azure AI Search
```

## Running the Agent Host Locally

```bash
cd video-scene-search
azd auth login
azd ai agent run video-scene-search
```

Or run directly with `dotnet`:

```bash
cd src/video-scene-search
dotnet run
```

## Interacting with the Agent

Send a search query using `azd`:

```bash
azd ai agent invoke --local "空から落ちるシーンを探してください"
```

Or use `curl`:

```bash
curl -X POST http://localhost:8088/responses \
  -H "Content-Type: application/json" \
  -d '{"input": "空から落ちるシーンを探してください", "stream": false}'
```

The agent returns a JSON response with matching scenes. Use the returned `id` for multi-turn conversations:

```bash
curl -X POST http://localhost:8088/responses \
  -H "Content-Type: application/json" \
  -d '{"input": "そのシーンの詳細を教えてください", "previous_response_id": "REPLACE_WITH_PREVIOUS_ID", "stream": false}'
```

## Deploying the Agent to Foundry

```bash
cd video-scene-search
azd deploy
```

Or use the **Foundry Toolkit** VS Code extension:

1. Open the Command Palette (`Ctrl+Shift+P`) and run **Foundry Toolkit: Deploy Hosted Agent**.
2. Select your Foundry project (or create a new one).
3. Confirm runtime settings and click **Deploy**.
4. After deployment, invoke the agent from the Agent Playground.

## Environment Variables

| Variable | Required | Description |
|----------|----------|-------------|
| `FOUNDRY_PROJECT_ENDPOINT` | Yes (auto-injected) | Foundry project endpoint |
| `AZURE_AI_MODEL_DEPLOYMENT_NAME` | Yes | Model deployment name (e.g., `gpt-4.1`) |
| `TOOLBOX_NAME` | Yes | Foundry Toolbox name (e.g., `video-scene-toolbox`) |
| `TOOLBOX_ENDPOINT` | No | Full Toolbox MCP URL (takes priority over `TOOLBOX_NAME`) |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | No (auto-injected) | Application Insights connection string |

