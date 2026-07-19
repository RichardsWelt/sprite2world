using System.Text.Json.Serialization;

namespace Sprite2World.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssetRole { Floor, Wall, Door, Obstacle, Decoration, Building, Road, Path, Grass, Sand, Water, Lava, Bridge, StartMarker, ExitMarker, Unused, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DerivedAssetStatus { Missing, Generating, Ready, Review, Failed, Manual }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterialChannel { Base, Normal, Metallic, Roughness, Lit }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TileKind { Void, Floor, Wall, Door, Obstacle, Decoration, Start, Exit }

public sealed record AssetDefinition
{
    public required string Id { get; init; }
    public required string FileName { get; set; }
    public required string RelativePath { get; init; }
    public required string Url { get; set; }
    public required string ContentHash { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public AssetRole Role { get; set; } = AssetRole.Unknown;
    public double Confidence { get; set; }
    public string ClassificationSource { get; set; } = "Unclassified";
    public bool ManualOverride { get; set; }
    public bool Excluded { get; set; }
    public string Category { get; set; } = "Allgemein";
    public List<string> Tags { get; init; } = [];
    public string? NormalMapUrl { get; set; }
    public DerivedAssetStatus NormalMapStatus { get; set; } = DerivedAssetStatus.Missing;
    public double NormalStrength { get; set; } = 1;
    public bool NormalInvertY { get; set; }
    public bool NormalReviewed { get; set; }
    public string? NormalMapSourceHash { get; set; }
    public string? MetallicMapUrl { get; set; }
    public string? RoughnessMapUrl { get; set; }
    public double MetallicStrength { get; set; }
    public double RoughnessStrength { get; set; } = .5;
    public double LightIntensity { get; set; } = 1;
    public SpriteAiMetadata? AiMetadata { get; set; }
}

public sealed record AssetFolderDefinition(string Theme, string Subfolder);

public sealed record SpriteAiMetadata(string Prompt, string Model, DateTimeOffset GeneratedAt, string GeneratorVersion, string View, string Framing, string Outline, string Lighting, List<string> Palette, int Attempts, bool QualityPassed, List<string> QualityIssues);

public sealed record SemanticBlueprint
{
    public string SchemaVersion { get; init; } = "1.0";
    public string Theme { get; init; } = "Abandoned Dungeon";
    public string WorldType { get; init; } = "TopDownRooms";
    public string EnvironmentType { get; init; } = "Dungeon";
    public int WidthHint { get; init; } = 56;
    public int HeightHint { get; init; } = 40;
    public List<BlueprintRegion> Regions { get; init; } = [];
    public List<BlueprintConnection> Connections { get; init; } = [];
    public string StartRegionId { get; init; } = "entrance";
    public string ExitRegionId { get; init; } = "sanctum";
    public int RequiredLoops { get; init; } = 1;
    public int DesiredDeadEnds { get; init; } = 2;
    public double DecorationDensity { get; init; } = .2;
    public double ObstacleDensity { get; init; } = .08;
    public int Seed { get; init; } = 424242;
    public List<string> Constraints { get; init; } = [];
}

public sealed record BlueprintRegion(string Id, string Name, string Purpose, string Size, List<string> Tags);
public sealed record BlueprintConnection(string From, string To, string Type = "Corridor", bool Required = true);
public readonly record struct GridPoint(int X, int Y);
public sealed record PlacedRegion(string Id, string Name, int X, int Y, int Width, int Height);
public sealed record TileCell(int X, int Y, TileKind Kind, bool Walkable, string? AssetId = null, string? RegionId = null);
public sealed record AssetPlacement(string Id, string AssetId, int X, int Y, double OffsetX = 0, double OffsetY = 0);
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LayerPurpose { Terrain, Decoration, Custom }
public sealed record WorldLayer
{
    public required string Id { get; init; }
    public string Name { get; set; } = "Ebene";
    public int Order { get; set; }
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public LayerPurpose Purpose { get; set; } = LayerPurpose.Custom;
    public bool Generated { get; set; }
    public List<AssetPlacement> Placements { get; init; } = [];
}

public sealed record WorldDefinition
{
    public string SchemaVersion { get; init; } = "1.0";
    public string GeneratorVersion { get; init; } = "1.0.0";
    public string EnvironmentType { get; init; } = "Dungeon";
    public int Seed { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int TileSize { get; init; } = 32;
    public required List<TileCell> Tiles { get; init; }
    public required List<PlacedRegion> Regions { get; init; }
    public required GridPoint Start { get; init; }
    public required GridPoint Exit { get; init; }
    public List<int> AttemptedSeeds { get; init; } = [];
    public List<string> Repairs { get; init; } = [];
    public List<WorldLayer> Layers { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ValidationSeverity { Info, Warning, Error }
public sealed record ValidationIssue(string CheckId, ValidationSeverity Severity, bool Passed, string Message, GridPoint? Position = null, string? RegionId = null, bool RepairAvailable = false);
public sealed record ValidationResult(bool IsValid, List<ValidationIssue> Issues)
{
    public static ValidationResult From(IEnumerable<ValidationIssue> issues)
    {
        var list = issues.ToList();
        return new(list.All(x => x.Passed || x.Severity != ValidationSeverity.Error), list);
    }
}

public sealed record WorldGenerationResult(WorldDefinition World, ValidationResult Validation);
public sealed record ProjectExport(string SchemaVersion, string ProjectId, string Name, DateTimeOffset ExportedAt, List<AssetDefinition> Assets, SemanticBlueprint Blueprint, WorldDefinition World, ValidationResult Validation);
