#pragma warning disable OPENAI001

using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Microsoft.Extensions.Options;
using OpenAI.Responses;
using System.Text;
using VideoSceneSearch.Models;

namespace VideoSceneSearch.Services;

public interface IFoundryAgentClient
{
    Task<string> SearchScenesAsync(string query, Dictionary<string, string> availableVideos, CancellationToken cancellationToken = default);
}

/// <summary>
/// Azure AI Foundry エージェントを Responses API で呼び出すクライアント。
/// AIProjectClient を介して ProjectResponsesClient を取得し、
/// Microsoft Entra ID (DefaultAzureCredential) でキーレス認証を行います。
/// </summary>
public class FoundryAgentClient : IFoundryAgentClient
{
    private readonly ProjectResponsesClient _responsesClient;
    private readonly string _agentName;
    private readonly ILogger<FoundryAgentClient> _logger;

    public FoundryAgentClient(
        IOptions<AzureAIFoundrySettings> settings,
        ILogger<FoundryAgentClient> logger)
    {
        _logger = logger;
        var s = settings.Value;
        _agentName = s.AgentName;

        // プロジェクトエンドポイントと DefaultAzureCredential で AIProjectClient を作成します。
        // エンドポイント形式: https://{resource}.services.ai.azure.com/api/projects/{project}
        var projectClient = new AIProjectClient(
            endpoint: new Uri(s.Endpoint),
            tokenProvider: new Azure.Identity.DefaultAzureCredential());

        // エージェント名を指定して ProjectResponsesClient を取得します。
        // エージェントの model・instructions・tools はサービス側の定義が使用されます。
        _responsesClient = projectClient.ProjectOpenAIClient
            .GetProjectResponsesClientForAgent(s.AgentName);
    }

    public async Task<string> SearchScenesAsync(
        string query,
        Dictionary<string, string> availableVideos,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Sending request to Foundry Agent (agent: {AgentName})", _agentName);

        // 利用可能な動画一覧をコンテキストとして付与します。
        string message;
        if (availableVideos.Count > 0)
        {
            var videoList = string.Join("\n", availableVideos.Select(v => $"- {v.Key}: {v.Value}"));
            message = $"Available videos:\n{videoList}\n\nUser query: {query}";
        }
        else
        {
            message = query;
        }

        var textBuilder = new StringBuilder();

        // Responses API でストリーミング応答を受信します。
        // 各ターンは単発リクエストとして送信されます。
        // 会話履歴を跨いだ継続が必要な場合は PreviousResponseId を使用してください。
        await foreach (var update in _responsesClient
            .CreateResponseStreamingAsync(message)
            .WithCancellation(cancellationToken))
        {
            if (update is StreamingResponseOutputTextDeltaUpdate textDelta)
            {
                textBuilder.Append(textDelta.Delta);
            }
        }

        var result = textBuilder.ToString();
        _logger.LogInformation("Received response from Foundry Agent ({Length} chars)", result.Length);

        return result;
    }
}
