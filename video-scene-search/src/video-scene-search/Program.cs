// Copyright (c) Microsoft. All rights reserved.

/*
 * Video Scene Search - Foundry Hosted Agent (.NET, Agent Framework)
 *
 * Hosted agent for searching video scenes based on natural language queries.
 * Uses Microsoft Agent Framework (Microsoft.Agents.AI) with a Foundry model.
 * The agent receives a user query (optionally with available video context) and
 * returns a JSON response with matching scenes, timestamps, and evidence.
 *
 * Required environment variables:
 *   FOUNDRY_PROJECT_ENDPOINT          - Foundry project endpoint (auto-injected in hosted containers)
 *   AZURE_AI_MODEL_DEPLOYMENT_NAME    - Model deployment name (declared in agent.manifest.yaml)
 *
 * Optional environment variables:
 *   APPLICATIONINSIGHTS_CONNECTION_STRING - Application Insights (auto-injected in hosted containers)
 *
 * Usage:
 *   dotnet run
 *
 *   # Search example:
 *   curl -sS -X POST http://localhost:8088/responses \
 *     -H "Content-Type: application/json" \
 *     -d '{"input": "Mario gets hit by a Goomba", "stream": false}' | jq .
 */

using Azure.AI.AgentServer.Core;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    Console.Error.WriteLine(
        "[WARNING] APPLICATIONINSIGHTS_CONNECTION_STRING not set - traces will not be sent " +
        "to Application Insights. Set it to enable local telemetry. " +
        "(This variable is auto-injected in hosted Foundry containers - do not declare it in agent.manifest.yaml.)");
}

var projectEndpoint = new Uri(Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
    ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT environment variable is not set."));

var deployment = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME")
    ?? throw new InvalidOperationException("AZURE_AI_MODEL_DEPLOYMENT_NAME environment variable is not set.");

const string instructions = """
    You are a video scene search assistant. Your task is to find relevant scenes in video files
    based on user queries.

    The user will provide a search query, optionally preceded by a list of available videos
    in the format "Available videos:\n- <videoId>: <title>\n\nUser query: <query>".

    Search through the available video scene knowledge and return the most relevant scenes.

    IMPORTANT: Always respond with ONLY valid JSON (no markdown code blocks, no extra text).
    Use this exact format:
    {
      "scenes": [
        {
          "videoId": "video-id-matching-available-videos",
          "title": "Video Title",
          "start": "HH:MM:SS",
          "end": "HH:MM:SS",
          "confidence": 0.85,
          "evidence": "Detailed description of why this scene matches the query",
          "mode": "gameplay/cutscene/menu/cinematic",
          "location": "location or area name in the video",
          "tags": "comma,separated,relevant,tags",
          "actions": "key actions or events occurring in this scene",
          "description": "concise scene description"
        }
      ]
    }

    Guidelines:
    - Return 0 to 10 most relevant scenes, ordered by confidence (highest first).
    - confidence should be between 0.0 and 1.0.
    - start and end should be timestamps in HH:MM:SS format.
    - If no relevant scenes are found, return {"scenes": []}.
    - Do NOT include scenes with confidence below 0.3.
    - videoId must match one of the available video IDs provided by the user.
    """;

// Create an AIAgent backed by a Foundry model.
AIAgent agent = new AIProjectClient(projectEndpoint, new DefaultAzureCredential())
    .AsAIAgent(
        model: deployment,
        instructions: instructions,
        name: "video-scene-search",
        description: "Video scene search assistant that returns structured JSON results with timestamps and evidence.");

// AgentHost.CreateBuilder() auto-configures:
//   - Kestrel on port 8088 (or the PORT environment variable)
//   - GET /readiness health probe
//   - OpenTelemetry traces and metrics
//   - x-platform-server response header
var builder = AgentHost.CreateBuilder(args);
builder.Services.AddFoundryResponses(agent);
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
app.Run();