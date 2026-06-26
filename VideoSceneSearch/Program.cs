using System.Text.Json;
using Microsoft.Extensions.Options;
using VideoSceneSearch.Models;
using VideoSceneSearch.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddRazorPages();

// Configure Azure AI Foundry settings
builder.Services.Configure<AzureAIFoundrySettings>(
    builder.Configuration.GetSection("AzureAIFoundry"));

// Configure Video Mapping settings
builder.Configuration.AddJsonFile("videomapping.json", optional: true, reloadOnChange: true);
builder.Services.Configure<VideoMappingSettings>(
    builder.Configuration);

// FoundryAgentClient を Singleton として登録します。
// 内部で ResponsesClient を保持し、DefaultAzureCredential によるトークンキャッシュを活用します。
builder.Services.AddSingleton<IFoundryAgentClient, FoundryAgentClient>();

var app = builder.Build();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();

// Minimal API endpoint for scene search
app.MapPost("/api/scene-search", async (
    SearchRequest request,
    IFoundryAgentClient foundryClient,
    IOptions<VideoMappingSettings> videoMappingSettings,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Query))
    {
        return Results.BadRequest(new { error = "Query is required" });
    }

    try
    {
        // Build available videos dictionary (videoId -> title)
        var availableVideos = videoMappingSettings.Value.VideoMapping
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Title);

        // Get JSON response from agent
        var jsonResult = await foundryClient.SearchScenesAsync(request.Query, availableVideos, cancellationToken);

        // Configure JSON options to be case-insensitive
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // Extract raw JSON from response — LLMs sometimes wrap output in markdown code blocks
        var cleanJson = jsonResult.Trim();
        var jsonStart = cleanJson.IndexOf('{');
        var jsonEnd = cleanJson.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            cleanJson = cleanJson[jsonStart..(jsonEnd + 1)];
        }

        // Parse the JSON into SceneSearchResponse
        var sceneResponse = JsonSerializer.Deserialize<SceneSearchResponse>(cleanJson, jsonOptions);

        if (sceneResponse?.Scenes == null || sceneResponse.Scenes.Count == 0)
        {
            return Results.Ok(new { scenes = new List<SceneResult>() });
        }

        // Enrich each scene with additional data
        for (int i = 0; i < sceneResponse.Scenes.Count; i++)
        {
            var scene = sceneResponse.Scenes[i];

            // Parse timestamps to seconds
            scene.StartSeconds = ParseTimeToSeconds(scene.Start);
            scene.EndSeconds = ParseTimeToSeconds(scene.End);

            // Use description from evidence if not set
            if (string.IsNullOrEmpty(scene.Description))
            {
                scene.Description = scene.Evidence;
            }

            // Mode, Location, tags, actions: keep as null/empty if not provided by agent
            // UI will conditionally show these only when they have values

            // VideoId and Title should already be set from agent response
            // If not set, use fallback values
            if (string.IsNullOrEmpty(scene.VideoId))
            {
                scene.VideoId = $"video{i + 1}";
            }
            if (string.IsNullOrEmpty(scene.Title))
            {
                scene.Title = "����";
            }
        }

        // Deduplicate scenes with same videoId + start + end timestamps
        // Keep the one with highest confidence, merge descriptions if different
        var deduplicatedScenes = sceneResponse.Scenes
            .GroupBy(s => new { s.VideoId, s.Start, s.End })
            .Select(group =>
            {
                // Get the scene with highest confidence
                var bestScene = group.OrderByDescending(s => s.Confidence).First();

                // Collect unique descriptions from all scenes in the group
                var allDescriptions = group
                    .Where(s => !string.IsNullOrEmpty(s.Description))
                    .Select(s => s.Description!)
                    .Distinct()
                    .ToList();

                // If there are multiple unique descriptions, join them
                if (allDescriptions.Count > 1)
                {
                    bestScene.Description = string.Join(" / ", allDescriptions);
                }

                // Collect unique evidence from all scenes
                var allEvidence = group
                    .Where(s => !string.IsNullOrEmpty(s.Evidence))
                    .Select(s => s.Evidence!)
                    .Distinct()
                    .ToList();

                if (allEvidence.Count > 1)
                {
                    bestScene.Evidence = string.Join("\n---\n", allEvidence);
                }

                return bestScene;
            })
            .OrderByDescending(s => s.Confidence)
            .ToList();

        logger.LogInformation("Deduplicated scenes: {Original} -> {Deduplicated}", 
            sceneResponse.Scenes.Count, deduplicatedScenes.Count);

        sceneResponse.Scenes = deduplicatedScenes;

        return Results.Ok(sceneResponse);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error calling Azure AI Foundry Agent");
        return Results.Problem(
            title: "Error calling Azure AI Foundry Agent",
            detail: ex.Message,
            statusCode: 500);
    }
});

// API endpoint to get video mapping configuration
app.MapGet("/api/video-mapping", (IOptions<VideoMappingSettings> videoMappingSettings) =>
{
    return Results.Ok(videoMappingSettings.Value.VideoMapping);
});

static double ParseTimeToSeconds(string timeString)
{
    if (string.IsNullOrWhiteSpace(timeString))
        return 0;
        
    var parts = timeString.Split(':');
    if (parts.Length == 3 &&
        int.TryParse(parts[0], out int hours) &&
        int.TryParse(parts[1], out int minutes) &&
        double.TryParse(parts[2], out double seconds))
    {
        return hours * 3600 + minutes * 60 + seconds;
    }
    return 0;
}

app.MapRazorPages();

app.Run();
