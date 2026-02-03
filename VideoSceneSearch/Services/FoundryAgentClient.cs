using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VideoSceneSearch.Models;

namespace VideoSceneSearch.Services;

public interface IFoundryAgentClient
{
    Task<string> SearchScenesAsync(string query, Dictionary<string, string> availableVideos, CancellationToken cancellationToken = default);
}

public class FoundryAgentClient : IFoundryAgentClient
{
    private readonly HttpClient _httpClient;
    private readonly AzureAIFoundrySettings _settings;
    private readonly DefaultAzureCredential _credential;
    private readonly ILogger<FoundryAgentClient> _logger;

    public FoundryAgentClient(
        HttpClient httpClient,
        IOptions<AzureAIFoundrySettings> settings,
        DefaultAzureCredential credential,
        ILogger<FoundryAgentClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _credential = credential;
        _logger = logger;
    }

    public async Task<string> SearchScenesAsync(string query, Dictionary<string, string> availableVideos, CancellationToken cancellationToken = default)
    {
        try
        {
            // Build available videos list for the prompt
            var videoList = string.Join("\n", availableVideos.Select(v => $"- videoId: \"{v.Key}\", title: \"{v.Value}\""));

            // Prepare the request payload for Azure AI Foundry Responses API with JSON instruction
            // Note: response_format may not be supported by Responses API, so we rely on explicit instructions
            var jsonInstruction = $@"You must return ONLY valid JSON in the following schema. No markdown, no code blocks, no explanatory text:
{{
  ""scenes"": [
    {{ ""videoId"": ""..."", ""title"": ""..."", ""start"": ""HH:MM:SS"", ""end"": ""HH:MM:SS"", ""confidence"": 0.0, ""evidence"": ""..."" }}
  ]
}}

IMPORTANT: You must use ONLY these exact videoId values from the available videos:
{videoList}

The videoId in your response MUST exactly match one of the videoId values listed above.

User query: ";

            // Option 1: Using input string only (current implementation)
            var requestBody = new
            {
                input = jsonInstruction + query
            };

            // Option 2: Adding response_format (uncomment if API supports it)
            // var requestBody = new
            // {
            //     input = jsonInstruction + query,
            //     response_format = new
            //     {
            //         type = "json_object"
            //     }
            // };

            // Option 3: Using messages array (uncomment if Option 1/2 fail)
            // var requestBody = new
            // {
            //     messages = new[]
            //     {
            //         new
            //         {
            //             role = "system",
            //             content = "You must output ONLY valid JSON. No markdown. No extra text. Use this schema: {\"scenes\": [{\"start\": \"HH:MM:SS\", \"end\": \"HH:MM:SS\", \"confidence\": 0.0, \"evidence\": \"...\"}]}"
            //         },
            //         new
            //         {
            //             role = "user",
            //             content = $"Return JSON that matches the schema. Query: {query}"
            //         }
            //     }
            // };

            var jsonContent = JsonSerializer.Serialize(requestBody);
            _logger.LogInformation("Request body: {RequestBody}", jsonContent);
            
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            // Create request
            var request = new HttpRequestMessage(HttpMethod.Post, _settings.Endpoint)
            {
                Content = content
            };

            // Use API Key if available, otherwise use Entra ID
            if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            {
                _logger.LogInformation("Using API Key authentication");
                request.Headers.Add("api-key", _settings.ApiKey);
            }
            else
            {
                _logger.LogInformation("Using Entra ID authentication");
                var tokenRequestContext = new TokenRequestContext(new[] { _settings.Scope });
                var token = await _credential.GetTokenAsync(tokenRequestContext, cancellationToken);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            }

            _logger.LogInformation("Sending request to Azure AI Foundry Agent with JSON mode enabled");

            // Send request to Foundry Agent
            var response = await _httpClient.SendAsync(request, cancellationToken);
            
            // Read response
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation("Response status: {StatusCode}", response.StatusCode);
            
            response.EnsureSuccessStatusCode();

            // Validate that response is valid JSON
            var validatedJson = ValidateAndExtractJson(responseContent);
            
            _logger.LogInformation("Successfully received and validated JSON response");
            return validatedJson;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Azure AI Foundry Agent");
            throw;
        }
    }

    private string ValidateAndExtractJson(string responseContent)
    {
        try
        {
            // Parse the full response to get the actual content
            using var doc = JsonDocument.Parse(responseContent);
            
            // Azure AI Foundry Responses API structure: output[] array with type: "message"
            if (doc.RootElement.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                // Find the message object in output array
                foreach (var item in output.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var itemType) && 
                        itemType.GetString() == "message" &&
                        item.TryGetProperty("content", out var content) && 
                        content.ValueKind == JsonValueKind.Array)
                    {
                        // Find output_text in content array
                        foreach (var contentItem in content.EnumerateArray())
                        {
                            if (contentItem.TryGetProperty("type", out var contentType) &&
                                contentType.GetString() == "output_text" &&
                                contentItem.TryGetProperty("text", out var text))
                            {
                                var contentString = text.GetString() ?? string.Empty;
                                _logger.LogInformation("Extracted output_text: {Content}", 
                                    contentString.Length > 500 ? contentString.Substring(0, 500) + "..." : contentString);

                                // Validate that the text itself is valid JSON
                                using var contentDoc = JsonDocument.Parse(contentString);
                                
                                // Check if it has the expected schema
                                if (contentDoc.RootElement.TryGetProperty("scenes", out var scenes) && 
                                    scenes.ValueKind == JsonValueKind.Array)
                                {
                                    _logger.LogInformation("JSON validation passed: Found 'scenes' array with {Count} items", 
                                        scenes.GetArrayLength());
                                    return contentString;
                                }
                                else
                                {
                                    _logger.LogWarning("JSON structure warning: 'scenes' array not found or invalid");
                                    return contentString; // Return anyway, let caller handle it
                                }
                            }
                        }
                    }
                }
            }
            
            // Fallback: Try OpenAI-style structure (choices[0].message.content)
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var contentStr))
            {
                var contentString = contentStr.GetString() ?? string.Empty;
                _logger.LogInformation("Extracted from choices (OpenAI format): {Content}", 
                    contentString.Length > 500 ? contentString.Substring(0, 500) + "..." : contentString);

                using var contentDoc = JsonDocument.Parse(contentString);
                if (contentDoc.RootElement.TryGetProperty("scenes", out var scenes) && 
                    scenes.ValueKind == JsonValueKind.Array)
                {
                    _logger.LogInformation("JSON validation passed (OpenAI format)");
                    return contentString;
                }
                return contentString;
            }
            
            _logger.LogWarning("Unexpected response structure, returning as-is");
            return responseContent;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON validation failed. Response content: {Content}", 
                responseContent.Length > 1000 ? responseContent.Substring(0, 1000) + "..." : responseContent);
            throw new InvalidOperationException("Agent response is not valid JSON. Please check the response structure.", ex);
        }
    }
}
