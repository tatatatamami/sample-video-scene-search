#pragma warning disable OPENAI001

using Azure.Identity;
using Microsoft.Extensions.Options;
using OpenAI.Responses;
using System.ClientModel.Primitives;
using System.Text;
using VideoSceneSearch.Models;

namespace VideoSceneSearch.Services;

public interface IFoundryAgentClient
{
    Task<string> SearchScenesAsync(string query, Dictionary<string, string> availableVideos, CancellationToken cancellationToken = default);
}

/// <summary>
/// Azure AI Foundry ホステッドエージェントを OpenAI Responses API で呼び出すクライアント。
/// Microsoft Entra ID (DefaultAzureCredential) を使用してキーレス認証を行います。
/// </summary>
public class FoundryAgentClient : IFoundryAgentClient
{
    private readonly ResponsesClient _responsesClient;
    private readonly AzureAIFoundrySettings _settings;
    private readonly ILogger<FoundryAgentClient> _logger;

    public FoundryAgentClient(
        IOptions<AzureAIFoundrySettings> settings,
        ILogger<FoundryAgentClient> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        // Microsoft Entra ID を使用したキーレス認証。
        // Azure AI Foundry のスコープは https://ai.azure.com/.default を使用します。
        // 参考: https://learn.microsoft.com/azure/ai-foundry/
        var tokenPolicy = new BearerTokenPolicy(
            new DefaultAzureCredential(),
            "https://ai.azure.com/.default");

        // エンドポイントは "/responses" を除いたベース URL を設定します。
        // 例: https://{resource}.services.ai.azure.com/api/projects/{project}/agents/{agent-name}/endpoint/protocols/openai
        // Azure AI Foundry のホステッドエージェント (Responses プロトコル) は api-version=v1 クエリパラメータが必須です。
        var options = new ResponsesClientOptions { Endpoint = new Uri(_settings.Endpoint) };
        options.AddPolicy(new FoundryApiVersionPolicy(), PipelinePosition.BeforeTransport);
        _responsesClient = new ResponsesClient(tokenPolicy, options);
    }

    public async Task<string> SearchScenesAsync(
        string query,
        Dictionary<string, string> availableVideos,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Sending request to Foundry Hosted Agent");

        // Build message with available video context so the agent can reference videos by ID.
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

        var options = new CreateResponseOptions
        {
            Model = _settings.ModelDeploymentName,
            StreamingEnabled = true
        };
        options.InputItems.Add(ResponseItem.CreateUserMessageItem(message));

        var textBuilder = new StringBuilder();

        await foreach (var update in _responsesClient.CreateResponseStreamingAsync(options)
            .WithCancellation(cancellationToken))
        {
            if (update is StreamingResponseOutputTextDeltaUpdate textDelta)
            {
                textBuilder.Append(textDelta.Delta);
            }
        }

        var result = textBuilder.ToString();
        _logger.LogInformation("Received response from Foundry Hosted Agent ({Length} chars)", result.Length);

        return result;
    }
}

/// <summary>
/// Azure AI Foundry のホステッドエージェント (Responses プロトコル) が要求する
/// api-version=v1 クエリパラメータをすべてのリクエストに付与するパイプラインポリシー。
/// </summary>
internal sealed class FoundryApiVersionPolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        AppendApiVersion(message.Request);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        AppendApiVersion(message.Request);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private static void AppendApiVersion(PipelineRequest request)
    {
        var uriStr = request.Uri?.AbsoluteUri;
        if (uriStr is null) return;

        // api-version が未設定のリクエストのみ追加する
        if (!uriStr.Contains("api-version=", StringComparison.OrdinalIgnoreCase))
        {
            var separator = uriStr.Contains('?') ? "&" : "?";
            request.Uri = new Uri(uriStr + separator + "api-version=v1");
        }
    }
}
