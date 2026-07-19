using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Sprite2World.Application;
using Sprite2World.Contracts;
using Sprite2World.Domain;
using Sprite2World.Infrastructure;
using Xunit;

namespace Sprite2World.Tests;

public sealed class WorldEngineTests
{
    private static readonly List<AssetDefinition> Assets =
    [
        Asset("floor", AssetRole.Floor), Asset("wall", AssetRole.Wall), Asset("door", AssetRole.Door),
        Asset("obstacle", AssetRole.Obstacle), Asset("decoration", AssetRole.Decoration)
    ];

    [Fact]
    public void Blueprint_round_trips_through_json()
    {
        var expected = DemoBlueprintFactory.Create();
        var json = JsonSerializer.Serialize(expected);
        var actual = JsonSerializer.Deserialize<SemanticBlueprint>(json);
        Assert.NotNull(actual);
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(json, JsonSerializer.Serialize(actual));
    }

    [Fact]
    public void Same_seed_produces_identical_world()
    {
        var generator = new DeterministicWorldGenerator(); var blueprint = DemoBlueprintFactory.Create(12345);
        var left = JsonSerializer.Serialize(generator.Generate(blueprint, Assets, 12345));
        var right = JsonSerializer.Serialize(generator.Generate(blueprint, Assets, 12345));
        Assert.Equal(left, right);
    }

    [Fact]
    public void Rooms_do_not_overlap_and_connections_are_reachable()
    {
        var world = new DeterministicWorldGenerator().Generate(DemoBlueprintFactory.Create(77), Assets, 77);
        foreach (var pair in world.Regions.SelectMany((a, i) => world.Regions.Skip(i + 1).Select(b => (a, b))))
            Assert.False(pair.a.X < pair.b.X + pair.b.Width && pair.a.X + pair.a.Width > pair.b.X && pair.a.Y < pair.b.Y + pair.b.Height && pair.a.Y + pair.a.Height > pair.b.Y);
        Assert.True(new WorldValidator().IsReachable(world, world.Start, world.Exit));
    }

    [Fact]
    public void Start_exit_collision_and_asset_references_validate()
    {
        var world = new DeterministicWorldGenerator().Generate(DemoBlueprintFactory.Create(99), Assets, 99);
        var result = new WorldValidator().Validate(world, Assets);
        Assert.True(result.IsValid, string.Join("; ", result.Issues.Where(x => !x.Passed).Select(x => x.Message)));
        Assert.All(world.Tiles, tile => Assert.Equal(tile.Walkable, tile.Kind is TileKind.Floor or TileKind.Door or TileKind.Decoration or TileKind.Start or TileKind.Exit));
        Assert.Contains(world.Tiles, x => x.X == world.Start.X && x.Y == world.Start.Y && x.Kind == TileKind.Start);
        Assert.Contains(world.Tiles, x => x.X == world.Exit.X && x.Y == world.Exit.Y && x.Kind == TileKind.Exit);
    }

    [Fact]
    public void Generated_decorations_have_a_terrain_underlay_for_layer_ordering()
    {
        var world = new DeterministicWorldGenerator().Generate(DemoBlueprintFactory.Create(101), Assets, 101);
        var terrain = world.Layers.Single(layer => layer.Purpose == LayerPurpose.Terrain);
        var decorations = world.Layers.Single(layer => layer.Purpose == LayerPurpose.Decoration);

        Assert.NotEmpty(decorations.Placements);
        Assert.All(decorations.Placements, decoration =>
            Assert.Contains(terrain.Placements, item => item.X == decoration.X && item.Y == decoration.Y));
    }

    [Fact]
    public void Overworld_uses_open_regions_and_outdoor_terrain_assets()
    {
        var assets = Assets.Concat([Asset("grass", AssetRole.Grass), Asset("path", AssetRole.Path)]).ToList();
        var blueprint = DemoBlueprintFactory.Create(303) with { EnvironmentType = "Overworld" };

        var world = new DeterministicWorldGenerator().Generate(blueprint, assets, blueprint.Seed);
        var terrain = world.Layers.Single(layer => layer.Purpose == LayerPurpose.Terrain);

        Assert.DoesNotContain(world.Tiles, tile => tile.Kind == TileKind.Wall);
        Assert.DoesNotContain(world.Tiles, tile => tile.Kind == TileKind.Void);
        Assert.Equal(world.Width * world.Height, world.Tiles.Count);
        Assert.All(terrain.Placements, item => Assert.Equal("grass", item.AssetId));
        Assert.InRange(world.Tiles.Count(tile => tile.Kind == TileKind.Decoration) / (double)world.Tiles.Count, 0.005, 0.04);
        Assert.True(new WorldValidator().IsReachable(world, world.Start, world.Exit));
    }

    [Fact]
    public void Overworld_decorations_reserve_their_complete_sprite_footprint()
    {
        var grass = Asset("grass", AssetRole.Grass) with { Width = 48, Height = 48 };
        var tree = Asset("tree", AssetRole.Decoration) with { Width = 96, Height = 192, Category = "Overworld/Trees" };
        tree.Tags.AddRange(["overworld", "forest"]);
        var blueprint = DemoBlueprintFactory.CreateForEnvironment("Overworld", 707) with { DecorationDensity = .5, ObstacleDensity = 0 };
        var world = new DeterministicWorldGenerator().Generate(blueprint, [grass, tree], blueprint.Seed);
        var placements = world.Layers.Single(layer => layer.Purpose == LayerPurpose.Decoration).Placements;
        var occupied = new HashSet<(int X, int Y)>();

        Assert.NotEmpty(placements);
        foreach (var placement in placements)
        {
            var footprint = Enumerable.Range(placement.X, 2).SelectMany(x => Enumerable.Range(placement.Y, 4).Select(y => (x, y))).ToList();
            Assert.All(footprint, point => Assert.InRange(point.x, 0, world.Width - 1));
            Assert.All(footprint, point => Assert.InRange(point.y, 0, world.Height - 1));
            Assert.DoesNotContain(footprint, point => occupied.Contains(point));
            foreach (var point in footprint) occupied.Add(point);
        }
    }

    [Fact]
    public async Task Starter_pack_contains_minimal_overworld_and_dungeon_sprites()
    {
        var root = Path.Combine(Path.GetTempPath(), $"s2w-{Guid.NewGuid():N}");
        try
        {
            var importer = new SafeAssetImporter(Options.Create(new StorageOptions { DataPath = root }));
            var result = await importer.CreateDemoAsync("starter");

            Assert.True(result.Assets.Count >= 15);
            Assert.Contains(result.Assets, asset => asset.Role == AssetRole.Grass && asset.Category.StartsWith("Overworld", StringComparison.Ordinal));
            Assert.Contains(result.Assets, asset => asset.Role == AssetRole.Path);
            Assert.Contains(result.Assets, asset => asset.Role == AssetRole.Road);
            Assert.Contains(result.Assets, asset => asset.Role == AssetRole.Sand);
            Assert.Contains(result.Assets, asset => asset.Role == AssetRole.Wall && asset.Category.StartsWith("Dungeon", StringComparison.Ordinal));
            Assert.All(result.Assets, asset => Assert.Equal(48, asset.Width));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("Dungeon")]
    [InlineData("Interior")]
    [InlineData("Overworld")]
    public void Environment_fallback_blueprints_have_valid_connected_graphs(string environment)
    {
        var blueprint = DemoBlueprintFactory.CreateForEnvironment(environment, 404);

        Assert.Empty(BlueprintValidator.Validate(blueprint));
        Assert.Equal(environment, blueprint.EnvironmentType);
        Assert.NotEqual(blueprint.StartRegionId, blueprint.ExitRegionId);
    }

    [Fact]
    public void Blueprint_validator_rejects_duplicate_self_disconnected_and_insufficient_loop_graphs()
    {
        var blueprint = DemoBlueprintFactory.Create() with
        {
            RequiredLoops = 2,
            Connections = [new("entrance", "entrance"), new("entrance", "hall"), new("hall", "entrance")]
        };

        var errors = BlueprintValidator.Validate(blueprint);

        Assert.Contains(errors, error => error.Contains("self-connection", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("disconnected", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("loop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Blueprint_graph_repair_fixes_overworld_model_graph_without_fallback()
    {
        var source = DemoBlueprintFactory.CreateForEnvironment("Overworld", 606) with
        {
            RequiredLoops = 1,
            DesiredDeadEnds = 2,
            Connections =
            [
                new("village", "grove", "Path"),
                new("grove", "beach", "Path"),
                new("beach", "pond", "Path"),
                new("pond", "ruins", "Path"),
                new("ruins", "crossroads", "Path"),
                new("crossroads", "cliff", "Path")
            ]
        };

        Assert.Contains(BlueprintValidator.Validate(source), error => error.Contains("loop", StringComparison.OrdinalIgnoreCase));

        var repaired = BlueprintGraphRepair.Repair(source);

        Assert.Empty(BlueprintValidator.Validate(repaired));
        Assert.Equal("Overworld", repaired.EnvironmentType);
        Assert.Equal(2, repaired.DesiredDeadEnds);
        Assert.True(repaired.Connections.Count >= repaired.Regions.Count);
    }

    [Fact]
    public void Blueprint_graph_repair_removes_invalid_edges_and_connects_components()
    {
        var source = DemoBlueprintFactory.Create() with
        {
            Connections = [new("entrance", "entrance"), new("entrance", "hall"), new("hall", "entrance")]
        };

        var repaired = BlueprintGraphRepair.Repair(source);

        Assert.Empty(BlueprintValidator.Validate(repaired));
        Assert.DoesNotContain(repaired.Connections, connection => connection.From == connection.To);
        Assert.Equal(repaired.Connections.Count, repaired.Connections.Select(connection => string.CompareOrdinal(connection.From, connection.To) < 0 ? $"{connection.From}|{connection.To}" : $"{connection.To}|{connection.From}").Distinct().Count());
    }

    [Fact]
    public void Interior_generation_uses_distinct_building_layout_and_doors()
    {
        var blueprint = DemoBlueprintFactory.CreateForEnvironment("Interior", 505);
        var world = new DeterministicWorldGenerator().Generate(blueprint, Assets, blueprint.Seed);

        Assert.Equal("Interior", world.EnvironmentType);
        Assert.Contains(world.Tiles, tile => tile.Kind == TileKind.Door);
        Assert.Contains(world.Tiles, tile => tile.Kind == TileKind.Wall);
        Assert.True(new WorldValidator().IsReachable(world, world.Start, world.Exit));
    }

    [Fact]
    public void Sprite_quality_analyzer_flags_noise_and_edge_cropping()
    {
        var pixels = new List<GeneratedSpritePixel>();
        for (var y = 3; y <= 8; y++) for (var x = 0; x <= 5; x++) pixels.Add(new(x, y, "#ffffff"));
        pixels.Add(new(15, 15, "#ffffff"));

        var report = SpriteQualityAnalyzer.Analyze(16, 16, pixels);

        Assert.False(report.Passed);
        Assert.Contains(report.Issues, issue => issue.Code == "isolated-pixels");
        Assert.Contains(report.Issues, issue => issue.Code == "touches-edge");
    }

    [Fact]
    public void Sprite_quality_analyzer_accepts_a_coherent_centered_subject()
    {
        var pixels = new List<GeneratedSpritePixel>();
        for (var y = 4; y <= 11; y++) for (var x = 5; x <= 10; x++) pixels.Add(new(x, y, "#ffffff"));

        var report = SpriteQualityAnalyzer.Analyze(16, 16, pixels);

        Assert.True(report.Passed, string.Join("; ", report.Issues.Select(issue => issue.Message)));
    }

    [Fact]
    public void Missing_required_asset_roles_are_reported_as_warnings()
    {
        var world = new DeterministicWorldGenerator().Generate(DemoBlueprintFactory.Create(), [], 1);
        var result = new WorldValidator().Validate(world, []);
        Assert.Contains(result.Issues, x => x.CheckId == "assets.role.floor" && !x.Passed && x.Severity == ValidationSeverity.Warning);
        Assert.Contains(result.Issues, x => x.CheckId == "assets.role.wall" && !x.Passed && x.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public void Automatic_repair_removes_route_blocker()
    {
        var tiles = new List<TileCell>
        {
            new(0,0,TileKind.Start,true), new(1,0,TileKind.Obstacle,false), new(2,0,TileKind.Exit,true)
        };
        var world = new WorldDefinition { Seed = 1, Width = 3, Height = 1, Tiles = tiles, Regions = [new("room", "Room", 0, 0, 3, 1)], Start = new(0,0), Exit = new(2,0) };
        var validator = new WorldValidator(); var repaired = new WorldRepairService(validator).Repair(world, [], 2);
        Assert.True(repaired.Validation.IsValid);
        Assert.True(repaired.World.Tiles.Single(x => x.X == 1).Walkable);
        Assert.NotEmpty(repaired.World.Repairs);
    }

    [Fact]
    public void Project_export_contains_complete_playable_state()
    {
        var blueprint = DemoBlueprintFactory.Create(); var world = new DeterministicWorldGenerator().Generate(blueprint, Assets, blueprint.Seed); var validation = new WorldValidator().Validate(world, Assets);
        var json = new JsonWorldExporter().ExportJson(new("1.0", "test", "Test", DateTimeOffset.UnixEpoch, Assets, blueprint, world, validation));
        using var document = JsonDocument.Parse(json);
        Assert.Equal("1.0", document.RootElement.GetProperty("schemaVersion").GetString());
        Assert.True(document.RootElement.GetProperty("world").GetProperty("tiles").GetArrayLength() > 0);
        Assert.True(document.RootElement.GetProperty("validation").GetProperty("isValid").GetBoolean());
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Zip_path_traversal_is_rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), $"s2w-{Guid.NewGuid():N}");
        try
        {
            var png = PngCodec.EncodeRgba(2, 2, (_, _) => ((byte)1, (byte)2, (byte)3, (byte)255));
            using var memory = new MemoryStream();
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true)) { var entry = archive.CreateEntry("../escape.png"); await using var output = entry.Open(); await output.WriteAsync(png); }
            var importer = new SafeAssetImporter(Options.Create(new StorageOptions { DataPath = root }));
            await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(new("project", [new("bad.zip", Convert.ToBase64String(memory.ToArray()))])));
            Assert.False(File.Exists(Path.Combine(root, "escape.png")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Imported_asset_can_be_physically_removed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"s2w-{Guid.NewGuid():N}");
        try
        {
            var importer = new SafeAssetImporter(Options.Create(new StorageOptions { DataPath = root }));
            var png = PngCodec.EncodeRgba(2, 2, (_, _) => ((byte)1, (byte)2, (byte)3, (byte)255));
            var result = await importer.ImportAsync(new("project", [new("sprites/hero.png", Convert.ToBase64String(png))]));
            var asset = Assert.Single(result.Assets);
            var path = Path.Combine(root, "library", "assets", "sprites", "hero.png");
            Assert.True(File.Exists(path));

            await importer.RemoveAsync(new("project", [asset.RelativePath]));

            Assert.False(File.Exists(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Saved_project_can_be_deleted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"s2w-{Guid.NewGuid():N}");
        try
        {
            var store = new ProjectFileStore(Options.Create(new StorageOptions { DataPath = root }));
            await store.SaveAsync(new("delete-me", "Delete me", [], null, null, null));
            Assert.Single(store.List());

            await store.DeleteAsync("delete-me");

            Assert.Empty(store.List());
            Assert.Null(await store.LoadAsync("delete-me"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Saved_project_preserves_prompt_and_model_settings()
    {
        var root = Path.Combine(Path.GetTempPath(), $"s2w-{Guid.NewGuid():N}");
        try
        {
            var store = new ProjectFileStore(Options.Create(new StorageOptions { DataPath = root }));
            await store.SaveAsync(new("autosave", "Autosave", [], null, null, null, "A beach world", "More paths", "gpt-5.6-luna", "low", "Overworld"));

            var loaded = Assert.IsType<SaveProjectRequest>(await store.LoadAsync("autosave"));

            Assert.Equal("A beach world", loaded.Prompt);
            Assert.Equal("More paths", loaded.Feedback);
            Assert.Equal("gpt-5.6-luna", loaded.Model);
            Assert.Equal("low", loaded.ReasoningEffort);
            Assert.Equal("Overworld", loaded.WorldEnvironment);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Png_encoder_outputs_valid_dimensions()
    {
        var bytes = PngCodec.EncodeRgba(17, 13, (_, _) => ((byte)12, (byte)34, (byte)56, (byte)255));
        Assert.Equal((17, 13), PngCodec.ReadDimensions(bytes));
    }

    [Fact]
    public void OpenAI_model_catalog_defaults_to_Luna_and_has_unique_ids()
    {
        Assert.Equal("gpt-5.6-luna", OpenAiModelCatalog.DefaultModelId);
        Assert.Equal(OpenAiModelCatalog.Models.Count, OpenAiModelCatalog.Models.Select(model => model.Id).Distinct().Count());
        Assert.Contains(OpenAiModelCatalog.Models, model => model.Id == OpenAiModelCatalog.DefaultModelId);
        Assert.Contains(OpenAiModelCatalog.Models, model => model.Id == "gpt-5.6-terra");
        Assert.Contains(OpenAiModelCatalog.Models, model => model.Id == "gpt-5.6-sol");
    }

    [Fact]
    public void Drawing_layers_and_asset_categories_round_trip_through_json()
    {
        var asset = Asset("palm", AssetRole.Decoration) with { Category = "Strand" };
        var world = new DeterministicWorldGenerator().Generate(DemoBlueprintFactory.Create(12), [asset], 12);
        world.Layers.Add(new() { Id = "decor", Name = "Palmen", Order = 1, Placements = [new("p1", asset.Id, 3, 4)] });

        var json = JsonSerializer.Serialize(new { asset, world });
        using var document = JsonDocument.Parse(json);

        Assert.Equal("Strand", document.RootElement.GetProperty("asset").GetProperty("Category").GetString());
        var palmLayer = document.RootElement.GetProperty("world").GetProperty("Layers").EnumerateArray().Single(layer => layer.GetProperty("Name").GetString() == "Palmen");
        Assert.Equal("palm", palmLayer.GetProperty("Placements")[0].GetProperty("AssetId").GetString());
    }

    [Fact]
    public void Visible_wall_on_drawing_layer_blocks_playtest_route_validation()
    {
        var wall = Asset("wall-brush", AssetRole.Wall);
        var world = new WorldDefinition
        {
            Seed = 1, Width = 3, Height = 1,
            Tiles = [new(0,0,TileKind.Start,true), new(1,0,TileKind.Floor,true), new(2,0,TileKind.Exit,true)],
            Regions = [new("room", "Room", 0, 0, 3, 1)], Start = new(0,0), Exit = new(2,0),
            Layers = [new() { Id = "walls", Name = "Wände", Order = 0, Placements = [new("p1", wall.Id, 1, 0)] }]
        };

        var result = new WorldValidator().Validate(world, [wall, Asset("floor-required", AssetRole.Floor)]);

        Assert.Contains(result.Issues, issue => issue.CheckId == "exit.reachable" && !issue.Passed);
    }

    private static AssetDefinition Asset(string id, AssetRole role) => new() { Id = id, FileName = $"{id}.png", RelativePath = $"{id}.png", Url = $"/data/{id}.png", ContentHash = id.PadRight(64, '0'), Width = 16, Height = 16, Role = role, Confidence = 1, ClassificationSource = "Test" };
}
