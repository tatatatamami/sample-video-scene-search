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
    Task<string> SearchScenesAsync(string query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Azure AI Foundry Hosted Agent（Responses プロトコル）を HttpClient 経由で呼び出す。
/// AI Search は Hosted Agent が Foundry Toolbox (MCP) 経由で自律的に呼び出す。
/// Web アプリはこのクライアントだけを呼び出してシーン検索結果を取得する。
/// </summary>
public class FoundryAgentClient : IFoundryAgentClient
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly AzureAIFoundrySettings _settings;
    private readonly ILogger<FoundryAgentClient> _logger;
    private readonly TokenCredential _credential;
    private readonly Uri _responsesEndpoint;

    // Agent が Toolbox 経由で AI Search を呼び出した後に返すレスポンス形式
    private sealed record AgentSceneResponse(
        [property: JsonPropertyName("scenes")] List<AgentSceneItem> Scenes);

    private sealed record AgentSceneItem(
        [property: JsonPropertyName("documentId")] string DocumentId,
        [property: JsonPropertyName("videoId")] string VideoId,
        [property: JsonPropertyName("startMs")] int StartMs,
        [property: JsonPropertyName("endMs")] int EndMs,
        [property: JsonPropertyName("sceneSummary")] string SceneSummary,
        [property: JsonPropertyName("documentType")] string? DocumentType,
        [property: JsonPropertyName("evidence")] string Evidence);

    public FoundryAgentClient(
        IOptions<AzureAIFoundrySettings> settings,
        ILogger<FoundryAgentClient> logger)
    {
        _settings = settings.Value;
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
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Foundry Hosted Agent 呼び出し: {Endpoint}", _responsesEndpoint);

        // Entra ID ベアラートークン取得
        var tokenCtx = new TokenRequestContext(["https://ai.azure.com/.default"]);
        var token = await _credential.GetTokenAsync(tokenCtx, cancellationToken);

        // ユーザーメッセージ = クエリのみ。動画リスト等は送らない。
        // videoId/タイムスタンプは AI Search ドキュメントの [文書メタデータ] ブロックから抽出する。
        string userMessage = query;

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

        // Agent が返す構造化 JSON をパース
        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        AgentSceneResponse? parsed = null;
        try { parsed = JsonSerializer.Deserialize<AgentSceneResponse>(cleanJson, jsonOpts); }
        catch (Exception ex) { _logger.LogWarning(ex, "Agent JSON パース失敗: {Text}", cleanJson); }

        if (parsed?.Scenes == null || parsed.Scenes.Count == 0)
            return JsonSerializer.Serialize(new SceneSearchResponse());

        // Agent レスポンスからシーン結果を構築
        var scenes = new List<SceneResult>();
        for (int i = 0; i < parsed.Scenes.Count; i++)
        {
            var item = parsed.Scenes[i];
            if (string.IsNullOrEmpty(item.DocumentId)) continue;

            // videoId: Agent が [文書メタデータ] から抽出できなかった場合は documentId をパース
            var videoId = item.VideoId;
            if (string.IsNullOrEmpty(videoId))
            {
                var parts = item.DocumentId.Split("_scene_", 2);
                videoId = parts.Length > 1 ? parts[0] : item.DocumentId;
            }

            scenes.Add(new SceneResult
            {
                VideoId     = videoId,
                Title       = videoId, // Program.cs で officialTitle に上書きされる
                Start       = MsToTimeString(item.StartMs),
                End         = MsToTimeString(item.EndMs),
                // Confidence: 検索順位ベースの表示用仮値（AI Search のスコアではない）
                Confidence  = Math.Max(0.1, 1.0 - (i * 0.1)),
                Evidence    = item.Evidence ?? "",
                Description = item.SceneSummary ?? "",
                Mode        = item.DocumentType ?? "scene",  // デフォルトは scene（シーンドキュメントの documentType に導常）
                SceneId     = item.DocumentId,
                DocumentId  = item.DocumentId,
            });
        }

        return JsonSerializer.Serialize(new SceneSearchResponse { Scenes = scenes }, jsonOpts);
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
        // テキストが見つからない場合、デバッグのためにレスポンス全体をログ出力
        _logger.LogWarning("応答テキストなし。全レスポンス: {Json}", responseJson[..Math.Min(1000, responseJson.Length)]);
        return "";
    }

    private static string MsToTimeString(int ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}

