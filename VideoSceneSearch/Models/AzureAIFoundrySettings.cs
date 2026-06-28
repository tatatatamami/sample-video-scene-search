namespace VideoSceneSearch.Models;

public class AzureAIFoundrySettings
{
    /// <summary>
    /// Azure AI Foundry のエンドポイント URL。
    /// 例: https://{resource}.services.ai.azure.com/api/projects/{project}/agents/{agent-name}/endpoint/protocols/openai
    /// AgentsClient のベース URL (host のみ) を導出するために使用します。
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// エージェントに設定されているモデルのデプロイメント名 (例: gpt-4.1)。
    /// </summary>
    public string ModelDeploymentName { get; set; } = string.Empty;

    /// <summary>
    /// Foundry ポータルで作成した標準エージェントの ID (GUID)。
    /// </summary>
    public string? AgentId { get; set; }
}
