using System.Text.Json.Serialization;

namespace VideoSceneSearch.Models;

public class VideoMappingSettings
{
    [JsonPropertyName("VideoMapping")]
    public Dictionary<string, VideoInfo> VideoMapping { get; set; } = new();
}

public class VideoInfo
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("thumbnail")]
    public string? Thumbnail { get; set; }
}
