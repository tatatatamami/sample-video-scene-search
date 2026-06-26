namespace VideoSceneSearch.Models;

public class AzureAIFoundrySettings
{
    /// <summary>
    /// Azure AI Foundry のプロジェクトエンドポイント。
    /// 形式: https://{resource}.services.ai.azure.com/api/projects/{project}
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// 呼び出すエージェントの名前 (例: video-scene-search)。
    /// Foundry ポータルまたは agent.yaml の name フィールドに対応します。
    /// </summary>
    public string AgentName { get; set; } = string.Empty;
}
