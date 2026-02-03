using System.Text.Json;
using System.Text.RegularExpressions;
using VideoSceneSearch.Models;

namespace VideoSceneSearch.Services;

public interface IAgentResponseParser
{
    ParsedSceneResponse ParseResponse(string responseJson);
}

public class AgentResponseParser : IAgentResponseParser
{
    private readonly ILogger<AgentResponseParser> _logger;

    public AgentResponseParser(ILogger<AgentResponseParser> logger)
    {
        _logger = logger;
    }

    public ParsedSceneResponse ParseResponse(string responseJson)
    {
        var result = new ParsedSceneResponse
        {
            RawResponse = responseJson
        };

        try
        {
            var agentResponse = JsonSerializer.Deserialize<AgentResponse>(responseJson);
            if (agentResponse?.Choices == null || agentResponse.Choices.Count == 0)
            {
                _logger.LogWarning("No choices found in agent response");
                return result;
            }

            var content = agentResponse.Choices[0].Message?.Content ?? string.Empty;
            _logger.LogInformation("Agent content length: {Length}", content.Length);
            
            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning("Content is empty");
                return result;
            }

            ParseSceneCandidates(content, result);
            
            _logger.LogInformation("Parsed {Count} candidates with summary: Total={Total}, High={High}, Medium={Medium}, Low={Low}", 
                result.Candidates.Count, 
                result.Summary.TotalCount, 
                result.Summary.HighCount, 
                result.Summary.MediumCount, 
                result.Summary.LowCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing agent response");
        }

        return result;
    }

    private void ParseSceneCandidates(string content, ParsedSceneResponse result)
    {
        // Split by lines, keeping empty lines to track sections
        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
        
        // Parse summary
        ParseSummary(lines, result);

        // Parse candidates
        SceneCandidate? currentCandidate = null;
        string? lastField = null;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            
            if (string.IsNullOrWhiteSpace(trimmedLine))
            {
                continue;
            }

            // Start of new candidate - look for "Video ID:"
            if (trimmedLine.StartsWith("Video ID:", StringComparison.OrdinalIgnoreCase) ||
                trimmedLine.Contains("Video ID:"))
            {
                if (currentCandidate != null)
                {
                    result.Candidates.Add(currentCandidate);
                    _logger.LogDebug("Added candidate: {VideoId} - {Title}", currentCandidate.VideoId, currentCandidate.Title);
                }
                currentCandidate = new SceneCandidate();
                currentCandidate.VideoId = ExtractValue(trimmedLine, "Video ID:");
                lastField = "VideoId";
            }
            else if (currentCandidate != null)
            {
                if (trimmedLine.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
                {
                    currentCandidate.Title = ExtractValue(trimmedLine, "Title:");
                    lastField = "Title";
                }
                else if (trimmedLine.StartsWith("Timestamp:", StringComparison.OrdinalIgnoreCase))
                {
                    var timestampPart = ExtractValue(trimmedLine, "Timestamp:");
                    ParseTimestamp(timestampPart, currentCandidate);
                    lastField = "Timestamp";
                }
                else if (trimmedLine.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
                {
                    currentCandidate.Description = ExtractValue(trimmedLine, "Description:");
                    lastField = "Description";
                }
                else if (trimmedLine.StartsWith("根拠:") || trimmedLine.StartsWith("根拠："))
                {
                    currentCandidate.Evidence = ExtractValueJapanese(trimmedLine, new[] { "根拠:", "根拠：" });
                    lastField = "Evidence";
                }
                else if (trimmedLine.StartsWith("理由:") || trimmedLine.StartsWith("理由："))
                {
                    currentCandidate.Reason = ExtractValueJapanese(trimmedLine, new[] { "理由:", "理由：" });
                    lastField = "Reason";
                }
                else if (trimmedLine.StartsWith("参照:") || trimmedLine.StartsWith("参照："))
                {
                    currentCandidate.Reference = ExtractValueJapanese(trimmedLine, new[] { "参照:", "参照：" });
                    lastField = "Reference";
                }
                else if (lastField != null && !trimmedLine.Contains(':'))
                {
                    // Continuation of previous field
                    AppendToField(currentCandidate, lastField, trimmedLine);
                }
            }
        }

        if (currentCandidate != null)
        {
            result.Candidates.Add(currentCandidate);
            _logger.LogDebug("Added final candidate: {VideoId} - {Title}", currentCandidate.VideoId, currentCandidate.Title);
        }
    }

    private void AppendToField(SceneCandidate candidate, string fieldName, string value)
    {
        switch (fieldName)
        {
            case "Description":
                candidate.Description += " " + value;
                break;
            case "Evidence":
                candidate.Evidence += " " + value;
                break;
            case "Reason":
                candidate.Reason += " " + value;
                break;
            case "Reference":
                candidate.Reference += " " + value;
                break;
        }
    }

    private void ParseSummary(string[] lines, ParsedSceneResponse result)
    {
        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (trimmedLine.Contains("該当シーン候補") || trimmedLine.Contains("シーン候補"))
            {
                // Try to match: "該当シーン候補: 3件 (High: 2 / Medium: 1 / Low: 0)"
                var match = Regex.Match(trimmedLine, @"(\d+)\s*件.*?High[：:]\s*(\d+).*?Medium[：:]\s*(\d+).*?Low[：:]\s*(\d+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    result.Summary.TotalCount = int.Parse(match.Groups[1].Value);
                    result.Summary.HighCount = int.Parse(match.Groups[2].Value);
                    result.Summary.MediumCount = int.Parse(match.Groups[3].Value);
                    result.Summary.LowCount = int.Parse(match.Groups[4].Value);
                    _logger.LogDebug("Parsed summary from line: {Line}", trimmedLine);
                }
                break;
            }
        }
    }

    private void ParseTimestamp(string timestampPart, SceneCandidate candidate)
    {
        // Extract timestamp and confidence
        var parts = timestampPart.Split('/');
        if (parts.Length >= 1)
        {
            candidate.Timestamp = parts[0].Trim();
            
            // Parse start and end times
            var timeMatch = Regex.Match(candidate.Timestamp, @"(\d{2}:\d{2}:\d{2}\.\d{2})\s*-\s*(\d{2}:\d{2}:\d{2}\.\d{2})");
            if (timeMatch.Success)
            {
                candidate.StartTime = ParseTimeToSeconds(timeMatch.Groups[1].Value);
                candidate.EndTime = ParseTimeToSeconds(timeMatch.Groups[2].Value);
            }
        }
        
        if (parts.Length >= 2)
        {
            var confidencePart = parts[1].Trim();
            var confMatch = Regex.Match(confidencePart, @"信頼度[：:]?\s*(\w+)");
            if (confMatch.Success)
            {
                candidate.Confidence = confMatch.Groups[1].Value;
            }
        }
    }

    private double ParseTimeToSeconds(string timeString)
    {
        var parts = timeString.Split(':');
        if (parts.Length == 3)
        {
            if (int.TryParse(parts[0], out int hours) &&
                int.TryParse(parts[1], out int minutes) &&
                double.TryParse(parts[2], out double seconds))
            {
                return hours * 3600 + minutes * 60 + seconds;
            }
        }
        return 0;
    }

    private string ExtractValue(string line, string prefix)
    {
        var colonIndex = line.IndexOf(':');
        if (colonIndex > 0 && colonIndex < line.Length - 1)
        {
            return line.Substring(colonIndex + 1).Trim();
        }
        return string.Empty;
    }

    private string ExtractValueJapanese(string line, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            var index = line.IndexOf(prefix);
            if (index >= 0)
            {
                var startIndex = index + prefix.Length;
                if (startIndex < line.Length)
                {
                    return line.Substring(startIndex).Trim();
                }
            }
        }
        return string.Empty;
    }
}
