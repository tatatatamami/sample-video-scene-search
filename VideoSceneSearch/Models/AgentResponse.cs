using System.Text.Json.Serialization;

namespace VideoSceneSearch.Models;

public class AgentResponse
{
    [JsonPropertyName("choices")]
    public List<Choice>? Choices { get; set; }
}

public class Choice
{
    [JsonPropertyName("message")]
    public Message? Message { get; set; }
    
    [JsonPropertyName("index")]
    public int Index { get; set; }
}

public class Message
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;
    
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

public class SceneCandidateSummary
{
    public int TotalCount { get; set; }
    public int HighCount { get; set; }
    public int MediumCount { get; set; }
    public int LowCount { get; set; }
}

public class SceneCandidate
{
    public string VideoId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public string Confidence { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public double StartTime { get; set; }
    public double EndTime { get; set; }
}

public class ParsedSceneResponse
{
    public SceneCandidateSummary Summary { get; set; } = new();
    public List<SceneCandidate> Candidates { get; set; } = new();
    public string RawResponse { get; set; } = string.Empty;
}
