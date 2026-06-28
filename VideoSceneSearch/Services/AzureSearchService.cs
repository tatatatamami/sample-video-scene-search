using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Options;
using OpenAI.Embeddings;
using System.Text;
using VideoSceneSearch.Models;

namespace VideoSceneSearch.Services;

/// <summary>AI Search から取得した単一ドキュメントの必要最小情報。</summary>
public sealed record RetrievedDocument(
    string VideoId,
    string DocumentType,
    string? SceneId,
    string? KeyFrameId,
    int BeginMs,
    int EndMs,
    int TimeMs,
    string? SceneSummary,
    double Score);

/// <summary>Azure AI Search 検索結果（コンテキスト文字列 ＋ ドキュメント辞書）。</summary>
public sealed record AzureSearchResult(
    string ContextText,
    IReadOnlyDictionary<string, RetrievedDocument> Documents);

public interface IAzureSearchService
{
    /// <summary>
    /// Azure AI Search でハイブリッド検索（テキスト＋ベクトル＋セマンティックランカー）を実行し、
    /// エージェントに渡す取得済みコンテキスト文字列とドキュメント辞書を返す。
    /// </summary>
    /// <param name="documentTypeFilter">
    /// OData フィルターに追加する documentType 値（"scene" / "keyframe" / null=両方）。
    /// 統合インデックスで scene と keyframe を使い分ける場合に指定します。
    /// </param>
    /// <param name="scenePersonFilter">
    /// scenePeople コレクションの完全一致フィルター値。人物名を指定すると
    /// <c>scenePeople/any(p: p eq '...')</c> フィルターが追加されます。
    /// </param>
    Task<AzureSearchResult> SearchAsync(
        string query,
        string? videoIdFilter = null,
        string? documentTypeFilter = null,
        string? scenePersonFilter = null,
        CancellationToken cancellationToken = default);

    /// <summary>AI Search からドキュメントIDで直接取得する。タイムスタンプ補完用。</summary>
    Task<RetrievedDocument?> GetDocumentByIdAsync(string documentId, CancellationToken cancellationToken = default);
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

        if (!string.IsNullOrEmpty(_settings.ApiKey))
        {
            _searchClient = new SearchClient(
                new Uri(_settings.Endpoint),
                _settings.IndexName,
                new Azure.AzureKeyCredential(_settings.ApiKey));
        }
        else
        {
            var credential = new DefaultAzureCredential();
            _searchClient = new SearchClient(
                new Uri(_settings.Endpoint),
                _settings.IndexName,
                credential);
        }

        var openAiClient = new AzureOpenAIClient(
            new Uri(_settings.EmbeddingEndpoint),
            new DefaultAzureCredential());
        _embeddingClient = openAiClient.GetEmbeddingClient(_settings.EmbeddingDeployment);
    }

    public async Task<AzureSearchResult> SearchAsync(
        string query,
        string? videoIdFilter = null,
        string? documentTypeFilter = null,
        string? scenePersonFilter = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Azure AI Search: query={Query}, videoFilter={VideoFilter}, typeFilter={TypeFilter}, personFilter={PersonFilter}",
            query, videoIdFilter, documentTypeFilter, scenePersonFilter);

        // クエリをベクトル化
        var embeddingResult = await _embeddingClient.GenerateEmbeddingAsync(
            query, cancellationToken: cancellationToken);
        ReadOnlyMemory<float> queryVector = embeddingResult.Value.ToFloats();

        // ハイブリッド検索オプション（テキスト BM25 ＋ ベクトル HNSW ＋ セマンティックランカー）
        var options = new SearchOptions
        {
            Size = _settings.TopK,
            QueryType = SearchQueryType.Semantic,
            SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = "semantic-config",
            },
        };
        options.Select.Add("id");
        options.Select.Add("documentType");
        options.Select.Add("videoId");
        options.Select.Add("sceneId");
        options.Select.Add("keyFrameId");
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
            KNearestNeighborsCount = 50,  // セマンティックランカー使用時の推奨値
        });

        // フィルター構築
        var filterParts = new List<string>();
        if (!string.IsNullOrEmpty(videoIdFilter))
            filterParts.Add($"videoId eq '{EscapeODataString(videoIdFilter)}'");
        if (!string.IsNullOrEmpty(documentTypeFilter))
            filterParts.Add($"documentType eq '{EscapeODataString(documentTypeFilter)}'");
        if (!string.IsNullOrEmpty(scenePersonFilter))
            filterParts.Add($"scenePeople/any(p: p eq '{EscapeODataString(scenePersonFilter)}')");
        if (filterParts.Count > 0)
            options.Filter = string.Join(" and ", filterParts);

        var searchResponse = await _searchClient.SearchAsync<SearchDocument>(
            query, options, cancellationToken);

        var sb = new StringBuilder();
        var documents = new Dictionary<string, RetrievedDocument>();
        int count = 0;
        await foreach (var result in searchResponse.Value.GetResultsAsync())
        {
            var doc = result.Document;
            var docId        = GetString(doc, "id");
            var docType      = GetString(doc, "documentType");
            var videoId      = GetString(doc, "videoId");
            var sceneId      = GetString(doc, "sceneId");
            var keyFrameId   = GetString(doc, "keyFrameId");
            var beginMs      = GetInt(doc, "beginMs");
            var endMs        = GetInt(doc, "endMs");
            var timeMs       = GetInt(doc, "timeMs");
            var text         = GetString(doc, "search_text");
            var sceneSummary = GetString(doc, "scene_summary");
            var scenePeople  = GetStringList(doc, "scenePeople");
            var visiblePeople = GetStringList(doc, "visiblePeople");
            var score        = result.Score ?? 0.0;

            // ドキュメント辞書に追加（resultId → メタデータ）
            if (!string.IsNullOrEmpty(docId))
            {
                documents[docId] = new RetrievedDocument(
                    VideoId: videoId,
                    DocumentType: docType,
                    SceneId: string.IsNullOrEmpty(sceneId) ? null : sceneId,
                    KeyFrameId: string.IsNullOrEmpty(keyFrameId) ? null : keyFrameId,
                    BeginMs: beginMs,
                    EndMs: endMs,
                    TimeMs: timeMs,
                    SceneSummary: string.IsNullOrEmpty(sceneSummary) ? null : sceneSummary,
                    Score: score);
            }

            sb.AppendLine($"--- 検索結果 {++count} (score: {score:F3}) ---");
            // 人物・ID・時刻は truncation の外側に必ず含める
            sb.AppendLine($"id: {docId}  type: {docType}  videoId: {videoId}  sceneId: {sceneId}  beginMs: {beginMs}  endMs: {endMs}  timeMs: {timeMs}");
            if (scenePeople.Count > 0)
                sb.AppendLine($"シーン登場人物: {string.Join(", ", scenePeople)}");
            if (visiblePeople.Count > 0 && docType == "keyframe")
                sb.AppendLine($"フレーム内人物: {string.Join(", ", visiblePeople)}");
            if (!string.IsNullOrEmpty(sceneSummary))
                sb.AppendLine($"シーン要約: {sceneSummary}");
            // 内容テキスト（長い場合は先頭 600 文字のみ — トークン節約）
            sb.AppendLine(text.Length > 600 ? text[..600] + "…" : text);
            sb.AppendLine();
        }

        _logger.LogInformation("Azure AI Search: {Count} 件の結果を取得しました", count);

        string contextText = count == 0
            ? "(関連するシーン情報が見つかりませんでした)"
            : sb.ToString();

        return new AzureSearchResult(contextText, documents);
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

    /// <inheritdoc />
    public async Task<RetrievedDocument?> GetDocumentByIdAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _searchClient.GetDocumentAsync<SearchDocument>(
                documentId, cancellationToken: cancellationToken);
            var doc = response.Value;

            var docType     = GetString(doc, "documentType");
            var videoId     = GetString(doc, "videoId");
            var sceneId     = GetString(doc, "sceneId");
            var beginMs     = GetInt(doc, "beginMs");
            var endMs       = GetInt(doc, "endMs");
            var timeMs      = GetInt(doc, "timeMs");
            var sceneSummary = GetString(doc, "scene_summary");

            return new RetrievedDocument(
                VideoId: videoId,
                DocumentType: docType,
                SceneId: string.IsNullOrEmpty(sceneId) ? null : sceneId,
                KeyFrameId: null,
                BeginMs: beginMs,
                EndMs: endMs,
                TimeMs: timeMs,
                SceneSummary: string.IsNullOrEmpty(sceneSummary) ? null : sceneSummary,
                Score: 1.0);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogWarning("Document not found in AI Search: {DocumentId}", documentId);
            return null;
        }
    }
}
