using Sprite2World.Domain;

namespace Sprite2World.Application;

public sealed class WorldRepairService(IWorldValidator validator) : IWorldRepairService
{
    public WorldGenerationResult Repair(WorldDefinition world, IReadOnlyList<AssetDefinition> assets, int maximumAttempts = 3)
    {
        var validation = validator.Validate(world, assets);
        if (validation.IsValid) return new(world, validation);
        var tiles = world.Tiles.ToList();
        var repairs = world.Repairs.ToList();
        for (var attempt = 0; attempt < Math.Clamp(maximumAttempts, 1, 10) && !validation.IsValid; attempt++)
        {
            var blocked = tiles.Where(t => t.Kind == TileKind.Obstacle).OrderBy(t => Math.Abs(t.X - world.Start.X) + Math.Abs(t.Y - world.Start.Y)).ToList();
            if (blocked.Count == 0) break;
            var remove = blocked[0];
            tiles[tiles.IndexOf(remove)] = remove with { Kind = TileKind.Floor, Walkable = true };
            foreach (var layer in world.Layers.Where(layer => layer.Purpose == LayerPurpose.Decoration)) layer.Placements.RemoveAll(item => item.X == remove.X && item.Y == remove.Y && assets.FirstOrDefault(asset => asset.Id == item.AssetId)?.Role == AssetRole.Obstacle);
            repairs.Add($"Removed blocking obstacle at ({remove.X}, {remove.Y}).");
            world = world with { Tiles = tiles.ToList(), Repairs = repairs.ToList() };
            validation = validator.Validate(world, assets);
        }
        return new(world, validation);
    }
}
