using System.Text.Json.Serialization;

namespace VideoSceneSearch.Models;

public class SceneSearchResponse
{
    [JsonPropertyName("scenes")]
    public List<SceneResult> Scenes { get; set; } = new();
}

public class SceneResult
{
    [JsonPropertyName("videoId")]
    public string VideoId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("start")]
    public string Start { get; set; } = string.Empty;

    [JsonPropertyName("end")]
    public string End { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("evidence")]
    public string Evidence { get; set; } = string.Empty;

    // Additional properties for UI display
    public string Description { get; set; } = string.Empty;
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
}
