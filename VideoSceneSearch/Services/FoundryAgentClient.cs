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
    Task<string> SearchScenesAsync(string query, CancellationToken cancellationToken = default);
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

    public async Task<string> SearchScenesAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            // Prepare the request payload for Azure AI Foundry Responses API
            // Input should be a simple string based on the API error messages
            var requestBody = new
            {
                input = query
            };

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

            _logger.LogInformation("Sending request to Azure AI Foundry Agent: {Endpoint}", _settings.Endpoint);

            // Send request to Foundry Agent
            var response = await _httpClient.SendAsync(request, cancellationToken);
            
            // Log response details for debugging
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation("Response status: {StatusCode}", response.StatusCode);
            
            // Only log first 1000 chars to avoid excessive logging
            var logContent = responseContent.Length > 1000 
                ? responseContent.Substring(0, 1000) + "... (truncated)" 
                : responseContent;
            _logger.LogInformation("Response content preview: {ResponseContent}", logContent);
            
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Received successful response from Azure AI Foundry Agent");

            return responseContent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Azure AI Foundry Agent");
            throw;
        }
    }
}
