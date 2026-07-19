using System.Net.Http.Json;
using Sprite2World.Contracts;
using Sprite2World.Domain;

namespace Sprite2World.Infrastructure;

public sealed class WorkerClient(HttpClient http)
{
    public async Task<ImportAssetsResponse> ImportAsync(ImportAssetsRequest request, CancellationToken cancellationToken = default) => await Post<ImportAssetsRequest, ImportAssetsResponse>("internal/assets/process", request, cancellationToken);
    public async Task<List<AssetDefinition>> ListSharedAssetsAsync(CancellationToken cancellationToken = default) => await http.GetFromJsonAsync<List<AssetDefinition>>("internal/assets/library", cancellationToken) ?? [];
    public async Task<List<AssetFolderDefinition>> ListSharedFoldersAsync(CancellationToken cancellationToken = default) => await http.GetFromJsonAsync<List<AssetFolderDefinition>>("internal/assets/library/folders", cancellationToken) ?? [];
    public async Task SyncSharedAssetsAsync(IEnumerable<AssetDefinition> assets, IEnumerable<AssetFolderDefinition> folders, CancellationToken cancellationToken = default) { using var response = await http.PostAsJsonAsync("internal/assets/library", new SyncAssetLibraryRequest(assets.Where(asset => asset.Url.StartsWith("/data/library/", StringComparison.Ordinal)).ToList(), folders.ToList()), cancellationToken); await Ensure(response, cancellationToken); }
    public async Task RemoveAssetsAsync(RemoveAssetsRequest request, CancellationToken cancellationToken = default) { using var response = await http.PostAsJsonAsync("internal/assets/remove", request, cancellationToken); await Ensure(response, cancellationToken); }
    public async Task<SaveAssetImageResponse> SaveAssetImageAsync(SaveAssetImageRequest request, CancellationToken cancellationToken = default) => await Post<SaveAssetImageRequest, SaveAssetImageResponse>("internal/assets/image", request, cancellationToken);
    public async Task<ImportAssetsResponse> DemoAsync(string projectId, CancellationToken cancellationToken = default) => await Post<DemoAssetsRequest, ImportAssetsResponse>("internal/assets/demo", new(projectId), cancellationToken);
    public async Task<WorldGenerationResult> GenerateAsync(GenerateWorldRequest request, CancellationToken cancellationToken = default) => await Post<GenerateWorldRequest, WorldGenerationResult>("internal/worlds/generate", request, cancellationToken);
    public async Task<ValidationResult> ValidateAsync(ValidateWorldRequest request, CancellationToken cancellationToken = default) => await Post<ValidateWorldRequest, ValidationResult>("internal/worlds/validate", request, cancellationToken);
    public async Task<RenderPreviewResponse> RenderAsync(RenderPreviewRequest request, CancellationToken cancellationToken = default) => await Post<RenderPreviewRequest, RenderPreviewResponse>("internal/previews/render", request, cancellationToken);
    public async Task SaveAsync(SaveProjectRequest request, CancellationToken cancellationToken = default) { using var response = await http.PostAsJsonAsync("internal/projects/save", request, cancellationToken); await Ensure(response, cancellationToken); }
    public async Task SavePreviewAsync(SaveProjectPreviewRequest request, CancellationToken cancellationToken = default) { using var response = await http.PostAsJsonAsync("internal/projects/preview", request, cancellationToken); await Ensure(response, cancellationToken); }
    public async Task<List<ProjectSummary>> ListAsync(CancellationToken cancellationToken = default) => await http.GetFromJsonAsync<List<ProjectSummary>>("internal/projects", cancellationToken) ?? [];
    public async Task<SaveProjectRequest?> LoadAsync(string id, CancellationToken cancellationToken = default) => await http.GetFromJsonAsync<SaveProjectRequest>($"internal/projects/{Uri.EscapeDataString(id)}", cancellationToken);
    public async Task DeleteProjectAsync(string id, CancellationToken cancellationToken = default) { using var response = await http.DeleteAsync($"internal/projects/{Uri.EscapeDataString(id)}", cancellationToken); await Ensure(response, cancellationToken); }
    public async Task<WorkerStatus?> StatusAsync(CancellationToken cancellationToken = default) => await http.GetFromJsonAsync<WorkerStatus>("health/details", cancellationToken);

    private async Task<TResponse> Post<TRequest, TResponse>(string uri, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(uri, request, cancellationToken); await Ensure(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: cancellationToken) ?? throw new InvalidOperationException("Worker returned an empty response.");
    }
    private static async Task Ensure(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var message = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"Worker request failed ({(int)response.StatusCode}): {message[..Math.Min(message.Length, 500)]}");
    }
}
