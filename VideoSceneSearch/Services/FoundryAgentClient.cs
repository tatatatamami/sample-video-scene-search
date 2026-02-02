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
        ILogger<FoundryAgentClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _credential = new DefaultAzureCredential();
        _logger = logger;
    }

    public async Task<string> SearchScenesAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            // Get access token using Entra ID
            var tokenRequestContext = new TokenRequestContext(new[] { _settings.Scope });
            var token = await _credential.GetTokenAsync(tokenRequestContext, cancellationToken);

            _logger.LogInformation("Obtained Entra ID token for Azure AI Foundry");

            // Prepare the request payload
            var requestBody = new
            {
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = query
                    }
                }
            };

            var jsonContent = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            // Create request with Bearer token
            var request = new HttpRequestMessage(HttpMethod.Post, _settings.Endpoint)
            {
                Content = content
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            _logger.LogInformation("Sending request to Azure AI Foundry Agent: {Endpoint}", _settings.Endpoint);

            // Send request to Foundry Agent
            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation("Received response from Azure AI Foundry Agent");

            return responseContent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Azure AI Foundry Agent");
            throw;
        }
    }
}
