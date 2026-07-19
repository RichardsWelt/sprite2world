using Sprite2World.Application;
using Sprite2World.Contracts;
using Sprite2World.Domain;
using Sprite2World.Infrastructure;

namespace Sprite2World.Web.Services;

public sealed class EditorState(WorkerClient worker, IBlueprintService blueprints, IAssetClassificationService classification, ISpriteGenerationService spriteGeneration, IWorldExporter exporter, OpenAiCredentialState credentials, ILogger<EditorState> logger)
{
    private List<AssetDefinition> _sharedAssets = [];
    private List<AssetFolderDefinition> _sharedFolders = [];
    public event Action? Changed;
    public bool HasOpenProject { get; private set; }
    public string ProjectId { get; private set; } = $"project-{Guid.NewGuid():N}";
    public string ProjectName { get; set; } = "No project open";
    public string Prompt { get; set; } = DemoBlueprintFactory.DungeonPrompt;
    public string Feedback { get; set; } = "Needs more exploration and another loop";
    public string Model { get; set; } = OpenAiModelCatalog.DefaultModelId;
    public string ReasoningEffort { get; set; } = "medium";
    public string WorldEnvironment { get; private set; } = "Dungeon";
    public string Status { get; private set; } = "Ready";
    public string? Error { get; private set; }
    public bool IsBusy { get; private set; }
    public bool AiConfigured => credentials.IsReady;
    public List<AssetDefinition> Assets { get; private set; } = [];
    public List<AssetFolderDefinition> AssetFolders { get; private set; } = [];
    public SemanticBlueprint? Blueprint { get; private set; }
    public WorldDefinition? World { get; private set; }
    public ValidationResult? Validation { get; private set; }
    public List<WorldVersion> Versions { get; } = [];
    public int? CurrentVersionNumber { get; private set; }
    public WorkerStatus? WorkerStatus { get; private set; }
    public string? ActiveLayerId { get; private set; }

    public async Task InitializeAsync()
    {
        if (World is not null || IsBusy) return;
        await RunAsync("Loading world library…", async () =>
        {
            WorkerStatus = await worker.StatusAsync();
            _sharedAssets = await worker.ListSharedAssetsAsync();
            _sharedFolders = await worker.ListSharedFoldersAsync();
            HasOpenProject = false; Assets = _sharedAssets.ToList(); AssetFolders = _sharedFolders.ToList(); EnsureAssetFoldersFromAssets(); Blueprint = null; World = null; Validation = null; Versions.Clear(); CurrentVersionNumber = null;
            ProjectName = "No project open"; Status = "World library ready";
            await Task.CompletedTask;
        });
    }

    public async Task ImportAsync(IEnumerable<(string Name, byte[] Data)> files, int completed = 0, int total = 0)
    {
        await RunAsync("Importing and validating assets…", async () =>
        {
            var request = new ImportAssetsRequest(ProjectId, files.Select(x => new UploadedFileDto(x.Name, Convert.ToBase64String(x.Data))).ToList());
            var response = await worker.ImportAsync(request);
            Assets = Assets.Concat(response.Assets).GroupBy(a => a.Id).Select(g => g.First()).ToList();
            _sharedAssets = Assets.Where(IsSharedAsset).ToList();
            EnsureAssetFoldersFromAssets();
            Status = total > 0 ? $"Imported {Math.Min(completed + response.Assets.Count, total):N0} of {total:N0} files" : $"Imported {response.Assets.Count} assets";
        });
    }

    public async Task RemoveAssetsAsync(IReadOnlyCollection<string> assetIds)
    {
        var removed = Assets.Where(asset => assetIds.Contains(asset.Id)).ToList();
        if (removed.Count == 0) return;
        EnsureAssetFoldersFromAssets();
        await RunAsync($"Removing {removed.Count:N0} assets…", async () =>
        {
            await worker.RemoveAssetsAsync(new(ProjectId, removed.Select(asset => asset.RelativePath).ToList()));
            var removedIds = removed.Select(asset => asset.Id).ToHashSet(StringComparer.Ordinal);
            Assets = Assets.Where(asset => !removedIds.Contains(asset.Id)).ToList();
            if (World is not null) foreach (var layer in World.Layers) layer.Placements.RemoveAll(item => removedIds.Contains(item.AssetId));
            await SaveCoreAsync();
            Status = $"Removed {removed.Count:N0} assets";
        });
    }

    public async Task SaveSpriteImageAsync(AssetDefinition asset, string base64)
    {
        await RunAsync("Saving sprite…", async () =>
        {
            var saved = await worker.SaveAssetImageAsync(new(ProjectId, asset.Id, asset.RelativePath, base64, MaterialChannel.Base, IsSharedAsset(asset)));
            asset.Url = saved.Url; asset.ContentHash = saved.ContentHash; asset.Width = saved.Width; asset.Height = saved.Height;
            asset.NormalMapStatus = DerivedAssetStatus.Review; asset.NormalReviewed = false; asset.NormalMapSourceHash = null;
            await SaveCoreAsync(); Status = $"Saved sprite {asset.FileName}";
        });
    }

    public async Task<GeneratedSprite?> GenerateSpriteAsync(string prompt, int width, int height, SpriteGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        GeneratedSprite? generated = null;
        await RunAsync("OpenAI is drawing the sprite…", async () =>
        {
            generated = await spriteGeneration.GenerateSpriteAsync(prompt, width, height, Model, options ?? new(ReasoningEffort: ReasoningEffort), cancellationToken);
            Status = $"AI sprite ready: {generated.Name}";
        });
        return generated;
    }

    public async Task<AssetDefinition?> CreateSpriteAsync(string name, int width, int height, string base64, string theme = "Custom", string subfolder = "Sprites")
    {
        if (width is < 48 or > 384 || height is < 48 or > 384 || width % 48 != 0 || height % 48 != 0) throw new InvalidOperationException("Sprites must use 48×48 pixel tile units (up to 8×8 tiles).");
        AssetDefinition? created = null;
        await RunAsync("Creating sprite…", async () =>
        {
            var safeName = string.Concat((string.IsNullOrWhiteSpace(name) ? "untitled-sprite" : name.Trim()).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')).Trim('-');
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "untitled-sprite";
            var relative = $"custom/{safeName[..Math.Min(safeName.Length, 64)]}.png";
            var id = $"asset-{Guid.NewGuid():N}";
            var saved = await worker.SaveAssetImageAsync(new(ProjectId, id, relative, base64, MaterialChannel.Base, true));
            var normalizedTheme = NormalizeThemeFolderName(NormalizeFolderSegment(theme, "Custom"));
            var normalizedSubfolder = NormalizeFolderSegment(subfolder, "Sprites");
            created = new AssetDefinition
            {
                Id = id, FileName = Path.GetFileName(relative), RelativePath = relative, Url = saved.Url,
                ContentHash = saved.ContentHash, Width = saved.Width, Height = saved.Height, Role = AssetRole.Unknown,
                ClassificationSource = "Created in Sprite Studio", Category = $"{normalizedTheme}/{normalizedSubfolder}"
            };
            Assets.Add(created); AddAssetFolder(normalizedTheme, normalizedSubfolder); await SaveCoreAsync(); Status = $"Created {created.FileName} ({width}×{height})";
        });
        return created;
    }

    public async Task SaveSpriteAiMetadataAsync(AssetDefinition asset, GeneratedSprite generated)
    {
        var metadata = generated.Metadata;
        asset.AiMetadata = new(metadata.Prompt, metadata.Model, metadata.GeneratedAt, metadata.GeneratorVersion, metadata.View, metadata.Framing, metadata.Outline, metadata.Lighting, metadata.Palette.ToList(), metadata.Attempts, generated.Quality.Passed, generated.Quality.Issues.Select(issue => issue.Message).ToList());
        await SaveCoreAsync();
    }

    public async Task SaveNormalMapAsync(AssetDefinition asset, string base64, double strength, bool invertY, bool reviewed = false)
    {
        asset.NormalMapStatus = DerivedAssetStatus.Generating; Notify();
        await RunAsync("Generating normal map…", async () =>
        {
            var saved = await worker.SaveAssetImageAsync(new(ProjectId, asset.Id, asset.RelativePath, base64, MaterialChannel.Normal, IsSharedAsset(asset)));
            asset.NormalMapUrl = saved.Url; asset.NormalStrength = Math.Clamp(strength, .1, 5); asset.NormalInvertY = invertY;
            asset.NormalReviewed = reviewed; asset.NormalMapSourceHash = asset.ContentHash;
            asset.NormalMapStatus = reviewed ? DerivedAssetStatus.Ready : DerivedAssetStatus.Review;
            await SaveCoreAsync(); Status = $"Normal map ready: {asset.FileName}";
        });
        if (Error is not null) { asset.NormalMapStatus = DerivedAssetStatus.Failed; Notify(); }
    }

    public async Task SaveMaterialMapAsync(AssetDefinition asset, MaterialChannel channel, string base64, bool approve)
    {
        if (channel is not (MaterialChannel.Normal or MaterialChannel.Metallic or MaterialChannel.Roughness)) return;
        await RunAsync($"Saving {channel.ToString().ToLowerInvariant()} map…", async () =>
        {
            var saved = await worker.SaveAssetImageAsync(new(ProjectId, asset.Id, asset.RelativePath, base64, channel, IsSharedAsset(asset)));
            if (channel == MaterialChannel.Normal)
            {
                asset.NormalMapUrl = saved.Url; asset.NormalMapSourceHash = asset.ContentHash;
            }
            else if (channel == MaterialChannel.Metallic) asset.MetallicMapUrl = saved.Url;
            else asset.RoughnessMapUrl = saved.Url;
            var materialReady = approve && (channel == MaterialChannel.Normal || asset.NormalMapUrl is not null);
            asset.NormalReviewed = materialReady; asset.NormalMapStatus = materialReady ? DerivedAssetStatus.Ready : DerivedAssetStatus.Review;
            await SaveCoreAsync(); Status = approve ? $"Saved and approved {asset.FileName}" : $"Saved {channel} map";
        });
    }

    public async Task SaveMaterialSettingsAsync(AssetDefinition asset)
    {
        await SaveCoreAsync(); Status = $"Saved material settings for {asset.FileName}"; Notify();
    }

    public void SetNormalSettings(AssetDefinition asset, double strength, bool invertY)
    {
        asset.NormalStrength = Math.Clamp(strength, .1, 5); asset.NormalInvertY = invertY;
        if (asset.NormalMapUrl is not null) asset.NormalMapStatus = DerivedAssetStatus.Review;
        asset.NormalReviewed = false; Notify();
    }

    public void SetMaterialSettings(AssetDefinition asset, double metallic, double roughness, double lightIntensity)
    {
        asset.MetallicStrength = Math.Clamp(metallic, 0, 1); asset.RoughnessStrength = Math.Clamp(roughness, 0, 1); asset.LightIntensity = Math.Clamp(lightIntensity, .1, 3);
        if (asset.NormalMapStatus == DerivedAssetStatus.Ready) asset.NormalMapStatus = DerivedAssetStatus.Review;
        asset.NormalReviewed = false; Notify();
    }

    public void MarkNormalReviewed(AssetDefinition asset)
    {
        if (asset.NormalMapUrl is null) return;
        asset.NormalReviewed = true; asset.NormalMapStatus = DerivedAssetStatus.Ready; Status = $"Normal map approved: {asset.FileName}"; Notify();
    }

    public async Task FinalizeSpriteAsync(AssetDefinition asset)
    {
        if (asset.NormalMapUrl is null) return;
        asset.NormalReviewed = true; asset.NormalMapStatus = DerivedAssetStatus.Ready;
        await SaveCoreAsync(); Status = $"Sprite ready: {asset.FileName}"; Notify();
    }

    public async Task LoadDemoAsync()
    {
        await RunAsync("Creating original demo asset pack…", async () =>
        {
            var response = await worker.DemoAsync(ProjectId);
            Assets = Assets.Concat(response.Assets)
                .GroupBy(asset => asset.Id, StringComparer.Ordinal)
                .Select(group => group.Last())
                .OrderBy(asset => asset.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(asset => asset.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            EnsureAssetFoldersFromAssets();
            Status = "Starter sprites loaded";
            await SaveCoreAsync();
        });
    }

    public async Task ClassifyAsync()
    {
        await RunAsync("OpenAI is classifying the asset library…", async () =>
        {
            Assets = (await classification.ClassifyAsync(Assets, Model)).ToList(); Status = "AI classification complete"; await SaveCoreAsync();
        });
    }

    public async Task GenerateAsync(bool useAi = true)
    {
        await RunAsync(useAi ? "OpenAI is designing the semantic blueprint…" : "Building deterministic world…", async () =>
        {
            var source = Blueprint ?? DemoBlueprintFactory.CreateForEnvironment(WorldEnvironment);
            if (useAi)
            {
                try { source = await blueprints.CreateAsync(BuildWorldPrompt(), Model, ReasoningEffort); }
                catch (OpenAiException ex) { Error = $"AI fallback: {ex.Message}"; source = DemoBlueprintFactory.CreateForEnvironment(WorldEnvironment, source.Seed + Versions.Count); }
            }
            WorldEnvironment = NormalizeWorldEnvironment(source.EnvironmentType); Blueprint = source;
            var editorLayers = World is null ? [] : CloneLayers(World.Layers.Where(layer => !layer.Generated));
            var generated = await worker.GenerateAsync(new(source, Assets, source.Seed));
            World = generated.World with { Layers = MergeLayers(generated.World.Layers, editorLayers) }; Validation = generated.Validation; EnsureEditorLayer();
            Versions.Add(new(Versions.Count + 1, DateTimeOffset.Now, source, World with { Layers = CloneLayers(World.Layers) }, Validation, useAi && AiConfigured ? "OpenAI blueprint" : "Deterministic blueprint"));
            CurrentVersionNumber = Versions.Count;
            Status = $"World v{Versions.Count} generated · seed {World.Seed}";
            await SaveCoreAsync();
        });
    }

    public async Task ImproveAsync()
    {
        if (Blueprint is null) return;
        await RunAsync("Improving semantic blueprint from feedback…", async () =>
        {
            SemanticBlueprint improved;
            try { improved = await blueprints.ImproveAsync(BuildWorldPrompt(), Blueprint with { EnvironmentType = WorldEnvironment }, Validation, $"{Feedback}\n\nAvailable sprite library:\n{BuildAssetCatalogContext()}", Model, ReasoningEffort); }
            catch (OpenAiException ex) { Error = $"AI fallback: {ex.Message}"; improved = DemoBlueprintFactory.Improve(Blueprint, Feedback) with { EnvironmentType = WorldEnvironment }; }
            WorldEnvironment = NormalizeWorldEnvironment(improved.EnvironmentType); Blueprint = improved;
            var editorLayers = World is null ? [] : CloneLayers(World.Layers.Where(layer => !layer.Generated));
            var generated = await worker.GenerateAsync(new(improved, Assets, improved.Seed));
            World = generated.World with { Layers = MergeLayers(generated.World.Layers, editorLayers) }; Validation = generated.Validation; EnsureEditorLayer();
            Versions.Add(new(Versions.Count + 1, DateTimeOffset.Now, improved, World with { Layers = CloneLayers(World.Layers) }, Validation, AiConfigured ? "AI feedback" : "Local feedback"));
            CurrentVersionNumber = Versions.Count;
            Status = $"Improved world v{Versions.Count} ready"; await SaveCoreAsync();
        });
    }

    public async Task ValidateAsync()
    {
        if (World is null) return;
        await RunAsync("Running independent validation…", async () => { Validation = await worker.ValidateAsync(new(World, Assets)); Status = Validation.IsValid ? "All checks passed" : "Validation found issues"; });
    }
    public async Task SaveAsync()
    {
        if (!HasOpenProject) return;
        await RunAsync("Saving project…", async () => { await SaveCoreAsync(); Status = "Project saved"; });
    }
    public Task<List<ProjectSummary>> ListProjectsAsync() => worker.ListAsync();
    public async Task LoadProjectAsync(string id)
    {
        await RunAsync("Opening project…", async () =>
        {
            var project = await worker.LoadAsync(id) ?? throw new InvalidOperationException("The saved project no longer exists.");
            _sharedAssets = await worker.ListSharedAssetsAsync();
            _sharedFolders = await worker.ListSharedFoldersAsync();
            ProjectId = project.ProjectId; ProjectName = project.Name; Assets = project.Assets.Concat(_sharedAssets).GroupBy(asset => asset.Id, StringComparer.Ordinal).Select(group => group.Last()).ToList(); AssetFolders = (project.AssetFolders ?? []).Concat(_sharedFolders).Select(folder => new AssetFolderDefinition(NormalizeThemeFolderName(folder.Theme), NormalizeRoleFolderName(folder.Subfolder))).GroupBy(folder => $"{folder.Theme}\u001f{folder.Subfolder}", StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList(); EnsureAssetFoldersFromAssets(); Blueprint = project.Blueprint; World = project.World; Validation = project.Validation;
            if (!string.IsNullOrWhiteSpace(project.Prompt)) Prompt = project.Prompt;
            if (!string.IsNullOrWhiteSpace(project.Feedback)) Feedback = project.Feedback;
            if (!string.IsNullOrWhiteSpace(project.Model)) Model = project.Model;
            if (!string.IsNullOrWhiteSpace(project.ReasoningEffort)) ReasoningEffort = project.ReasoningEffort;
            WorldEnvironment = NormalizeWorldEnvironment(project.WorldEnvironment ?? project.Blueprint?.EnvironmentType);
            HasOpenProject = true; EnsureGeneratedTerrainUnderlays(); EnsureEditorLayer();
            Versions.Clear();
            CurrentVersionNumber = null;
            if (Blueprint is not null && World is not null && Validation is not null)
            {
                Versions.Add(new(1, DateTimeOffset.Now, Blueprint, World, Validation, "Saved project"));
                CurrentVersionNumber = 1;
            }
            Status = $"Opened {ProjectName}";
        });
    }
    public async Task<RenderPreviewResponse?> RenderPngAsync() => World is null ? null : await worker.RenderAsync(new(World, Assets));
    public string? ExportJson() => Blueprint is null || World is null || Validation is null ? null : exporter.ExportJson(new("1.0", ProjectId, ProjectName, DateTimeOffset.UtcNow, Assets, Blueprint, World, Validation));
    public void SetRole(AssetDefinition asset, AssetRole role) { asset.Role = role; asset.Confidence = 1; asset.ClassificationSource = "Manual"; asset.ManualOverride = true; Notify(); }
    public void SetExcluded(AssetDefinition asset, bool excluded) { asset.Excluded = excluded; asset.ManualOverride = true; Notify(); }
    public void SetCategory(IEnumerable<string> assetIds, string category)
    {
        var normalized = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim();
        normalized = normalized.Length > 90 ? normalized[..90] : normalized;
        var ids = assetIds.ToHashSet(StringComparer.Ordinal);
        foreach (var asset in Assets.Where(asset => ids.Contains(asset.Id))) asset.Category = normalized;
        Status = $"Category assigned: {normalized}"; Notify();
    }
    public void SetAssetFolder(IEnumerable<string> assetIds, string theme, string subfolder)
    {
        var normalizedTheme = NormalizeThemeFolderName(NormalizeFolderSegment(theme, "General"));
        var normalizedSubfolder = NormalizeFolderSegment(subfolder, "Sprites");
        SetCategory(assetIds, $"{normalizedTheme}/{normalizedSubfolder}");
    }
    public async Task CreateAssetFolderAsync(string theme, string? subfolder)
    {
        var normalizedTheme = NormalizeThemeFolderName(NormalizeFolderSegment(theme, "General"));
        var normalizedSubfolder = NormalizeFolderSegment(subfolder, "Sprites");
        AddAssetFolder(normalizedTheme, normalizedSubfolder);
        await SaveCoreAsync(); Status = $"Created folder: {normalizedTheme}/{normalizedSubfolder}"; Notify();
    }
    public async Task UpdateAssetLibraryEntryAsync(AssetDefinition asset, string? fileName, string? theme = null, string? subfolder = null)
    {
        AddAssetFolderForAsset(asset);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var extension = Path.GetExtension(asset.FileName);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".png";
            var stem = Path.GetFileNameWithoutExtension(fileName.Trim());
            var invalid = Path.GetInvalidFileNameChars().ToHashSet();
            stem = new string(stem.Where(character => !invalid.Contains(character)).ToArray()).Trim();
            if (string.IsNullOrWhiteSpace(stem)) stem = "sprite";
            asset.FileName = $"{stem[..Math.Min(stem.Length, 72)]}{extension.ToLowerInvariant()}";
        }
        if (theme is not null || subfolder is not null)
        {
            var current = (asset.Category ?? "").Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var normalizedTheme = NormalizeThemeFolderName(NormalizeFolderSegment(theme ?? current.FirstOrDefault() ?? "General", "General"));
            var normalizedSubfolder = NormalizeFolderSegment(subfolder ?? (current.Length > 1 ? current[1] : asset.Role.ToString()), "Sprites");
            asset.Category = $"{normalizedTheme}/{normalizedSubfolder}";
            AddAssetFolder(normalizedTheme, normalizedSubfolder);
        }
        await SaveCoreAsync(); Status = $"Updated sprite: {asset.FileName}"; Notify();
    }
    public void SetActiveLayer(string id) { EnsureEditorLayer(); if (World?.Layers.Any(layer => layer.Id == id) == true) { ActiveLayerId = id; Notify(); } }
    public void AddLayer()
    {
        if (World is null) return;
        var layer = new WorldLayer { Id = $"layer-{Guid.NewGuid():N}", Name = $"Layer {World.Layers.Count + 1}", Order = World.Layers.Count == 0 ? 0 : World.Layers.Max(item => item.Order) + 1, Purpose = LayerPurpose.Custom };
        World.Layers.Add(layer); ActiveLayerId = layer.Id; Status = $"Layer added: {layer.Name}"; Notify();
    }
    public void RenameLayer(string id, string name)
    {
        var layer = World?.Layers.FirstOrDefault(layer => layer.Id == id); if (layer is null) return;
        var normalized = string.IsNullOrWhiteSpace(name) ? "Layer" : name.Trim(); layer.Name = normalized.Length > 40 ? normalized[..40] : normalized; Notify();
    }
    public void ToggleLayerVisibility(string id) { var layer = World?.Layers.FirstOrDefault(layer => layer.Id == id); if (layer is not null) { layer.Visible = !layer.Visible; Notify(); } }
    public void ToggleLayerLock(string id) { var layer = World?.Layers.FirstOrDefault(layer => layer.Id == id); if (layer is not null) { layer.Locked = !layer.Locked; Notify(); } }
    public void MoveLayer(string id, int delta)
    {
        if (World is null) return;
        var ordered = World.Layers.OrderBy(layer => layer.Order).ToList();
        var index = ordered.FindIndex(layer => layer.Id == id);
        if (index < 0 || ordered[index].Locked) return;
        var target = index + delta;
        if (target < 0 || target >= ordered.Count || ordered[target].Locked) return;
        var moving = ordered[index];
        ordered.RemoveAt(index);
        ordered.Insert(target, moving);
        for (var order = 0; order < ordered.Count; order++) ordered[order].Order = order;
        Notify();
    }
    public void RemoveLayer(string id)
    {
        if (World is null) return; var layer = World.Layers.FirstOrDefault(layer => layer.Id == id); if (layer is null || layer.Generated) return;
        World.Layers.Remove(layer); var order = 0; foreach (var item in World.Layers.OrderBy(item => item.Order)) item.Order = order++; EnsureEditorLayer(); Status = $"Layer removed: {layer.Name}"; Notify();
    }
    public bool Paint(string assetId, int x, int y, double offsetX = 0, double offsetY = 0)
    {
        EnsureEditorLayer(); if (World is null) return false;
        var layer = World.Layers.FirstOrDefault(layer => layer.Id == ActiveLayerId); var asset = Assets.FirstOrDefault(asset => asset.Id == assetId); if (layer is null || layer.Locked || asset is null) return false;
        if (asset.Role is AssetRole.Floor or AssetRole.Wall) { offsetX = 0; offsetY = 0; }
        offsetX = Math.Clamp(offsetX, 0, .999999); offsetY = Math.Clamp(offsetY, 0, .999999);
        var existing = layer.Placements.FirstOrDefault(item => item.X == x && item.Y == y && Math.Abs(item.OffsetX - offsetX) < .0001 && Math.Abs(item.OffsetY - offsetY) < .0001);
        if (existing?.AssetId == assetId) return false; if (existing is not null) layer.Placements.Remove(existing);
        layer.Placements.Add(new($"placement-{Guid.NewGuid():N}", assetId, x, y, offsetX, offsetY)); Status = $"Drawing on {layer.Name}"; Notify(); return true;
    }
    public bool Erase(int x, int y, double? offsetX = null, double? offsetY = null)
    {
        EnsureEditorLayer(); var layer = World?.Layers.FirstOrDefault(layer => layer.Id == ActiveLayerId); if (layer is null || layer.Locked) return false;
        var removed = layer.Placements.RemoveAll(item => item.X == x && item.Y == y && (offsetX is null || Math.Abs(item.OffsetX - offsetX.Value) < .0001) && (offsetY is null || Math.Abs(item.OffsetY - offsetY.Value) < .0001)); if (removed == 0) return false; Status = $"Erased on {layer.Name}"; Notify(); return true;
    }
    public void ActivateLayerForAsset(string assetId)
    {
        EnsureEditorLayer(); if (World is null) return; var role = Assets.FirstOrDefault(asset => asset.Id == assetId)?.Role;
        var purpose = role is AssetRole.Floor or AssetRole.Wall or AssetRole.Door or AssetRole.Road or AssetRole.Path or AssetRole.Grass or AssetRole.Sand ? LayerPurpose.Terrain : LayerPurpose.Decoration;
        var layer = World.Layers.Where(layer => layer.Purpose == purpose).OrderBy(layer => layer.Order).FirstOrDefault(); if (layer is not null) ActiveLayerId = layer.Id; Notify();
    }
    public void RestoreVersion(int number)
    {
        var version = Versions.FirstOrDefault(v => v.Number == number); if (version is null) return;
        Blueprint = version.Blueprint; World = version.World with { Layers = CloneLayers(version.World.Layers) }; Validation = version.Validation; CurrentVersionNumber = number; EnsureEditorLayer(); Status = $"Restored v{number}"; Notify();
    }
    public void NewProject(string? name = null)
    {
        ProjectId = $"project-{Guid.NewGuid():N}";
        ProjectName = string.IsNullOrWhiteSpace(name) ? "Untitled World" : name.Trim();
        Prompt = DemoBlueprintFactory.DungeonPrompt;
        Feedback = "Needs more exploration and another loop";
        WorldEnvironment = "Dungeon";
        Assets = _sharedAssets.ToList(); AssetFolders = _sharedFolders.ToList(); EnsureAssetFoldersFromAssets(); Blueprint = null; World = null; Validation = null; Versions.Clear(); CurrentVersionNumber = null; Error = null; HasOpenProject = true; Status = "New project"; Notify();
    }
    public void CloseProject() { ProjectId = $"closed-{Guid.NewGuid():N}"; ProjectName = "No project open"; Assets = _sharedAssets.ToList(); AssetFolders = _sharedFolders.ToList(); EnsureAssetFoldersFromAssets(); Blueprint = null; World = null; Validation = null; Versions.Clear(); CurrentVersionNumber = null; Error = null; HasOpenProject = false; Status = "Project closed"; Notify(); }
    public async Task DeleteProjectAsync(string id)
    {
        await RunAsync("Deleting project…", async () => { await worker.DeleteProjectAsync(id); if (ProjectId == id) CloseProject(); Status = "Project deleted"; });
    }
    public void ClearError() { Error = null; Notify(); }
    public void SetWorldEnvironment(string value)
    {
        var previous = WorldEnvironment;
        WorldEnvironment = NormalizeWorldEnvironment(value);
        if (IsDefaultWorldPrompt(Prompt, previous)) Prompt = DefaultWorldPrompt(WorldEnvironment);
        Status = $"World type: {WorldEnvironment}"; Notify();
    }

    public Task AutoSaveAsync(CancellationToken cancellationToken = default) => HasOpenProject && !IsBusy
        ? worker.SaveAsync(CreateSaveRequest(), cancellationToken)
        : Task.CompletedTask;

    public Task SaveWorldPreviewAsync(string base64, CancellationToken cancellationToken = default) => HasOpenProject && World is not null
        ? worker.SavePreviewAsync(new(ProjectId, base64), cancellationToken)
        : Task.CompletedTask;

    private SaveProjectRequest CreateSaveRequest() => new(ProjectId, ProjectName, Assets, Blueprint, World, Validation, Prompt, Feedback, Model, ReasoningEffort, WorldEnvironment, AssetFolders);
    private string BuildWorldPrompt() => $"""
        Environment type: {WorldEnvironment}
        {WorldEnvironmentGuidance(WorldEnvironment)}

        User request:
        {Prompt.Trim()}

        Available sprite library:
        {BuildAssetCatalogContext()}
        """;
    private static string WorldEnvironmentGuidance(string value) => value switch
    {
        "Interior" => "Create 4-10 believable functional rooms inside one coherent building, joined by doors or hallways. Include circulation space and keep all rooms reachable.",
        "Overworld" => "Create one continuous outdoor ground surface covering the full map. Treat 5-10 zones only as semantic biomes or landmarks, never as rooms or separated islands. Keep decoration sparse and add readable paths or roads with at least one alternate route.",
        _ => "Create 5-10 enclosed dungeon rooms connected by corridors. Include at least one loop, useful branches and distinct room purposes."
    };
    private static string NormalizeWorldEnvironment(string? value) => value is "Interior" or "Overworld" ? value : "Dungeon";
    private static bool IsDefaultWorldPrompt(string prompt, string environment) => string.Equals(prompt.Trim(), DefaultWorldPrompt(environment), StringComparison.OrdinalIgnoreCase) || string.Equals(prompt.Trim(), DemoBlueprintFactory.DungeonPrompt, StringComparison.OrdinalIgnoreCase);
    private static string DefaultWorldPrompt(string environment) => environment switch
    {
        "Interior" => "Create a coherent building interior with a foyer, useful rooms, believable doors and hallways, two optional side rooms and a distinct rear exit.",
        "Overworld" => "Create one continuous outdoor ground surface with sparse decoration, natural biomes, a few landmarks, readable turning paths and an alternate route to the exit.",
        _ => DemoBlueprintFactory.DungeonPrompt
    };
    private string BuildAssetCatalogContext()
    {
        var lines = Assets.Where(asset => !asset.Excluded).GroupBy(asset => string.IsNullOrWhiteSpace(asset.Category) ? "General/Sprites" : asset.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"- {group.Key}: {string.Join(", ", group.GroupBy(asset => asset.Role).OrderBy(role => role.Key.ToString()).Select(role => $"{role.Key} ({role.Count()})"))}");
        return string.Join('\n', lines.Take(80));
    }
    private static string NormalizeFolderSegment(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().Replace('/', '-').Replace('\\', '-');
        return normalized[..Math.Min(normalized.Length, 40)];
    }
    private void EnsureAssetFoldersFromAssets() { foreach (var asset in Assets) AddAssetFolderForAsset(asset); }
    private void AddAssetFolderForAsset(AssetDefinition asset)
    {
        var parts = (asset.Category ?? "").Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        AddAssetFolder(parts.FirstOrDefault() ?? "General", parts.Length > 1 ? parts[1] : NormalizeRoleFolderName(asset.Role.ToString()));
    }
    private static string NormalizeRoleFolderName(string value) => value switch { "StartMarker" => "Start", "ExitMarker" => "Exit", _ => value };
    private static string NormalizeThemeFolderName(string value) => value.Equals("Allgemein", StringComparison.OrdinalIgnoreCase) ? "General" : value;
    private void AddAssetFolder(string theme, string subfolder)
    {
        var normalizedTheme = NormalizeThemeFolderName(NormalizeFolderSegment(theme, "General")); var normalizedSubfolder = NormalizeFolderSegment(subfolder, "Sprites");
        if (!AssetFolders.Any(folder => string.Equals(folder.Theme, normalizedTheme, StringComparison.OrdinalIgnoreCase) && string.Equals(folder.Subfolder, normalizedSubfolder, StringComparison.OrdinalIgnoreCase)))
            AssetFolders.Add(new(normalizedTheme, normalizedSubfolder));
    }
    private async Task SaveCoreAsync()
    {
        _sharedAssets = Assets.Where(IsSharedAsset).ToList();
        _sharedFolders = AssetFolders.ToList();
        await worker.SyncSharedAssetsAsync(_sharedAssets, _sharedFolders);
        if (HasOpenProject) await worker.SaveAsync(CreateSaveRequest());
    }
    private static bool IsSharedAsset(AssetDefinition asset) => asset.Url.StartsWith("/data/library/", StringComparison.Ordinal);
    private void EnsureEditorLayer()
    {
        if (World is null) { ActiveLayerId = null; return; }
        if (World.Layers.Count == 0)
        {
            World.Layers.Add(new() { Id = "terrain", Name = "Floor & Walls", Order = 0, Purpose = LayerPurpose.Terrain });
            World.Layers.Add(new() { Id = "decorations", Name = "Decorations", Order = 1, Purpose = LayerPurpose.Decoration });
        }
        if (ActiveLayerId is null || !World.Layers.Any(layer => layer.Id == ActiveLayerId)) ActiveLayerId = World.Layers.OrderByDescending(layer => layer.Order).First().Id;
    }
    private void EnsureGeneratedTerrainUnderlays()
    {
        if (World is null) return;
        var terrain = World.Layers.Where(layer => layer.Generated && layer.Purpose == LayerPurpose.Terrain).OrderBy(layer => layer.Order).FirstOrDefault();
        var decorations = World.Layers.Where(layer => layer.Generated && layer.Purpose == LayerPurpose.Decoration).SelectMany(layer => layer.Placements).ToList();
        var floorAssets = Assets.Where(asset => !asset.Excluded && asset.Role == AssetRole.Floor).OrderBy(asset => asset.Id, StringComparer.Ordinal).Select(asset => asset.Id).ToArray();
        if (terrain is null || decorations.Count == 0 || floorAssets.Length == 0) return;

        var occupied = terrain.Placements.Select(item => (item.X, item.Y)).ToHashSet();
        foreach (var placement in decorations.Where(item => !occupied.Contains((item.X, item.Y))))
        {
            var hash = unchecked((uint)World.Seed * 2654435761u ^ (uint)placement.X * 73856093u ^ (uint)placement.Y * 19349663u);
            var floorAssetId = floorAssets[(int)(hash % (uint)floorAssets.Length)];
            terrain.Placements.Add(new($"migrated-terrain-{placement.X}-{placement.Y}", floorAssetId, placement.X, placement.Y));
            occupied.Add((placement.X, placement.Y));
        }
    }
    private static List<WorldLayer> CloneLayers(IEnumerable<WorldLayer> layers) => layers.Select(layer => new WorldLayer { Id = layer.Id, Name = layer.Name, Order = layer.Order, Visible = layer.Visible, Locked = layer.Locked, Purpose = layer.Purpose, Generated = layer.Generated, Placements = layer.Placements.ToList() }).ToList();
    private static List<WorldLayer> MergeLayers(IEnumerable<WorldLayer> generated, IEnumerable<WorldLayer> custom)
    {
        var result = CloneLayers(generated); var order = result.Count;
        foreach (var layer in CloneLayers(custom)) { layer.Order = order++; result.Add(layer); }
        return result;
    }
    private async Task RunAsync(string status, Func<Task> action)
    {
        if (IsBusy) return; IsBusy = true; Error = null; Status = status; Notify();
        try { await action(); }
        catch (OperationCanceledException) { Error = null; Status = "Operation cancelled"; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Editor operation failed while status was {Status}", Status);
            Error = ex is OpenAiException or InvalidOperationException or InvalidDataException ? ex.Message : "The operation failed. Check docker compose logs for details.";
            Status = "Operation failed";
        }
        finally { IsBusy = false; Notify(); }
    }
    private void Notify() => Changed?.Invoke();
}

public sealed record WorldVersion(int Number, DateTimeOffset CreatedAt, SemanticBlueprint Blueprint, WorldDefinition World, ValidationResult Validation, string Source);
