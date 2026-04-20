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

    // New fields from agent response
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("tags")]
    public string? Tags { get; set; }

    [JsonPropertyName("actions")]
    public string? Actions { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    // Additional properties for UI display (calculated from start/end)
    [JsonPropertyName("startSeconds")]
    public double StartSeconds { get; set; }

    [JsonPropertyName("endSeconds")]
    public double EndSeconds { get; set; }
}
