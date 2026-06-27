namespace VideoSceneSearch.Models;

public class AzureAISearchSettings
{
    /// <summary>
    /// Azure AI Search サービスエンドポイント。
    /// 例: https://&lt;name&gt;.search.windows.net
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// 検索対象のインデックス名。
    /// 例: video-scenes-keyframe
    /// </summary>
    public string IndexName { get; set; } = string.Empty;

    /// <summary>
    /// クエリベクトル計算に使用する Azure OpenAI エンドポイント。
    /// 例: https://&lt;resource&gt;.services.ai.azure.com
    /// </summary>
    public string EmbeddingEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Embedding モデルのデプロイメント名。
    /// 例: text-embedding-3-small
    /// </summary>
    public string EmbeddingDeployment { get; set; } = string.Empty;

    /// <summary>
    /// 検索結果の最大取得件数。デフォルト: 10
    /// </summary>
    public int TopK { get; set; } = 10;
}
