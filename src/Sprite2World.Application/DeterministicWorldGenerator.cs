using Sprite2World.Domain;

namespace Sprite2World.Application;

public sealed class DeterministicWorldGenerator : IWorldGenerator
{
    public WorldDefinition Generate(SemanticBlueprint blueprint, IReadOnlyList<AssetDefinition> assets, int seed)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        var regions = blueprint.Regions.Count > 0 ? blueprint.Regions : DemoBlueprintFactory.Create(seed).Regions;
        var width = Math.Clamp(blueprint.WidthHint, 32, 128);
        var height = Math.Clamp(blueprint.HeightHint, 26, 128);
        var random = new Random(seed);
        var isOverworld = string.Equals(blueprint.EnvironmentType, "Overworld", StringComparison.OrdinalIgnoreCase);
        var isInterior = string.Equals(blueprint.EnvironmentType, "Interior", StringComparison.OrdinalIgnoreCase);
        var map = new MutableTile[width, height];
        for (var x = 0; x < width; x++) for (var y = 0; y < height; y++) map[x, y] = new(TileKind.Void, false, null);

        var placed = isInterior ? PlaceInteriorRooms(regions, width, height) : PlaceRooms(regions, width, height, random);
        if (isOverworld) FillOverworld(map, placed);
        else foreach (var room in placed) CarveRoom(map, room);

        var connections = blueprint.Connections.Where(c => placed.Any(r => r.Id == c.From) && placed.Any(r => r.Id == c.To)).ToList();
        if (connections.Count == 0)
            for (var i = 1; i < placed.Count; i++) connections.Add(new(placed[i - 1].Id, placed[i].Id));
        foreach (var connection in connections) CarveCorridor(map, Center(placed.First(r => r.Id == connection.From)), Center(placed.First(r => r.Id == connection.To)), random, connection.Type);

        var uniqueConnections = connections.Select(connection => string.CompareOrdinal(connection.From, connection.To) < 0 ? $"{connection.From}\0{connection.To}" : $"{connection.To}\0{connection.From}").Distinct(StringComparer.Ordinal).Count();
        var existingLoops = Math.Max(0, uniqueConnections - placed.Count + 1);
        var loopsToAdd = Math.Max(0, Math.Clamp(blueprint.RequiredLoops, 0, 3) - existingLoops);
        var extraLoopCandidates = placed.SelectMany((a, i) => placed.Skip(i + 2).Select(b => (a, b)))
            .Where(pair => !connections.Any(c => (c.From == pair.a.Id && c.To == pair.b.Id) || (c.From == pair.b.Id && c.To == pair.a.Id)))
            .Take(loopsToAdd);
        foreach (var pair in extraLoopCandidates) CarveCorridor(map, Center(pair.a), Center(pair.b), random, isOverworld ? "Path" : "Corridor");

        if (!isOverworld) AddWalls(map);
        if (isInterior) AddInteriorDoors(map, placed, connections);
        var startRoom = placed.FirstOrDefault(r => r.Id == blueprint.StartRegionId) ?? placed[0];
        var exitRoom = placed.FirstOrDefault(r => r.Id == blueprint.ExitRegionId) ?? placed[^1];
        var start = Center(startRoom);
        var exit = Center(exitRoom);
        map[start.X, start.Y] = new(TileKind.Start, true, startRoom.Id);
        map[exit.X, exit.Y] = new(TileKind.Exit, true, exitRoom.Id);

        if (isOverworld) AddOverworldObjects(map, start, exit, blueprint, random);
        else AddObjects(map, placed, start, exit, blueprint, random);
        var roleAssets = assets.Where(a => !a.Excluded && a.Role is not AssetRole.Unknown and not AssetRole.Unused).GroupBy(a => a.Role).ToDictionary(g => g.Key, g => g.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray());
        var tiles = new List<TileCell>(width * height);
        var terrain = new WorldLayer { Id = "generated-terrain", Name = "Boden & Wände", Order = 0, Purpose = LayerPurpose.Terrain, Generated = true };
        var decorations = new WorldLayer { Id = "generated-decorations", Name = "Dekorationen", Order = 1, Purpose = LayerPurpose.Decoration, Generated = true };
        var occupiedDecorationCells = new HashSet<(int X, int Y)> { (start.X, start.Y), (exit.X, exit.Y) };
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var cell = map[x, y];
            var regionTags = regions.FirstOrDefault(region => region.Id == cell.RegionId)?.Tags ?? [];
            var assetHints = regionTags.Concat(blueprint.Theme.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();
            var role = isOverworld && cell.Kind == TileKind.Floor ? OverworldGroundRole(roleAssets) : RoleFor(cell.Kind);
            var assetId = isOverworld && cell.Kind is TileKind.Decoration or TileKind.Obstacle
                ? PickOverworldObjectAsset(roleAssets, cell.Kind, x, y, seed, assetHints)
                : PickAsset(roleAssets, role, x, y, seed, assetHints);
            tiles.Add(new(x, y, cell.Kind, cell.Walkable, null, cell.RegionId));
            var isDecoration = cell.Kind is TileKind.Obstacle or TileKind.Decoration or TileKind.Start or TileKind.Exit;
            if (isDecoration)
            {
                var floorAssetId = PickAsset(roleAssets, isOverworld ? OverworldGroundRole(roleAssets) : AssetRole.Floor, x, y, seed, assetHints);
                if (floorAssetId is not null)
                    terrain.Placements.Add(new($"generated-terrain-{x}-{y}", floorAssetId, x, y));
                if (assetId is not null)
                {
                    var asset = assets.First(item => item.Id == assetId);
                    var footprintWidth = Math.Max(1, (int)Math.Ceiling(asset.Width / 48d));
                    var footprintHeight = Math.Max(1, (int)Math.Ceiling(asset.Height / 48d));
                    var footprint = Enumerable.Range(x, footprintWidth).SelectMany(px => Enumerable.Range(y, footprintHeight).Select(py => (X: px, Y: py))).ToList();
                    if (footprint.All(point => point.X < width && point.Y < height && !occupiedDecorationCells.Contains(point)))
                    {
                        decorations.Placements.Add(new($"generated-decoration-{x}-{y}", assetId, x, y));
                        foreach (var point in footprint) occupiedDecorationCells.Add(point);
                    }
                }
            }
            else if (assetId is not null)
                terrain.Placements.Add(new($"generated-terrain-{x}-{y}", assetId, x, y));
        }
        return new() { Seed = seed, EnvironmentType = blueprint.EnvironmentType, Width = width, Height = height, Tiles = tiles, Regions = placed, Start = start, Exit = exit, AttemptedSeeds = [seed], Layers = [terrain, decorations] };
    }

    private static List<PlacedRegion> PlaceInteriorRooms(IReadOnlyList<BlueprintRegion> regions, int width, int height)
    {
        var result = new List<PlacedRegion>();
        var columns = regions.Count <= 4 ? 2 : 3;
        var rows = (int)Math.Ceiling(regions.Count / (double)columns);
        var margin = 4;
        var usableWidth = width - margin * 2;
        var usableHeight = height - margin * 2;
        var cellWidth = usableWidth / columns;
        var cellHeight = usableHeight / rows;
        for (var index = 0; index < regions.Count; index++)
        {
            var col = index % columns; var row = index / columns;
            var x = margin + col * cellWidth + 1; var y = margin + row * cellHeight + 1;
            var roomWidth = (col == columns - 1 ? usableWidth - col * cellWidth : cellWidth) - 2;
            var roomHeight = (row == rows - 1 ? usableHeight - row * cellHeight : cellHeight) - 2;
            result.Add(new(regions[index].Id, regions[index].Name, x, y, Math.Max(5, roomWidth), Math.Max(5, roomHeight)));
        }
        return result;
    }

    private static List<PlacedRegion> PlaceRooms(IReadOnlyList<BlueprintRegion> regions, int width, int height, Random random)
    {
        var result = new List<PlacedRegion>();
        var columns = regions.Count <= 4 ? 2 : 3;
        var rows = (int)Math.Ceiling(regions.Count / (double)columns);
        var cellWidth = Math.Max(9, (width - 4) / columns);
        var cellHeight = Math.Max(8, (height - 4) / rows);
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            var scale = region.Size.ToLowerInvariant() switch { "large" => 1.0, "small" => .68, _ => .82 };
            var roomWidth = Math.Clamp((int)((cellWidth - 4) * scale) + random.Next(-1, 2), 5, cellWidth - 2);
            var roomHeight = Math.Clamp((int)((cellHeight - 4) * scale) + random.Next(-1, 2), 5, cellHeight - 2);
            var col = i % columns;
            var row = i / columns;
            var baseX = 2 + col * cellWidth;
            var baseY = 2 + row * cellHeight;
            var x = Math.Min(width - roomWidth - 2, baseX + Math.Max(0, (cellWidth - roomWidth) / 2));
            var y = Math.Min(height - roomHeight - 2, baseY + Math.Max(0, (cellHeight - roomHeight) / 2));
            result.Add(new(region.Id, region.Name, x, y, roomWidth, roomHeight));
        }
        return result;
    }

    private static void CarveRoom(MutableTile[,] map, PlacedRegion room)
    {
        for (var x = room.X; x < room.X + room.Width; x++)
        for (var y = room.Y; y < room.Y + room.Height; y++) map[x, y] = new(TileKind.Floor, true, room.Id);
    }

    private static void FillOverworld(MutableTile[,] map, IReadOnlyList<PlacedRegion> regions)
    {
        // Outdoor regions are semantic areas on one continuous surface, not separate rooms.
        // Assigning every cell to its nearest region keeps biome/tag selection useful while
        // guaranteeing that an overworld never degenerates into dungeon islands.
        for (var x = 0; x < map.GetLength(0); x++)
        for (var y = 0; y < map.GetLength(1); y++)
        {
            var nearest = regions.MinBy(region =>
            {
                var center = Center(region);
                var dx = center.X - x;
                var dy = center.Y - y;
                return dx * dx + dy * dy;
            })!;
            map[x, y] = new(TileKind.Floor, true, nearest.Id);
        }
    }

    private static void CarveCorridor(MutableTile[,] map, GridPoint from, GridPoint to, Random random, string type)
    {
        var radius = type.Contains("wide", StringComparison.OrdinalIgnoreCase) || type.Contains("road", StringComparison.OrdinalIgnoreCase) ? 2 : type.Contains("door", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        if (random.Next(2) == 0) { CarveHorizontal(map, from.X, to.X, from.Y, radius); CarveVertical(map, from.Y, to.Y, to.X, radius); }
        else { CarveVertical(map, from.Y, to.Y, from.X, radius); CarveHorizontal(map, from.X, to.X, to.Y, radius); }
    }

    private static void CarveHorizontal(MutableTile[,] map, int x1, int x2, int y, int radius)
    {
        for (var x = Math.Min(x1, x2); x <= Math.Max(x1, x2); x++)
            for (var dy = -radius; dy <= radius; dy++) if (Inside(map, x, y + dy)) map[x, y + dy] = new(TileKind.Floor, true, null);
    }

    private static void CarveVertical(MutableTile[,] map, int y1, int y2, int x, int radius)
    {
        for (var y = Math.Min(y1, y2); y <= Math.Max(y1, y2); y++)
            for (var dx = -radius; dx <= radius; dx++) if (Inside(map, x + dx, y)) map[x + dx, y] = new(TileKind.Floor, true, null);
    }

    private static void AddInteriorDoors(MutableTile[,] map, IReadOnlyList<PlacedRegion> rooms, IReadOnlyList<BlueprintConnection> connections)
    {
        foreach (var connection in connections)
        {
            var from = rooms.First(room => room.Id == connection.From); var to = rooms.First(room => room.Id == connection.To);
            var a = Center(from); var b = Center(to);
            var door = Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y)
                ? new GridPoint(b.X > a.X ? from.X + from.Width : from.X - 1, a.Y)
                : new GridPoint(a.X, b.Y > a.Y ? from.Y + from.Height : from.Y - 1);
            if (Inside(map, door.X, door.Y)) map[door.X, door.Y] = new(TileKind.Door, true, from.Id);
        }
    }

    private static void AddWalls(MutableTile[,] map)
    {
        var walls = new List<GridPoint>();
        for (var x = 1; x < map.GetLength(0) - 1; x++)
        for (var y = 1; y < map.GetLength(1) - 1; y++)
            if (map[x, y].Kind == TileKind.Void && Neighbors(x, y).Any(p => map[p.X, p.Y].Walkable)) walls.Add(new(x, y));
        foreach (var wall in walls) map[wall.X, wall.Y] = new(TileKind.Wall, false, null);
    }

    private static void AddObjects(MutableTile[,] map, IReadOnlyList<PlacedRegion> rooms, GridPoint start, GridPoint exit, SemanticBlueprint blueprint, Random random)
    {
        foreach (var room in rooms)
        {
            var candidates = Enumerable.Range(room.X + 1, Math.Max(0, room.Width - 2))
                .SelectMany(x => Enumerable.Range(room.Y + 1, Math.Max(0, room.Height - 2)).Select(y => new GridPoint(x, y)))
                .Where(p => p != start && p != exit && Math.Abs(p.X - Center(room).X) + Math.Abs(p.Y - Center(room).Y) > 2)
                .OrderBy(_ => random.Next()).ToList();
            var decorations = (int)(candidates.Count * Math.Clamp(blueprint.DecorationDensity, 0, .5));
            var obstacles = (int)(candidates.Count * Math.Clamp(blueprint.ObstacleDensity, 0, .25));
            foreach (var p in candidates.Take(decorations)) map[p.X, p.Y] = new(TileKind.Decoration, true, room.Id);
            foreach (var p in candidates.Skip(decorations).Take(obstacles)) map[p.X, p.Y] = new(TileKind.Obstacle, false, room.Id);
        }
    }

    private static void AddOverworldObjects(MutableTile[,] map, GridPoint start, GridPoint exit, SemanticBlueprint blueprint, Random random)
    {
        var candidates = Enumerable.Range(0, map.GetLength(0))
            .SelectMany(x => Enumerable.Range(0, map.GetLength(1)).Select(y => new GridPoint(x, y)))
            .Where(point => map[point.X, point.Y].RegionId is not null)
            .Where(point => Math.Abs(point.X - start.X) + Math.Abs(point.Y - start.Y) > 4)
            .Where(point => Math.Abs(point.X - exit.X) + Math.Abs(point.Y - exit.Y) > 4)
            .OrderBy(_ => random.Next())
            .ToList();

        // Outdoor maps should read as terrain first. Decorations only add a light accent and
        // obstacles remain rare enough that the continuous surface stays easy to traverse.
        var decorationRate = Math.Clamp(blueprint.DecorationDensity * .22, .008, .035);
        var obstacleRate = Math.Clamp(blueprint.ObstacleDensity * .16, 0, .012);
        var decorationCount = (int)(candidates.Count * decorationRate);
        var obstacleCount = (int)(candidates.Count * obstacleRate);
        foreach (var point in candidates.Take(decorationCount))
            map[point.X, point.Y] = new(TileKind.Decoration, true, map[point.X, point.Y].RegionId);
        foreach (var point in candidates.Skip(decorationCount).Take(obstacleCount))
            map[point.X, point.Y] = new(TileKind.Obstacle, false, map[point.X, point.Y].RegionId);
    }

    private static AssetRole RoleFor(TileKind kind) => kind switch { TileKind.Floor => AssetRole.Floor, TileKind.Wall => AssetRole.Wall, TileKind.Door => AssetRole.Door, TileKind.Obstacle => AssetRole.Obstacle, TileKind.Decoration => AssetRole.Decoration, TileKind.Start => AssetRole.StartMarker, TileKind.Exit => AssetRole.ExitMarker, _ => AssetRole.Unknown };
    private static AssetRole OutdoorRoleFor(IReadOnlyDictionary<AssetRole, AssetDefinition[]> assets, bool path, IReadOnlyList<string> tags)
    {
        var normalized = tags.Select(tag => tag.ToLowerInvariant()).ToHashSet();
        var preferences = path ? new[] { AssetRole.Path, AssetRole.Road, AssetRole.Grass, AssetRole.Sand, AssetRole.Floor }
            : normalized.Contains("sand") || normalized.Contains("beach") ? new[] { AssetRole.Sand, AssetRole.Grass, AssetRole.Floor, AssetRole.Path }
            : normalized.Contains("road") || normalized.Contains("plaza") ? new[] { AssetRole.Road, AssetRole.Path, AssetRole.Grass, AssetRole.Floor }
            : new[] { AssetRole.Grass, AssetRole.Sand, AssetRole.Floor, AssetRole.Path };
        return preferences.FirstOrDefault(role => assets.TryGetValue(role, out var choices) && choices.Length > 0, AssetRole.Floor);
    }
    private static AssetRole OverworldGroundRole(IReadOnlyDictionary<AssetRole, AssetDefinition[]> assets) => assets.TryGetValue(AssetRole.Grass, out var grass) && grass.Length > 0 ? AssetRole.Grass : AssetRole.Floor;
    private static string? PickAsset(Dictionary<AssetRole, AssetDefinition[]> assets, AssetRole role, int x, int y, int seed, IReadOnlyList<string>? desiredTags = null)
    {
        if (!assets.TryGetValue(role, out var choices) || choices.Length == 0)
        {
            if (role is AssetRole.StartMarker or AssetRole.ExitMarker && assets.TryGetValue(AssetRole.Floor, out var floors) && floors.Length > 0) choices = floors;
            else return null;
        }
        if (desiredTags is { Count: > 0 })
        {
            var hints = desiredTags.Select(value => value.ToLowerInvariant()).ToHashSet();
            var scored = choices.Select(asset => (Asset: asset, Score: asset.Tags.Count(tag => hints.Contains(tag.ToLowerInvariant())) + hints.Count(hint => asset.Category.Contains(hint, StringComparison.OrdinalIgnoreCase)) + (asset.ClassificationSource == "Bundled sprite pack" ? 2 : 0))).ToList();
            var best = scored.Max(item => item.Score);
            choices = scored.Where(item => item.Score == best).Select(item => item.Asset).OrderBy(asset => asset.Id, StringComparer.Ordinal).ToArray();
        }
        var value = unchecked((uint)(seed * 397 ^ x * 73856093 ^ y * 19349663));
        return choices[value % (uint)choices.Length].Id;
    }
    private static string? PickOverworldObjectAsset(Dictionary<AssetRole, AssetDefinition[]> assets, TileKind kind, int x, int y, int seed, IReadOnlyList<string> desiredTags)
    {
        var value = unchecked((uint)(seed * 397 ^ x * 73856093 ^ y * 19349663));
        var wantsBuilding = kind == TileKind.Decoration && value % 23 == 0 && assets.ContainsKey(AssetRole.Building);
        var role = wantsBuilding ? AssetRole.Building : kind == TileKind.Obstacle && assets.ContainsKey(AssetRole.Obstacle) ? AssetRole.Obstacle : AssetRole.Decoration;
        return PickAsset(assets, role, x, y, seed, desiredTags.Concat(["overworld", "outdoor"]).ToArray());
    }
    private static GridPoint Center(PlacedRegion room) => new(room.X + room.Width / 2, room.Y + room.Height / 2);
    private static bool Inside(MutableTile[,] map, int x, int y) => x >= 0 && y >= 0 && x < map.GetLength(0) && y < map.GetLength(1);
    private static IEnumerable<GridPoint> Neighbors(int x, int y) { yield return new(x - 1, y); yield return new(x + 1, y); yield return new(x, y - 1); yield return new(x, y + 1); }
    private sealed record MutableTile(TileKind Kind, bool Walkable, string? RegionId);
}
