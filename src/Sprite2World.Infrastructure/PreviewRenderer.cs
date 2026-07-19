using Sprite2World.Domain;

namespace Sprite2World.Infrastructure;

public sealed class PreviewRenderer
{
    public byte[] Render(WorldDefinition world, int scale, IReadOnlyList<AssetDefinition>? assets = null)
    {
        scale = Math.Clamp(scale, 2, 16);
        var roles = (assets ?? []).GroupBy(asset => asset.Id).ToDictionary(group => group.Key, group => group.First().Role);
        return PngCodec.EncodeRgba(world.Width * scale, world.Height * scale, (x, y) =>
        {
            var tile = world.Tiles[y / scale * world.Width + x / scale];
            var grid = x % scale == 0 || y % scale == 0;
            if (grid) return ((byte)12, (byte)14, (byte)21, (byte)255);
            if (tile.AssetId is not null && roles.TryGetValue(tile.AssetId, out var role))
            {
                var assetColor = role switch
                {
                    AssetRole.Grass => (58, 116, 62, 255), AssetRole.Path => (128, 93, 58, 255), AssetRole.Road => (91, 96, 103, 255),
                    AssetRole.Sand => (188, 158, 99, 255), AssetRole.Water => (43, 105, 151, 255), AssetRole.Building => (125, 92, 70, 255),
                    _ => ((int R, int G, int B, int A)?)null
                };
                if (assetColor is { } color) return ((byte)color.R, (byte)color.G, (byte)color.B, (byte)color.A);
            }
            return tile.Kind switch
            {
                TileKind.Void => (7, 9, 14, 255),
                TileKind.Floor when world.EnvironmentType == "Overworld" => (58, 116, 62, 255),
                TileKind.Floor when world.EnvironmentType == "Interior" => (112, 91, 73, 255),
                TileKind.Floor => (54, 57, 69, 255),
                TileKind.Wall when world.EnvironmentType == "Interior" => (62, 43, 38, 255),
                TileKind.Wall => (24, 26, 36, 255),
                TileKind.Door => (139, 87, 50, 255), TileKind.Obstacle => (105, 65, 39, 255), TileKind.Decoration => (104, 75, 128, 255),
                TileKind.Start => (47, 196, 120, 255), TileKind.Exit => (230, 69, 87, 255), _ => (255, 0, 255, 255)
            };
        });
    }
}
