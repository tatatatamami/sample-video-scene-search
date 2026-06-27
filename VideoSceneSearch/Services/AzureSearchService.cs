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
            var docId    = GetString(doc, "id");
            var videoId  = GetString(doc, "videoId");
            var beginMs  = GetInt(doc, "beginMs");
            var endMs    = GetInt(doc, "endMs");
            var text     = GetString(doc, "search_text");
            var score    = result.Score ?? 0.0;

            sb.AppendLine($"--- 検索結果 {++count} (スコア: {score:F3}) ---");
            sb.AppendLine($"ID: {docId}  videoId: {videoId}  開始: {beginMs}ms  終了: {endMs}ms");
            // テキストが長い場合は先頭部分のみ渡す
            sb.AppendLine(text.Length > 1000 ? text[..1000] + "…" : text);
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

    /// <summary>OData フィルタ文字列内のシングルクォートをエスケープする。</summary>
    private static string EscapeODataString(string value)
        => value.Replace("'", "''");
}
