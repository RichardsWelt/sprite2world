using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sprite2World.Application;
using Sprite2World.Contracts;
using Sprite2World.Domain;

namespace Sprite2World.Infrastructure;

public sealed class DemoProjectSeeder(
    IOptions<StorageOptions> options,
    SafeAssetImporter importer,
    ProjectFileStore store,
    PreviewRenderer renderer,
    IWorldGenerator generator,
    IWorldRepairService repair,
    ILogger<DemoProjectSeeder> logger)
{
    private const string SeedVersion = "demo-projects-shared-library-v4";
    private readonly string _markerPath = Path.Combine(options.Value.DataPath, "projects", $".{SeedVersion}");

    public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_markerPath)) return;

        // Created in reverse display order because the library shows the newest project first.
        var demos = new[]
        {
            new DemoProject("demo-interior-v1", "Demo · Interior", "Interior", 505,
                "Create a coherent townhouse interior with a foyer, living room, kitchen, office, storage room and rear terrace. Connect the rooms with believable doors and keep every room reachable."),
            new DemoProject("demo-dungeon-v1", "Demo · Dungeon", "Dungeon", 424242,
                DemoBlueprintFactory.DungeonPrompt),
            new DemoProject("demo-overworld-v1", "Demo · Overworld", "Overworld", 606,
                "Create one continuous outdoor landscape with grass, a small village, woodland, beach, crossroads and a beacon cliff. Keep decoration sparse and connect the landmarks with readable paths and roads.")
        };

        foreach (var demo in demos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await importer.CreateDemoAsync(demo.Id, cancellationToken);
            var library = await importer.LoadLibraryAsync(cancellationToken);
            var assets = library.Where(asset => IsUsefulForDemo(asset, demo.Environment)).ToList();
            var blueprint = DemoBlueprintFactory.CreateForEnvironment(demo.Environment, demo.Seed);
            var generated = generator.Generate(blueprint, assets, demo.Seed);
            var result = repair.Repair(generated, assets);
            var folders = BuildFolders(assets);
            var project = new SaveProjectRequest(
                demo.Id, demo.Name, assets, blueprint, result.World, result.Validation,
                demo.Prompt, "Ready to explore and customize.", null, "medium", demo.Environment, folders);

            await store.SaveAsync(project, cancellationToken);
            await store.SavePreviewAsync(demo.Id, renderer.Render(result.World, 3, assets), cancellationToken);
            logger.LogInformation("Created starter project {ProjectId} ({Environment})", demo.Id, demo.Environment);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);
        await File.WriteAllTextAsync(_markerPath, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
    }

    private static List<AssetFolderDefinition> BuildFolders(IEnumerable<AssetDefinition> assets) => assets
        .Select(asset => (asset.Category ?? "General/Sprites").Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        .Select(parts => new AssetFolderDefinition(parts.FirstOrDefault() ?? "General", parts.Length > 1 ? parts[1] : "Sprites"))
        .Distinct()
        .ToList();

    private static bool IsUsefulForDemo(AssetDefinition asset, string environment)
    {
        if (asset.Role is AssetRole.StartMarker or AssetRole.ExitMarker) return true;
        if (environment == "Overworld") return asset.Category.StartsWith("Overworld", StringComparison.OrdinalIgnoreCase) || asset.Category.StartsWith("Architecture", StringComparison.OrdinalIgnoreCase);
        if (environment == "Interior") return asset.Category.StartsWith("Dungeon", StringComparison.OrdinalIgnoreCase) || asset.Category.StartsWith("Architecture", StringComparison.OrdinalIgnoreCase);
        return asset.Category.StartsWith("Dungeon", StringComparison.OrdinalIgnoreCase) || asset.Category.StartsWith("Architecture", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record DemoProject(string Id, string Name, string Environment, int Seed, string Prompt);
}
