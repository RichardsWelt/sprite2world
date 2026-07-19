using Microsoft.AspNetCore.Diagnostics;
using Sprite2World.Application;
using Sprite2World.Contracts;
using Sprite2World.Infrastructure;

if (args.Contains("--healthcheck", StringComparer.Ordinal))
{
    try { using var client = new HttpClient(); using var response = await client.GetAsync("http://localhost:8080/health"); Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1; }
    catch (HttpRequestException) { Environment.ExitCode = 1; }
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<StorageOptions>(o =>
{
    o.DataPath = builder.Configuration["SPRITE2WORLD_DATA_PATH"] ?? "/app/data";
    o.MaxAssets = builder.Configuration.GetValue("Limits:MaxAssets", 10_000);
});
builder.Services.AddSingleton<SafeAssetImporter>();
builder.Services.AddSingleton<ProjectFileStore>();
builder.Services.AddSingleton<PreviewRenderer>();
builder.Services.AddSingleton<DemoProjectSeeder>();
builder.Services.AddSingleton<IWorldGenerator, DeterministicWorldGenerator>();
builder.Services.AddSingleton<IWorldValidator, WorldValidator>();
builder.Services.AddSingleton<IWorldRepairService, WorldRepairService>();
builder.Services.AddHealthChecks();
var app = builder.Build();
app.UseExceptionHandler(error => error.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    context.Response.StatusCode = exception is InvalidDataException ? 400 : 500;
    await context.Response.WriteAsJsonAsync(new { error = exception?.Message ?? "Worker operation failed.", correlationId = context.TraceIdentifier });
}));
app.MapHealthChecks("/health");
app.MapGet("/health/details", () => new WorkerStatus("sprite2world-worker", "1.0.0", DateTimeOffset.UtcNow));
app.MapPost("/internal/assets/process", (ImportAssetsRequest request, SafeAssetImporter importer, CancellationToken ct) => importer.ImportAsync(request, ct));
app.MapGet("/internal/assets/library", (SafeAssetImporter importer, CancellationToken ct) => importer.LoadLibraryAsync(ct));
app.MapGet("/internal/assets/library/folders", (SafeAssetImporter importer, CancellationToken ct) => importer.LoadLibraryFoldersAsync(ct));
app.MapPost("/internal/assets/library", async (SyncAssetLibraryRequest request, SafeAssetImporter importer, CancellationToken ct) => { await importer.UpsertLibraryAsync(request.Assets, ct); if (request.Folders is not null) await importer.SaveLibraryFoldersAsync(request.Folders, ct); return Results.NoContent(); });
app.MapPost("/internal/assets/remove", async (RemoveAssetsRequest request, SafeAssetImporter importer, CancellationToken ct) => { await importer.RemoveAsync(request, ct); return Results.NoContent(); });
app.MapPost("/internal/assets/image", (SaveAssetImageRequest request, SafeAssetImporter importer, CancellationToken ct) => importer.SaveImageAsync(request, ct));
app.MapPost("/internal/assets/demo", (DemoAssetsRequest request, SafeAssetImporter importer, CancellationToken ct) => importer.CreateDemoAsync(request.ProjectId, ct));
app.MapPost("/internal/worlds/generate", (GenerateWorldRequest request, IWorldGenerator generator, IWorldValidator validator, IWorldRepairService repair) =>
{
    var world = generator.Generate(request.Blueprint, request.Assets, request.Seed);
    return Results.Ok(repair.Repair(world, request.Assets));
});
app.MapPost("/internal/worlds/validate", (ValidateWorldRequest request, IWorldValidator validator) => validator.Validate(request.World, request.Assets));
app.MapPost("/internal/previews/render", (RenderPreviewRequest request, PreviewRenderer renderer) => new RenderPreviewResponse("sprite2world-preview.png", Convert.ToBase64String(renderer.Render(request.World, request.Scale, request.Assets))));
app.MapPost("/internal/projects/save", async (SaveProjectRequest request, ProjectFileStore store, PreviewRenderer renderer, CancellationToken ct) =>
{
    await store.SaveAsync(request, ct);
    if (request.World is not null && !store.HasPreview(request.ProjectId)) await store.SavePreviewAsync(request.ProjectId, renderer.Render(request.World, 3, request.Assets), ct);
    return Results.NoContent();
});
app.MapPost("/internal/projects/preview", async (SaveProjectPreviewRequest request, ProjectFileStore store, CancellationToken ct) =>
{
    byte[] png;
    try { png = Convert.FromBase64String(request.Base64); }
    catch (FormatException) { return Results.BadRequest(new { error = "Preview is not valid base64." }); }
    if (png.Length > 12 * 1024 * 1024) return Results.BadRequest(new { error = "Preview is too large." });
    var dimensions = PngCodec.ReadDimensions(png);
    if (dimensions.Width > 1600 || dimensions.Height > 1600) return Results.BadRequest(new { error = "Preview dimensions exceed 1600 pixels." });
    await store.SavePreviewAsync(request.ProjectId, png, ct);
    return Results.NoContent();
});
app.MapGet("/internal/projects", (ProjectFileStore store) => store.List().OrderByDescending(x => x.UpdatedAt).ToList());
app.MapGet("/internal/projects/{id}", async (string id, ProjectFileStore store, CancellationToken ct) => await store.LoadAsync(id, ct) is { } project ? Results.Ok(project) : Results.NotFound());
app.MapDelete("/internal/projects/{id}", async (string id, ProjectFileStore store) => { await store.DeleteAsync(id); return Results.NoContent(); });
await app.Services.GetRequiredService<SafeAssetImporter>().EnsureBundledAssetsAsync();
await app.Services.GetRequiredService<DemoProjectSeeder>().EnsureSeededAsync();
app.Run();
