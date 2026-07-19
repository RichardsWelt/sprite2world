using Sprite2World.Domain;

namespace Sprite2World.Application;

public interface IWorldGenerator { WorldDefinition Generate(SemanticBlueprint blueprint, IReadOnlyList<AssetDefinition> assets, int seed); }
public interface IWorldValidator { ValidationResult Validate(WorldDefinition world, IReadOnlyList<AssetDefinition> assets); bool IsReachable(WorldDefinition world, GridPoint from, GridPoint to); }
public interface IWorldRepairService { WorldGenerationResult Repair(WorldDefinition world, IReadOnlyList<AssetDefinition> assets, int maximumAttempts = 3); }
public interface IBlueprintService
{
    Task<SemanticBlueprint> CreateAsync(string prompt, string model, string reasoningEffort, CancellationToken cancellationToken = default);
    Task<SemanticBlueprint> ImproveAsync(string prompt, SemanticBlueprint current, ValidationResult? validation, string feedback, string model, string reasoningEffort, CancellationToken cancellationToken = default);
}
public interface IAssetClassificationService { Task<IReadOnlyList<AssetDefinition>> ClassifyAsync(IReadOnlyList<AssetDefinition> assets, string model, CancellationToken cancellationToken = default); }
public interface ISpriteGenerationService
{
    Task<GeneratedSprite> GenerateSpriteAsync(string prompt, int width, int height, string model, SpriteGenerationOptions? options = null, CancellationToken cancellationToken = default);
}
public sealed record SpriteGenerationOptions(string View = "Auto", string Framing = "Full subject", string Outline = "1 px", string Lighting = "Top left", string PaletteHint = "", string ReasoningEffort = "medium");
public sealed record GeneratedSpriteComposition(string View, string Framing, IReadOnlyList<string> VisibleParts, IReadOnlyList<string> StructuralRules);
public sealed record SpriteQualityIssue(string Code, string Message, bool Repairable = true);
public sealed record SpriteQualityReport(bool Passed, int ConnectedComponents, int IsolatedPixels, int TransparentHoles, double CanvasFill, IReadOnlyList<SpriteQualityIssue> Issues);
public sealed record SpriteGenerationMetadata(string Prompt, string Model, DateTimeOffset GeneratedAt, string GeneratorVersion, string View, string Framing, string Outline, string Lighting, IReadOnlyList<string> Palette, int Attempts);
public sealed record GeneratedSprite(string Name, string Description, IReadOnlyList<GeneratedSpritePixel> Pixels, GeneratedSpriteComposition Composition, SpriteQualityReport Quality, SpriteGenerationMetadata Metadata);
public sealed record GeneratedSpritePixel(int X, int Y, string Color);
public interface IWorldExporter { string ExportJson(ProjectExport project); }
