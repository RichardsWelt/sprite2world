using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Sprite2World.Contracts;
using Sprite2World.Domain;

namespace Sprite2World.Infrastructure;

public sealed class SafeAssetImporter(IOptions<StorageOptions> options)
{
    private readonly StorageOptions _options = options.Value;
    private readonly SemaphoreSlim _libraryLock = new(1, 1);
    private string LibraryRoot => Path.Combine(_options.DataPath, "library");
    private string LibraryAssetRoot => Path.Combine(LibraryRoot, "assets");
    private string LibraryManifestPath => Path.Combine(LibraryRoot, "manifest.json");
    private string LibraryFoldersPath => Path.Combine(LibraryRoot, "folders.json");

    public async Task<ImportAssetsResponse> ImportAsync(ImportAssetsRequest request, CancellationToken cancellationToken = default)
    {
        var root = LibraryRoot;
        var assetRoot = LibraryAssetRoot;
        Directory.CreateDirectory(assetRoot);
        var candidates = new List<(string Path, byte[] Data)>();
        long total = 0;
        foreach (var file in request.Files)
        {
            byte[] data;
            try { data = Convert.FromBase64String(file.Base64); }
            catch (FormatException) { throw new InvalidDataException($"{file.Name}: invalid upload encoding."); }
            if (data.LongLength > _options.MaxFileBytes) throw new InvalidDataException($"{file.Name}: file exceeds the {_options.MaxFileBytes / 1024 / 1024} MB limit.");
            total += data.LongLength;
            if (total > _options.MaxTotalBytes) throw new InvalidDataException("The upload exceeds the total extraction limit.");
            if (Path.GetExtension(file.Name).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                ExtractZip(data, candidates, ref total);
            else if (Path.GetExtension(file.Name).Equals(".png", StringComparison.OrdinalIgnoreCase))
                candidates.Add((NormalizeRelative(file.Name), data));
            else throw new InvalidDataException($"{file.Name}: only PNG and ZIP files are supported.");
        }
        if (candidates.Count > _options.MaxAssets) throw new InvalidDataException($"Asset pack contains more than {_options.MaxAssets} PNG files.");
        var duplicate = candidates.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Conflicting duplicate path: {duplicate.Key}");

        var assets = new List<AssetDefinition>();
        foreach (var candidate in candidates.OrderBy(x => x.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (width, height) = PngCodec.ReadDimensions(candidate.Data);
            var hash = Convert.ToHexStringLower(SHA256.HashData(candidate.Data));
            var relative = NormalizeRelative(candidate.Path);
            var destination = SafeDestination(assetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, candidate.Data, cancellationToken);
            var id = $"asset-{hash[..12]}";
            var role = GuessRole(relative);
            assets.Add(new()
            {
                Id = id, FileName = Path.GetFileName(relative), RelativePath = relative,
                Url = $"/data/library/assets/{relative.Replace('\\', '/')}", ContentHash = hash,
                Width = width, Height = height, Role = role, Confidence = role == AssetRole.Unknown ? 0 : .55,
                ClassificationSource = role == AssetRole.Unknown ? "Unclassified" : "Folder hint"
            });
        }
        await UpsertLibraryAsync(assets, cancellationToken);
        return new(assets, []);
    }

    public async Task<List<AssetDefinition>> LoadLibraryAsync(CancellationToken cancellationToken = default)
    {
        await _libraryLock.WaitAsync(cancellationToken);
        try { return await ReadLibraryUnsafeAsync(cancellationToken); }
        finally { _libraryLock.Release(); }
    }

    public async Task UpsertLibraryAsync(IEnumerable<AssetDefinition> assets, CancellationToken cancellationToken = default)
    {
        var shared = assets.Where(asset => asset.Url.StartsWith("/data/library/", StringComparison.Ordinal)).ToList();
        if (shared.Count == 0) return;
        await _libraryLock.WaitAsync(cancellationToken);
        try
        {
            var merged = (await ReadLibraryUnsafeAsync(cancellationToken)).Concat(shared)
                .GroupBy(asset => asset.Id, StringComparer.Ordinal).Select(group => group.Last())
                .OrderBy(asset => asset.Category, StringComparer.OrdinalIgnoreCase).ThenBy(asset => asset.FileName, StringComparer.OrdinalIgnoreCase).ToList();
            await WriteLibraryUnsafeAsync(merged, cancellationToken);
        }
        finally { _libraryLock.Release(); }
    }

    public async Task<List<AssetFolderDefinition>> LoadLibraryFoldersAsync(CancellationToken cancellationToken = default)
    {
        await _libraryLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(LibraryFoldersPath)) return [];
            return JsonSerializer.Deserialize<List<AssetFolderDefinition>>(await File.ReadAllTextAsync(LibraryFoldersPath, cancellationToken), JsonOptions.Indented) ?? [];
        }
        catch (JsonException) { return []; }
        finally { _libraryLock.Release(); }
    }

    public async Task SaveLibraryFoldersAsync(IEnumerable<AssetFolderDefinition> folders, CancellationToken cancellationToken = default)
    {
        await _libraryLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(LibraryRoot);
            var unique = folders.GroupBy(folder => $"{folder.Theme}\u001f{folder.Subfolder}", StringComparer.OrdinalIgnoreCase).Select(group => group.First())
                .OrderBy(folder => folder.Theme, StringComparer.OrdinalIgnoreCase).ThenBy(folder => folder.Subfolder, StringComparer.OrdinalIgnoreCase).ToList();
            await File.WriteAllTextAsync(LibraryFoldersPath, JsonSerializer.Serialize(unique, JsonOptions.Indented), cancellationToken);
        }
        finally { _libraryLock.Release(); }
    }

    public async Task EnsureBundledAssetsAsync(CancellationToken cancellationToken = default)
    {
        var definitions = new[]
        {
            new BundledAsset("overworld/terrain/grass-floor.png", AssetRole.Grass, "Overworld/Terrain", ["overworld", "outdoor", "grass", "nature", "floor", "tileable"]),
            new BundledAsset("overworld/flowers/flowers-1.png", AssetRole.Decoration, "Overworld/Flowers", ["overworld", "outdoor", "flowers", "nature", "small"]),
            new BundledAsset("overworld/flowers/flowers-2.png", AssetRole.Decoration, "Overworld/Flowers", ["overworld", "outdoor", "flowers", "nature", "small"]),
            new BundledAsset("overworld/trees/tree-1.png", AssetRole.Decoration, "Overworld/Trees", ["overworld", "outdoor", "tree", "forest", "nature", "large"]),
            new BundledAsset("overworld/trees/tree-2.png", AssetRole.Decoration, "Overworld/Trees", ["overworld", "outdoor", "tree", "forest", "nature", "large"]),
            new BundledAsset("overworld/buildings/house.png", AssetRole.Building, "Overworld/Buildings", ["overworld", "outdoor", "house", "village", "landmark", "large"]),
            new BundledAsset("architecture/pillars/pillar.png", AssetRole.Decoration, "Architecture/Pillars", ["architecture", "pillar", "ruins", "stone", "large"])
        };
        var files = new List<UploadedFileDto>();
        foreach (var definition in definitions)
        {
            var source = Path.Combine(AppContext.BaseDirectory, "SeedAssets", "overworld", Path.GetFileName(definition.RelativePath));
            if (!File.Exists(source)) throw new FileNotFoundException($"Bundled starter sprite is missing: {source}");
            files.Add(new(definition.RelativePath, Convert.ToBase64String(await File.ReadAllBytesAsync(source, cancellationToken))));
        }
        var imported = await ImportAsync(new("shared-library", files), cancellationToken);
        foreach (var asset in imported.Assets)
        {
            var definition = definitions.First(item => item.RelativePath == asset.RelativePath);
            asset.Role = definition.Role; asset.Category = definition.Category; asset.Tags.Clear(); asset.Tags.AddRange(definition.Tags);
            asset.Confidence = 1; asset.ClassificationSource = "Bundled sprite pack"; asset.ManualOverride = true;
        }
        await UpsertLibraryAsync(imported.Assets, cancellationToken);
    }

    public async Task<ImportAssetsResponse> CreateDemoAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var roles = new[]
        {
            ("overworld/terrain/grass.png", AssetRole.Grass, "Overworld / Terrain", new[] { "outdoor", "grass", "nature" }),
            ("overworld/terrain/grass-flowers.png", AssetRole.Grass, "Overworld / Terrain", new[] { "outdoor", "grass", "flowers" }),
            ("overworld/terrain/dirt-path.png", AssetRole.Path, "Overworld / Wege", new[] { "outdoor", "path", "dirt" }),
            ("overworld/terrain/cobble-road.png", AssetRole.Road, "Overworld / Wege", new[] { "outdoor", "road", "stone" }),
            ("overworld/terrain/sand.png", AssetRole.Sand, "Overworld / Terrain", new[] { "outdoor", "sand", "beach" }),
            ("overworld/decor/shrub.png", AssetRole.Decoration, "Overworld / Dekoration", new[] { "outdoor", "shrub", "nature" }),
            ("overworld/decor/tree.png", AssetRole.Decoration, "Overworld / Dekoration", new[] { "outdoor", "tree", "nature" }),
            ("overworld/obstacles/rock.png", AssetRole.Obstacle, "Overworld / Hindernisse", new[] { "outdoor", "rock", "stone" }),
            ("dungeon/floors/stone-floor.png", AssetRole.Floor, "Dungeon / Terrain", new[] { "dungeon", "floor", "stone" }),
            ("dungeon/walls/obsidian-wall.png", AssetRole.Wall, "Dungeon / Terrain", new[] { "dungeon", "wall", "stone" }),
            ("dungeon/doors/oak-door.png", AssetRole.Door, "Dungeon / Türen", new[] { "dungeon", "door", "wood" }),
            ("dungeon/props/crate.png", AssetRole.Obstacle, "Dungeon / Objekte", new[] { "dungeon", "crate", "wood" }),
            ("dungeon/decor/torch.png", AssetRole.Decoration, "Dungeon / Dekoration", new[] { "dungeon", "torch", "light" }),
            ("markers/start.png", AssetRole.StartMarker, "Marker", new[] { "start" }),
            ("markers/exit.png", AssetRole.ExitMarker, "Marker", new[] { "exit" })
        };
        var files = roles.Select(item =>
        {
            var png = PngCodec.EncodeRgba(48, 48, (x, y) => DemoPixel(item.Item1, x / 3, y / 3));
            return new UploadedFileDto(item.Item1, Convert.ToBase64String(png));
        }).ToList();
        var response = await ImportAsync(new(projectId, files), cancellationToken);
        foreach (var asset in response.Assets)
        {
            var definition = roles.First(x => asset.RelativePath == x.Item1);
            asset.Role = definition.Item2;
            asset.Category = definition.Item3;
            asset.Tags.AddRange(definition.Item4);
            asset.Confidence = 1; asset.ClassificationSource = "Demo manifest";
        }
        await UpsertLibraryAsync(response.Assets, cancellationToken);
        return response;
    }

    private static (byte R, byte G, byte B, byte A) DemoPixel(string path, int x, int y)
    {
        var noise = ((x * 13 + y * 7 + x * y) % 9) - 4;
        if (path.Contains("grass", StringComparison.Ordinal))
        {
            var flower = path.Contains("flowers", StringComparison.Ordinal) && ((x == 4 && y == 5) || (x == 12 && y == 10));
            if (flower) return (236, 207, 92, 255);
            var tuft = (x + y * 3) % 17 == 0;
            return tuft ? Opaque(43, 105, 48) : Tint(69, 132, 62, noise * 2);
        }
        if (path.Contains("dirt-path", StringComparison.Ordinal))
            return (x + y * 2) % 13 == 0 ? Opaque(151, 112, 68) : Tint(118, 83, 51, noise * 2);
        if (path.Contains("cobble-road", StringComparison.Ordinal))
        {
            var mortar = y % 5 == 0 || (x + (y / 5 % 2) * 3) % 7 == 0;
            return mortar ? Opaque(57, 61, 67) : Tint(91, 96, 103, noise);
        }
        if (path.Contains("sand", StringComparison.Ordinal))
            return (x * 5 + y * 3) % 19 == 0 ? Opaque(207, 180, 119) : Tint(188, 158, 99, noise);
        if (path.Contains("shrub", StringComparison.Ordinal))
        {
            var inside = ((x - 8) * (x - 8) + (y - 9) * (y - 9) < 35) || ((x - 5) * (x - 5) + (y - 10) * (y - 10) < 16);
            return inside ? Opaque(y < 8 ? (byte)65 : (byte)45, y < 8 ? (byte)139 : (byte)103, 52) : Transparent;
        }
        if (path.Contains("tree", StringComparison.Ordinal))
        {
            if (y >= 10 && x is >= 7 and <= 9) return (104, 66, 39, 255);
            var crown = (x - 8) * (x - 8) + (y - 6) * (y - 6) <= 28;
            return crown ? Opaque(y < 5 ? (byte)65 : (byte)43, y < 5 ? (byte)139 : (byte)107, 50) : Transparent;
        }
        if (path.Contains("rock", StringComparison.Ordinal))
        {
            var inside = y is >= 6 and <= 12 && x >= 3 + Math.Abs(9 - y) / 2 && x <= 13 - Math.Abs(9 - y) / 2;
            return inside ? Tint(91, 96, 105, y < 8 ? 22 : -10) : Transparent;
        }

        var (r, g, b) = path.Contains("wall", StringComparison.Ordinal) ? (32, 35, 46)
            : path.Contains("door", StringComparison.Ordinal) ? (125, 77, 43)
            : path.Contains("crate", StringComparison.Ordinal) ? (132, 82, 43)
            : path.Contains("torch", StringComparison.Ordinal) ? (232, 142, 39)
            : path.Contains("start", StringComparison.Ordinal) ? (57, 194, 112)
            : path.Contains("exit", StringComparison.Ordinal) ? (218, 66, 89)
            : (68, 71, 82);
        var edge = x is 0 or 15 || y is 0 or 15;
        var checker = (x / 4 + y / 4) % 2 == 0;
        return Tint((byte)r, (byte)g, (byte)b, edge ? -28 : checker ? 6 : -8);
    }

    private static (byte R, byte G, byte B, byte A) Tint(byte r, byte g, byte b, int delta) =>
        ((byte)Math.Clamp(r + delta, 0, 255), (byte)Math.Clamp(g + delta, 0, 255), (byte)Math.Clamp(b + delta, 0, 255), 255);
    private static (byte R, byte G, byte B, byte A) Opaque(byte r, byte g, byte b) => (r, g, b, 255);
    private static readonly (byte R, byte G, byte B, byte A) Transparent = (0, 0, 0, 0);

    public async Task RemoveAsync(RemoveAssetsRequest request, CancellationToken cancellationToken = default)
    {
        var assetRoot = LibraryAssetRoot;
        foreach (var relativePath in request.RelativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = SafeDestination(assetRoot, NormalizeRelative(relativePath));
            if (File.Exists(destination)) File.Delete(destination);
        }
        await _libraryLock.WaitAsync(cancellationToken);
        try
        {
            var removed = request.RelativePaths.Select(NormalizeRelative).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var remaining = (await ReadLibraryUnsafeAsync(cancellationToken)).Where(asset => !removed.Contains(asset.RelativePath)).ToList();
            await WriteLibraryUnsafeAsync(remaining, cancellationToken);
        }
        finally { _libraryLock.Release(); }
    }

    public async Task<SaveAssetImageResponse> SaveImageAsync(SaveAssetImageRequest request, CancellationToken cancellationToken = default)
    {
        var projectId = SafeSegment(request.ProjectId);
        var relative = NormalizeRelative(request.RelativePath);
        byte[] data;
        try { data = Convert.FromBase64String(request.Base64); }
        catch (FormatException) { throw new InvalidDataException("Invalid image encoding."); }
        if (data.LongLength > _options.MaxFileBytes) throw new InvalidDataException("Image exceeds the configured file-size limit.");
        var (width, height) = PngCodec.ReadDimensions(data);
        var hash = Convert.ToHexStringLower(SHA256.HashData(data));
        var (folder, suffix) = request.Channel switch
        {
            MaterialChannel.Base => ("assets", ""),
            MaterialChannel.Normal => ("material-maps", ".normal"),
            MaterialChannel.Metallic => ("material-maps", ".metallic"),
            MaterialChannel.Roughness => ("material-maps", ".roughness"),
            _ => throw new InvalidDataException("Lit preview is rendered and cannot be stored as a material channel.")
        };
        var storedRelative = request.Channel == MaterialChannel.Base
            ? relative
            : Path.ChangeExtension(relative, $"{suffix}.png").Replace('\\', '/');
        var root = request.Shared ? Path.Combine(LibraryRoot, folder) : Path.Combine(_options.DataPath, "projects", projectId, folder);
        var destination = SafeDestination(root, storedRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination, data, cancellationToken);
        var url = request.Shared ? $"/data/library/{folder}/{storedRelative}?v={hash[..12]}" : $"/data/projects/{projectId}/{folder}/{storedRelative}?v={hash[..12]}";
        return new(url, hash, width, height);
    }

    private async Task<List<AssetDefinition>> ReadLibraryUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(LibraryManifestPath)) return [];
        try { return JsonSerializer.Deserialize<List<AssetDefinition>>(await File.ReadAllTextAsync(LibraryManifestPath, cancellationToken), JsonOptions.Indented) ?? []; }
        catch (JsonException) { return []; }
    }

    private async Task WriteLibraryUnsafeAsync(IReadOnlyList<AssetDefinition> assets, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(LibraryRoot);
        await File.WriteAllTextAsync(LibraryManifestPath, JsonSerializer.Serialize(assets, JsonOptions.Indented), cancellationToken);
        List<AssetFolderDefinition> existingFolders = [];
        if (File.Exists(LibraryFoldersPath))
        {
            try { existingFolders = JsonSerializer.Deserialize<List<AssetFolderDefinition>>(await File.ReadAllTextAsync(LibraryFoldersPath, cancellationToken), JsonOptions.Indented) ?? []; }
            catch (JsonException) { }
        }
        var derivedFolders = assets.Select(asset => (asset.Category ?? "General/Sprites").Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(parts => new AssetFolderDefinition(parts.FirstOrDefault() ?? "General", parts.Length > 1 ? parts[1] : "Sprites"));
        var folders = existingFolders.Concat(derivedFolders).GroupBy(folder => $"{folder.Theme}\u001f{folder.Subfolder}", StringComparer.OrdinalIgnoreCase).Select(group => group.First())
            .OrderBy(folder => folder.Theme, StringComparer.OrdinalIgnoreCase).ThenBy(folder => folder.Subfolder, StringComparer.OrdinalIgnoreCase).ToList();
        await File.WriteAllTextAsync(LibraryFoldersPath, JsonSerializer.Serialize(folders, JsonOptions.Indented), cancellationToken);
        await CreateContactSheetAsync(LibraryRoot, assets, cancellationToken);
    }

    private void ExtractZip(byte[] data, List<(string Path, byte[] Data)> target, ref long total)
    {
        using var stream = new MemoryStream(data); using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
        {
            if (!Path.GetExtension(entry.Name).Equals(".png", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Length > _options.MaxFileBytes) throw new InvalidDataException($"{entry.FullName}: extracted file is too large.");
            total += entry.Length;
            if (total > _options.MaxTotalBytes) throw new InvalidDataException("ZIP extraction exceeds the total limit.");
            var path = NormalizeRelative(entry.FullName);
            using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output);
            target.Add((path, output.ToArray()));
        }
    }

    private static string NormalizeRelative(string input)
    {
        var normalized = input.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(input) || normalized.Split('/').Any(p => p is ".." or "." or "")) throw new InvalidDataException("Dangerous or invalid asset path.");
        return string.Join('/', normalized.Split('/').Select(SafeSegment));
    }
    private static string SafeSegment(string input)
    {
        var safe = new string(input.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..") throw new InvalidDataException("Invalid file name.");
        return safe.Length > 100 ? safe[..100] : safe;
    }
    private static string SafeDestination(string root, string relative)
    {
        var destination = Path.GetFullPath(Path.Combine(root, relative));
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(fullRoot, StringComparison.Ordinal)) throw new InvalidDataException("ZIP path traversal was rejected.");
        return destination;
    }
    private static AssetRole GuessRole(string path)
    {
        var value = path.ToLowerInvariant();
        if (value.Contains("floor")) return AssetRole.Floor;
        if (value.Contains("wall")) return AssetRole.Wall;
        if (value.Contains("door")) return AssetRole.Door;
        if (value.Contains("obstacle") || value.Contains("crate") || value.Contains("barrel")) return AssetRole.Obstacle;
        if (value.Contains("decor") || value.Contains("torch")) return AssetRole.Decoration;
        if (value.Contains("start")) return AssetRole.StartMarker;
        if (value.Contains("exit")) return AssetRole.ExitMarker;
        if (value.Contains("water")) return AssetRole.Water;
        if (value.Contains("grass")) return AssetRole.Grass;
        if (value.Contains("path")) return AssetRole.Path;
        if (value.Contains("road")) return AssetRole.Road;
        if (value.Contains("sand")) return AssetRole.Sand;
        if (value.Contains("building")) return AssetRole.Building;
        return AssetRole.Unknown;
    }
    private static async Task CreateContactSheetAsync(string root, IReadOnlyList<AssetDefinition> assets, CancellationToken cancellationToken)
    {
        var columns = 8; var cell = 32; var rows = Math.Max(1, (int)Math.Ceiling(assets.Count / (double)columns));
        var png = PngCodec.EncodeRgba(columns * cell, rows * cell, (x, y) =>
        {
            var index = (y / cell) * columns + x / cell;
            if (index >= assets.Count) return ((byte)12, (byte)14, (byte)20, (byte)255);
            var hash = assets[index].ContentHash;
            var edge = x % cell < 2 || y % cell < 2;
            return edge ? ((byte)124, (byte)92, (byte)255, (byte)255) : (Convert.ToByte(hash[..2], 16), Convert.ToByte(hash.Substring(2, 2), 16), Convert.ToByte(hash.Substring(4, 2), 16), (byte)255);
        });
        await File.WriteAllBytesAsync(Path.Combine(root, "contact-sheet.png"), png, cancellationToken);
        var mapping = assets.Select((a, i) => new { shortId = $"A{i + 1:000}", a.Id, a.FileName, a.RelativePath });
        await File.WriteAllTextAsync(Path.Combine(root, "contact-sheet-map.json"), JsonSerializer.Serialize(mapping, JsonOptions.Indented), cancellationToken);
    }
    private sealed record BundledAsset(string RelativePath, AssetRole Role, string Category, string[] Tags);
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
}
