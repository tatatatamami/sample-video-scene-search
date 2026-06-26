namespace VideoSceneSearch.Models;

public class AzureAIFoundrySettings
{
    /// <summary>
    /// Azure AI Foundry のホステッドエージェントエンドポイント (ベース URL)。
    /// "/responses" の前までを指定します。
    /// 例: https://{resource}.services.ai.azure.com/api/projects/{project}/agents/{agent-name}/endpoint/protocols/openai
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// エージェントに設定されているモデルのデプロイメント名 (例: gpt-4.1)。
    /// </summary>
    public string ModelDeploymentName { get; set; } = string.Empty;
}
