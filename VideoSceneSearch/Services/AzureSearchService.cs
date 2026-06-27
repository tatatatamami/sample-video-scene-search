using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Options;
using OpenAI.Embeddings;
using System.Text;
using VideoSceneSearch.Models;

namespace VideoSceneSearch.Services;

public interface IAzureSearchService
{
    /// <summary>
    /// Azure AI Search でハイブリッド検索（テキスト＋ベクトル）を実行し、
    /// エージェントに渡す取得済みコンテキスト文字列を返す。
    /// </summary>
    Task<string> SearchAsync(
        string query,
        string? videoIdFilter = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Azure AI Search を用いてドキュメントを検索するサービス。
/// クエリのベクトル化に Azure OpenAI Embedding モデルを使用します。
/// </summary>
public class AzureSearchService : IAzureSearchService
{
    private readonly SearchClient _searchClient;
    private readonly EmbeddingClient _embeddingClient;
    private readonly AzureAISearchSettings _settings;
    private readonly ILogger<AzureSearchService> _logger;

    public AzureSearchService(
        IOptions<AzureAISearchSettings> settings,
        ILogger<AzureSearchService> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        var credential = new DefaultAzureCredential();

        _searchClient = new SearchClient(
            new Uri(_settings.Endpoint),
            _settings.IndexName,
            credential);

        var openAiClient = new AzureOpenAIClient(
            new Uri(_settings.EmbeddingEndpoint),
            credential);
        _embeddingClient = openAiClient.GetEmbeddingClient(_settings.EmbeddingDeployment);
    }

    public async Task<string> SearchAsync(
        string query,
        string? videoIdFilter = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Azure AI Search: query={Query}, filter={Filter}", query, videoIdFilter);

        // クエリをベクトル化
        var embeddingResult = await _embeddingClient.GenerateEmbeddingAsync(
            query, cancellationToken: cancellationToken);
        ReadOnlyMemory<float> queryVector = embeddingResult.Value.ToFloats();

        // ハイブリッド検索オプション（テキスト BM25 ＋ ベクトル HNSW）
        var options = new SearchOptions
        {
            Size = _settings.TopK,
        };
        options.Select.Add("id");
        options.Select.Add("documentType");
        options.Select.Add("videoId");
        options.Select.Add("sceneId");
        options.Select.Add("beginMs");
        options.Select.Add("endMs");
        options.Select.Add("timeMs");
        options.Select.Add("scenePeople");
        options.Select.Add("visiblePeople");
        options.Select.Add("scene_summary");
        options.Select.Add("search_text");

        options.VectorSearch = new VectorSearchOptions();
        options.VectorSearch.Queries.Add(new VectorizedQuery(queryVector)
        {
            Fields = { "content_vector" },
            KNearestNeighborsCount = _settings.TopK,
        });

        if (!string.IsNullOrEmpty(videoIdFilter))
        {
            options.Filter = $"videoId eq '{EscapeODataString(videoIdFilter)}'";
        }

        var searchResponse = await _searchClient.SearchAsync<SearchDocument>(
            query, options, cancellationToken);

        var sb = new StringBuilder();
        int count = 0;
        await foreach (var result in searchResponse.Value.GetResultsAsync())
        {
            var doc = result.Document;
            var docId      = GetString(doc, "id");
            var docType    = GetString(doc, "documentType");
            var videoId    = GetString(doc, "videoId");
            var beginMs    = GetInt(doc, "beginMs");
            var endMs      = GetInt(doc, "endMs");
            var text       = GetString(doc, "search_text");
            var sceneSummary = GetString(doc, "scene_summary");
            var scenePeople  = GetStringList(doc, "scenePeople");
            var visiblePeople = GetStringList(doc, "visiblePeople");
            var score      = result.Score ?? 0.0;

            sb.AppendLine($"--- 検索結果 {++count} (score: {score:F3}) ---");
            // 人物・ ID ・時刻は truncation の外側に必ず含める
            sb.AppendLine($"id: {docId}  type: {docType}  videoId: {videoId}  beginMs: {beginMs}  endMs: {endMs}");
            if (scenePeople.Count > 0)
                sb.AppendLine($"シーン登場人物: {string.Join(", ", scenePeople)}");
            if (visiblePeople.Count > 0 && docType == "keyframe")
                sb.AppendLine($"フレーム内人物: {string.Join(", ", visiblePeople)}");
            if (!string.IsNullOrEmpty(sceneSummary))
                sb.AppendLine($"シーン要約: {sceneSummary}");
            // 内容テキスト（長い場合は先頭 1200 文字のみ）
            sb.AppendLine(text.Length > 1200 ? text[..1200] + "…" : text);
            sb.AppendLine();
        }

        var context = sb.ToString();
        _logger.LogInformation("Azure AI Search: {Count} 件の結果を取得しました", count);

        if (count == 0)
        {
            return "(関連するシーン情報が見つかりませんでした)";
        }

        return context;
    }

    // ---- ヘルパー ----

    private static string GetString(SearchDocument doc, string key)
        => doc.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static int GetInt(SearchDocument doc, string key)
    {
        if (doc.TryGetValue(key, out var v) && v is not null)
            return Convert.ToInt32(v);
        return 0;
    }

    private static List<string> GetStringList(SearchDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var v) || v is null) return [];
        if (v is IEnumerable<object> items)
            return items.Select(i => i?.ToString() ?? "").Where(s => s != "").ToList();
        return [];
    }

    /// <summary>OData フィルタ文字列内のシングルクォートをエスケープする。</summary>
    private static string EscapeODataString(string value)
        => value.Replace("'", "''");
}
