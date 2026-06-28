using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoSceneSearch.Models;

namespace VideoSceneSearch.Services;

public interface IFoundryAgentClient
{
    Task<string> SearchScenesAsync(string query, Dictionary<string, string> availableVideos, CancellationToken cancellationToken = default);
}

/// <summary>
/// Azure AI Foundry Hosted Agent（Responses プロトコル）を HttpClient 経由で呼び出す。
/// エージェントは Foundry Toolbox 経由で AI Search を自律的に呼び出す。
/// </summary>
public class FoundryAgentClient : IFoundryAgentClient
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly AzureAIFoundrySettings _settings;
    private readonly IAzureSearchService _searchService;
    private readonly ILogger<FoundryAgentClient> _logger;
    private readonly TokenCredential _credential;
    private readonly Uri _responsesEndpoint;

    private sealed record AgentPickResponse([property: JsonPropertyName("scenes")] List<AgentPick> Scenes);
    private sealed record AgentPick(
        [property: JsonPropertyName("resultId")] string ResultId,
        [property: JsonPropertyName("evidence")] string Evidence);

    public FoundryAgentClient(
        IOptions<AzureAIFoundrySettings> settings,
        IAzureSearchService searchService,
        ILogger<FoundryAgentClient> logger)
    {
        _settings = settings.Value;
        _searchService = searchService;
        _logger = logger;
        _credential = new DefaultAzureCredential();

        // Endpoint 例: https://{host}/api/projects/{proj}/agents/{name}/endpoint/protocols/openai
        // Responses API URL: {Endpoint}/responses?api-version=v1
        // クエリ文字列が付いている場合は除去してから組み立てる
        var baseEndpoint = _settings.Endpoint.TrimEnd('/');
        var queryStart = baseEndpoint.IndexOf('?');
        if (queryStart >= 0) baseEndpoint = baseEndpoint[..queryStart];
        _responsesEndpoint = new Uri(baseEndpoint + "/responses?api-version=v1");
    }

    public async Task<string> SearchScenesAsync(
        string query,
        Dictionary<string, string> availableVideos,
        CancellationToken cancellationToken = default)
    {
        // Step 1: Web アプリが AI Search を事前検索して正確な ID を取得
        var searchResult = await _searchService.SearchAsync(query, cancellationToken: cancellationToken);
        if (searchResult.Documents.Count == 0)
            return JsonSerializer.Serialize(new SceneSearchResponse());

        _logger.LogInformation("Foundry Hosted Agent 呼び出し: {Endpoint}", _responsesEndpoint);

        // Step 2: Entra ID ベアラートークン取得
        var tokenCtx = new TokenRequestContext(["https://ai.azure.com/.default"]);
        var token = await _credential.GetTokenAsync(tokenCtx, cancellationToken);

        // Step 3: AI Search 結果をコンテキストとして含むユーザーメッセージを構築
        string userMessage = BuildUserMessage(query, availableVideos, searchResult.ContextText);

        // OpenAI Responses API リクエストボディ
        var requestBody = JsonSerializer.Serialize(new
        {
            model = _settings.ModelDeploymentName,
            input = new[] { new { role = "user", content = userMessage } }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, _responsesEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        var httpResponse = await _http.SendAsync(request, cancellationToken);

        if ((int)httpResponse.StatusCode == 429)
            throw new HttpRequestException("Rate limit exceeded", null, System.Net.HttpStatusCode.TooManyRequests);

        httpResponse.EnsureSuccessStatusCode();

        var responseJson = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation("Agent 応答受信 ({Length} chars)", responseJson.Length);

        // レスポンスからテキストを抽出
        var responseText = ExtractOutputText(responseJson);
        _logger.LogInformation("Agent テキスト ({Length} chars): {Text}",
            responseText.Length, responseText.Length > 300 ? responseText[..300] + "..." : responseText);

        // markdown コードフェンスがあれば除去
        var cleanJson = responseText.Trim();
        if (cleanJson.StartsWith("```"))
            cleanJson = System.Text.RegularExpressions.Regex.Replace(
                cleanJson, @"```(?:json)?\s*|\s*```", "").Trim();

        // JSON パース
        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        AgentPickResponse? picks = null;
        try { picks = JsonSerializer.Deserialize<AgentPickResponse>(cleanJson, jsonOpts); }
        catch (Exception ex) { _logger.LogWarning(ex, "Agent JSON パース失敗: {Text}", cleanJson); }

        if (picks?.Scenes == null || picks.Scenes.Count == 0)
            return JsonSerializer.Serialize(new SceneSearchResponse());

        // Step 4: AI Search キャッシュを優先してメタデータ補完、なければ直接取得
        var scenes = new List<SceneResult>();
        for (int i = 0; i < picks.Scenes.Count; i++)
        {
            var pick = picks.Scenes[i];
            if (string.IsNullOrEmpty(pick.ResultId)) continue;

            // まず事前検索結果キャッシュを参照（ID が正確なため推奨）
            RetrievedDocument? doc = null;
            if (!searchResult.Documents.TryGetValue(pick.ResultId, out doc))
            {
                // キャッシュにない場合は AI Search へ直接取得
                doc = await _searchService.GetDocumentByIdAsync(pick.ResultId, cancellationToken);
            }
            if (doc == null)
            {
                _logger.LogWarning("AI Search でドキュメントが見つかりません: {ResultId}", pick.ResultId);
                continue;
            }

            var title = availableVideos.TryGetValue(doc.VideoId, out var t) ? t : doc.VideoId;
            scenes.Add(new SceneResult
            {
                VideoId     = doc.VideoId,
                Title       = title,
                Start       = MsToTimeString(doc.BeginMs),
                End         = MsToTimeString(doc.EndMs),
                Confidence  = Math.Max(0.1, 1.0 - (i * 0.1)),
                Evidence    = pick.Evidence,
                Description = doc.SceneSummary,
                Mode        = doc.DocumentType,
                SceneId     = doc.SceneId ?? doc.VideoId,
                DocumentId  = pick.ResultId,
            });
        }

        return JsonSerializer.Serialize(new SceneSearchResponse { Scenes = scenes }, jsonOpts);
    }

    /// <summary>
    /// AI Search 検索結果を含むユーザーメッセージを構築する。
    /// エージェントは提供されたコンテキストから選択するだけなので ID の捕洩を防止できる。
    /// </summary>
    private static string BuildUserMessage(string query, Dictionary<string, string> availableVideos, string contextText)
    {
        var sb = new System.Text.StringBuilder();
        if (availableVideos.Count > 0)
        {
            sb.AppendLine("Available videos:");
            foreach (var v in availableVideos) sb.AppendLine($"- {v.Key}: {v.Value}");
            sb.AppendLine();
        }
        sb.AppendLine("[Azure AI Search 取得済みコンテキスト]");
        sb.AppendLine(contextText);
        sb.AppendLine("[/Azure AI Search 取得済みコンテキスト]");
        sb.AppendLine();
        sb.Append($"User query: {query}");
        return sb.ToString();
    }

    /// <summary>
    /// OpenAI Responses API のレスポンス JSON からアシスタントのテキストを抽出します。
    /// </summary>
    private string ExtractOutputText(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            // output[].content[].text (MessageResponseItem 形式)
            if (root.TryGetProperty("output", out var output))
            {
                foreach (var item in output.EnumerateArray())
                {
                    if (item.TryGetProperty("content", out var content))
                    {
                        foreach (var part in content.EnumerateArray())
                        {
                            if (part.TryGetProperty("text", out var text))
                                return text.GetString() ?? "";
                        }
                    }
                    // output[].text (直接テキスト形式)
                    if (item.TryGetProperty("text", out var directText))
                        return directText.GetString() ?? "";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "応答テキスト抽出失敗: {Json}", responseJson[..Math.Min(200, responseJson.Length)]);
        }
        return "";
    }

    private static string MsToTimeString(int ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
