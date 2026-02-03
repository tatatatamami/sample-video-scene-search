using System.Text.Json.Serialization;

namespace VideoSceneSearch.Models;

public class SceneSearchResult
{
    [JsonPropertyName("videoId")]
    public string VideoId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("startSec")]
    public double StartSec { get; set; }

    [JsonPropertyName("endSec")]
    public double EndSec { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("score")]
    public double Score { get; set; }
}
