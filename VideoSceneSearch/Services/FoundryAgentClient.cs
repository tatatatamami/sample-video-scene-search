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
    private readonly IAzureSearchService _searchService;
    private readonly ILogger<FoundryAgentClient> _logger;

    // Structured Output schema — agent must return JSON matching this shape.
    private static readonly BinaryData _responseSchema = BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "scenes": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "videoId":    { "type": "string" },
                  "title":      { "type": "string" },
                  "start":      { "type": "string" },
                  "end":        { "type": "string" },
                  "confidence": { "type": "number" },
                  "evidence":   { "type": "string" },
                  "mode":        { "anyOf": [{ "type": "string" }, { "type": "null" }] },
                  "location":    { "anyOf": [{ "type": "string" }, { "type": "null" }] },
                  "tags":        { "anyOf": [{ "type": "string" }, { "type": "null" }] },
                  "actions":     { "anyOf": [{ "type": "string" }, { "type": "null" }] },
                  "description": { "anyOf": [{ "type": "string" }, { "type": "null" }] }
                },
                "required": ["videoId", "title", "start", "end", "confidence", "evidence",
                             "mode", "location", "tags", "actions", "description"],
                "additionalProperties": false
              }
            }
          },
          "required": ["scenes"],
          "additionalProperties": false
        }
        """);

    public FoundryAgentClient(
        IOptions<AzureAIFoundrySettings> settings,
        IAzureSearchService searchService,
        ILogger<FoundryAgentClient> logger)
    {
        _settings = settings.Value;
        _searchService = searchService;
        _logger = logger;

        // Microsoft Entra ID を使用したキーレス認証。
        // Azure AI Foundry のスコープは https://ai.azure.com/.default を使用します。
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

        // Azure AI Search でシーンを事前検索し、取得したコンテキストをエージェントに渡す
        var retrievedContext = await _searchService.SearchAsync(query, cancellationToken: cancellationToken);

        // Build message with available video context and retrieved search results
        string message;
        if (availableVideos.Count > 0)
        {
            var videoList = string.Join("\n", availableVideos.Select(v => $"- {v.Key}: {v.Value}"));
            message = $"Available videos:\n{videoList}\n\n[Azure AI Search 取得済みコンテキスト]\n{retrievedContext}[/ Azure AI Search 取得済みコンテキスト]\n\nUser query: {query}";
        }
        else
        {
            message = $"[Azure AI Search 取得済みコンテキスト]\n{retrievedContext}[/ Azure AI Search 取得済みコンテキスト]\n\nUser query: {query}";
        }

        var options = new CreateResponseOptions
        {
            Model = _settings.ModelDeploymentName,
            TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                    "scene_search_response",
                    _responseSchema,
                    null,
                    jsonSchemaIsStrict: true)
            }
        };
        options.InputItems.Add(ResponseItem.CreateUserMessageItem(message));

        var response = (await _responsesClient.CreateResponseAsync(options, cancellationToken)).Value;

        _logger.LogInformation(
            "Response from Foundry Hosted Agent: id={Id}, status={Status}, totalTokens={Tokens}",
            response.Id, response.Status, response.Usage?.TotalTokenCount);

        if (response.Status == ResponseStatus.Failed)
        {
            throw new InvalidOperationException(
                $"Agent returned failed status: {response.Error?.Code} - {response.Error?.Message}");
        }

        if (response.Status == ResponseStatus.Incomplete)
        {
            throw new InvalidOperationException(
                $"Agent response was incomplete: {response.IncompleteStatusDetails?.Reason}");
        }

        var textBuilder = new StringBuilder();
        foreach (var item in response.OutputItems)
        {
            if (item is MessageResponseItem msg)
            {
                foreach (var part in msg.Content)
                {
                    if (part.Kind == ResponseContentPartKind.OutputText)
                        textBuilder.Append(part.Text);
                    else if (part.Kind == ResponseContentPartKind.Refusal)
                        throw new InvalidOperationException($"Agent refused to answer: {part.Refusal}");
                }
            }
        }

        var result = textBuilder.ToString();
        _logger.LogInformation("Extracted text from response ({Length} chars)", result.Length);

        if (string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidOperationException("Agent returned no output text.");
        }

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
        // Hosted Agent refreshed preview で必須のヘッダー
        request.Headers.Set("Foundry-Features", "HostedAgents=V1Preview");

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

