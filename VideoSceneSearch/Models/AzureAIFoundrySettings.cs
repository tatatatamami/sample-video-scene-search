namespace VideoSceneSearch.Models;

public class AzureAIFoundrySettings
{
    public string Endpoint { get; set; } = string.Empty;
    public string Scope { get; set; } = "https://cognitiveservices.azure.com/.default";
}
