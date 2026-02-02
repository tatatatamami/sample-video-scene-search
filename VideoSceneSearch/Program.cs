using Azure.Identity;
using VideoSceneSearch.Models;
using VideoSceneSearch.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddRazorPages();

// Configure Azure AI Foundry settings
builder.Services.Configure<AzureAIFoundrySettings>(
    builder.Configuration.GetSection("AzureAIFoundry"));

// Register DefaultAzureCredential as singleton for reuse across requests
builder.Services.AddSingleton<DefaultAzureCredential>();

// Add HttpClient and Foundry Agent Client
builder.Services.AddHttpClient<IFoundryAgentClient, FoundryAgentClient>();

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
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Query))
    {
        return Results.BadRequest(new { error = "Query is required" });
    }

    try
    {
        var result = await foundryClient.SearchScenesAsync(request.Query, cancellationToken);
        return Results.Ok(new { response = result });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Error calling Azure AI Foundry Agent",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.MapRazorPages();

app.Run();
