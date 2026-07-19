namespace Sprite2World.Infrastructure;

public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAI";
    public string? ApiKey { get; set; }
    public string DefaultModel { get; set; } = OpenAiModelCatalog.DefaultModelId;
    public string ReasoningEffort { get; set; } = "medium";
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";
    public string DataPath { get; set; } = "/app/data";
    public int MaxAssets { get; set; } = 10_000;
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;
    public long MaxTotalBytes { get; set; } = 100 * 1024 * 1024;
}
