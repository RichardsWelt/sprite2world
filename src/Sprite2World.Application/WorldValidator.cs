using Sprite2World.Domain;

namespace Sprite2World.Application;

public sealed class WorldValidator : IWorldValidator
{
    public ValidationResult Validate(WorldDefinition world, IReadOnlyList<AssetDefinition> assets)
    {
        var issues = new List<ValidationIssue>();
        var start = At(world, world.Start);
        var exit = At(world, world.Exit);
        issues.Add(Check("start.exists", start is not null && IsEffectivelyWalkable(world, assets, world.Start), "Start cell is valid and walkable.", "Start cell is missing or blocked.", world.Start, true));
        issues.Add(Check("exit.exists", exit is not null && IsEffectivelyWalkable(world, assets, world.Exit), "Exit cell is valid and walkable.", "Exit cell is missing or blocked.", world.Exit, true));
        var reachable = start is not null && exit is not null && Flood(world, world.Start, assets).Contains(world.Exit);
        issues.Add(Check("exit.reachable", reachable, "Exit is reachable from the start.", "Exit cannot be reached from the start.", world.Exit, true));

        var reachableCells = Flood(world, world.Start, assets);
        var allRegions = world.Regions.All(r => Enumerable.Range(r.X, r.Width).SelectMany(x => Enumerable.Range(r.Y, r.Height).Select(y => new GridPoint(x, y))).Any(reachableCells.Contains));
        issues.Add(Check("regions.connected", allRegions, $"All {world.Regions.Count} regions are connected.", "At least one region is isolated.", null, true));
        var overlaps = world.Regions.SelectMany((a, i) => world.Regions.Skip(i + 1).Select(b => Overlap(a, b))).Any(x => x);
        issues.Add(Check("regions.overlap", !overlaps, "Regions do not overlap illegally.", "Illegal region overlap detected."));
        var collisions = world.Tiles.All(t => t.Walkable == (t.Kind is TileKind.Floor or TileKind.Door or TileKind.Decoration or TileKind.Start or TileKind.Exit));
        issues.Add(Check("collision.consistent", collisions, "Collision map matches tile walkability.", "Collision data is inconsistent.", null, true));
        var assetIds = assets.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var referencesValid = world.Tiles.Where(t => t.AssetId is not null).All(t => assetIds.Contains(t.AssetId!)) && world.Layers.SelectMany(layer => layer.Placements).All(item => assetIds.Contains(item.AssetId));
        issues.Add(Check("assets.valid", referencesValid, "All tile references resolve to imported assets.", "The map references an unknown asset."));
        var requiredRoles = world.EnvironmentType == "Overworld" ? new[] { AssetRole.Grass, AssetRole.Path } : world.EnvironmentType == "Interior" ? new[] { AssetRole.Floor, AssetRole.Wall, AssetRole.Door } : new[] { AssetRole.Floor, AssetRole.Wall };
        foreach (var role in requiredRoles)
        {
            var found = assets.Any(a => !a.Excluded && a.Role == role);
            issues.Add(new($"assets.role.{role.ToString().ToLowerInvariant()}", found ? ValidationSeverity.Info : ValidationSeverity.Warning, found, found ? $"{role} asset is available." : $"No {role} asset mapped; debug placeholders are used."));
        }
        var boundaries = world.Tiles.All(t => t.X >= 0 && t.Y >= 0 && t.X < world.Width && t.Y < world.Height);
        issues.Add(Check("map.boundaries", boundaries && world.Tiles.Count == world.Width * world.Height, "Map boundaries are valid.", "Map dimensions or cells are invalid."));
        return ValidationResult.From(issues);
    }

    public bool IsReachable(WorldDefinition world, GridPoint from, GridPoint to) => Flood(world, from).Contains(to);

    private static HashSet<GridPoint> Flood(WorldDefinition world, GridPoint from, IReadOnlyList<AssetDefinition> assets)
    {
        var cells = world.Tiles.Select(tile => new GridPoint(tile.X, tile.Y)).Where(point => IsEffectivelyWalkable(world, assets, point)).ToHashSet();
        return Flood(cells, from);
    }

    private static HashSet<GridPoint> Flood(WorldDefinition world, GridPoint from)
    {
        var cells = world.Tiles.Where(t => t.Walkable).Select(t => new GridPoint(t.X, t.Y)).ToHashSet();
        return Flood(cells, from);
    }
    private static HashSet<GridPoint> Flood(HashSet<GridPoint> cells, GridPoint from)
    {
        var visited = new HashSet<GridPoint>();
        if (!cells.Contains(from)) return visited;
        var queue = new Queue<GridPoint>(); queue.Enqueue(from); visited.Add(from);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            foreach (var n in Neighbors(p).Where(cells.Contains).Where(visited.Add)) queue.Enqueue(n);
        }
        return visited;
    }
    private static bool IsEffectivelyWalkable(WorldDefinition world, IReadOnlyList<AssetDefinition> assets, GridPoint point)
    {
        var baseCell = At(world, point); if (baseCell is null) return false;
        var top = world.Layers.Where(layer => layer.Visible).OrderByDescending(layer => layer.Order).SelectMany(layer => layer.Placements.Where(item => item.X == point.X && item.Y == point.Y).Take(1)).FirstOrDefault();
        if (top is null) return baseCell.Walkable;
        var role = assets.FirstOrDefault(asset => asset.Id == top.AssetId)?.Role;
        return role switch { AssetRole.Wall or AssetRole.Obstacle or AssetRole.Building or AssetRole.Water or AssetRole.Lava => false, AssetRole.Unknown or AssetRole.Unused or null => baseCell.Walkable, _ => true };
    }
    private static TileCell? At(WorldDefinition world, GridPoint point) => world.Tiles.FirstOrDefault(t => t.X == point.X && t.Y == point.Y);
    private static ValidationIssue Check(string id, bool passed, string success, string failure, GridPoint? point = null, bool repair = false) => new(id, passed ? ValidationSeverity.Info : ValidationSeverity.Error, passed, passed ? success : failure, point, null, !passed && repair);
    private static bool Overlap(PlacedRegion a, PlacedRegion b) => a.X < b.X + b.Width && a.X + a.Width > b.X && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;
    private static IEnumerable<GridPoint> Neighbors(GridPoint p) { yield return p with { X = p.X - 1 }; yield return p with { X = p.X + 1 }; yield return p with { Y = p.Y - 1 }; yield return p with { Y = p.Y + 1 }; }
}
