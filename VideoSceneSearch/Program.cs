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

// Configure Azure AI Search settings
builder.Services.Configure<AzureAISearchSettings>(
    builder.Configuration.GetSection("AzureAISearch"));

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

    if (request.Query.Length > 500)
    {
        return Results.BadRequest(new { error = "Query is too long" });
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

        // Agent は JSON のみを返すよう instructions で指示済み。FoundryAgentClient 内でコードフェンスを除去済み。
        var sceneResponse = JsonSerializer.Deserialize<SceneSearchResponse>(jsonResult.Trim(), jsonOptions);

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

            // Validate that the videoId returned by the agent actually exists.
            // If not, exclude the scene (agent hallucinated a videoId).
            if (!availableVideos.TryGetValue(scene.VideoId ?? "", out var officialTitle))
            {
                logger.LogWarning(
                    "Agent returned unknown videoId '{VideoId}' — excluding scene", scene.VideoId);
                scene.VideoId = null; // mark for removal below
                continue;
            }

            // Trust the official title from videomapping, not the agent's title.
            scene.Title = officialTitle;
        }

        // Remove scenes excluded due to invalid videoId
        sceneResponse.Scenes = sceneResponse.Scenes.Where(s => s.VideoId != null).ToList();

        if (sceneResponse.Scenes.Count == 0)
        {
            return Results.Ok(new { scenes = new List<SceneResult>() });
        }

        // Deduplicate: same sceneId のシーンとキーフレームをまとめる。
        // シーンドキュメント (mode=scene) を優先し、なければ最高信頼度のキーフレームを使用する。
        // シーングループキー: documentId から _keyframe_N サフィックスを除去した値（AI Search の sceneId フィールドに依存しない）。
        static string SceneGroupKey(SceneResult s)
        {
            var docId = s.DocumentId ?? s.SceneId ?? s.VideoId;
            var keyframeIdx = docId.IndexOf("_keyframe_", StringComparison.Ordinal);
            return keyframeIdx > 0 ? docId[..keyframeIdx] : docId;
        }

        var deduplicatedScenes = sceneResponse.Scenes
            .GroupBy(s => new { s.VideoId, SceneGroup = SceneGroupKey(s) })
            .Select(group =>
            {
                // scene ドキュメントを優先、なければ最高 confidence
                var best = group
                    .OrderBy(s => s.Mode == "scene" ? 0 : 1)
                    .ThenByDescending(s => s.Confidence)
                    .First();

                // 証拠 (evidence) を全エントリのもので補完
                var allEvidence = group
                    .Where(s => !string.IsNullOrEmpty(s.Evidence))
                    .Select(s => s.Evidence!)
                    .Distinct()
                    .ToList();
                if (allEvidence.Count > 1)
                    best.Evidence = string.Join("\n---\n", allEvidence);

                return best;
            })
            .OrderByDescending(s => s.Confidence)
            .ToList();

        logger.LogInformation("Deduplicated scenes: {Original} -> {Deduplicated}", 
            sceneResponse.Scenes.Count, deduplicatedScenes.Count);

        sceneResponse.Scenes = deduplicatedScenes;

        return Results.Ok(sceneResponse);
    }
    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
    {
        logger.LogWarning(ex, "Rate limit exceeded for Azure AI model");
        return Results.Problem(
            title: "リクエストが集中しています。数秒待ってから再度お試しください。",
            statusCode: 429);
    }
    catch (Exception ex)
    {
        // 例外の詳細はサーバーログにのみ記録し、クライアントには汎用メッセージを返す
        logger.LogError(ex, "Error calling Azure AI Foundry Agent");
        return Results.Problem(
            title: "エージェントへの接続に失敗しました。しばらくしてから再試行してください。",
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
