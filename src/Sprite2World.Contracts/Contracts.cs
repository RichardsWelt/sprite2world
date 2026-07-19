using Sprite2World.Domain;

namespace Sprite2World.Contracts;

public sealed record UploadedFileDto(string Name, string Base64);
public sealed record ImportAssetsRequest(string ProjectId, List<UploadedFileDto> Files);
public sealed record ImportAssetsResponse(List<AssetDefinition> Assets, List<string> Warnings);
public sealed record RemoveAssetsRequest(string ProjectId, List<string> RelativePaths);
public sealed record SaveAssetImageRequest(string ProjectId, string AssetId, string RelativePath, string Base64, MaterialChannel Channel, bool Shared = false);
public sealed record SaveAssetImageResponse(string Url, string ContentHash, int Width, int Height);
public sealed record SyncAssetLibraryRequest(List<AssetDefinition> Assets, List<AssetFolderDefinition>? Folders = null);
public sealed record DemoAssetsRequest(string ProjectId);
public sealed record GenerateWorldRequest(SemanticBlueprint Blueprint, List<AssetDefinition> Assets, int Seed);
public sealed record ValidateWorldRequest(WorldDefinition World, List<AssetDefinition> Assets);
public sealed record RenderPreviewRequest(WorldDefinition World, List<AssetDefinition> Assets, int Scale = 8);
public sealed record RenderPreviewResponse(string FileName, string Base64);
public sealed record SaveProjectPreviewRequest(string ProjectId, string Base64);
public sealed record SaveProjectRequest(
    string ProjectId,
    string Name,
    List<AssetDefinition> Assets,
    SemanticBlueprint? Blueprint,
    WorldDefinition? World,
    ValidationResult? Validation,
    string? Prompt = null,
    string? Feedback = null,
    string? Model = null,
    string? ReasoningEffort = null,
    string? WorldEnvironment = null,
    List<AssetFolderDefinition>? AssetFolders = null);
public sealed record ProjectSummary(string Id, string Name, DateTimeOffset UpdatedAt);
public sealed record WorkerStatus(string Service, string Version, DateTimeOffset Time);
