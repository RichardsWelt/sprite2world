namespace Sprite2World.Infrastructure;

public sealed record OpenAiModelDefinition(string Id, string DisplayName, string Group);

public static class OpenAiModelCatalog
{
    public const string DefaultModelId = "gpt-5.6-luna";

    public static IReadOnlyList<OpenAiModelDefinition> Models { get; } =
    [
        new("gpt-5.6-luna", "GPT-5.6 Luna · günstig (Standard)", "GPT-5.6 · aktuell"),
        new("gpt-5.6-terra", "GPT-5.6 Terra · ausgewogen", "GPT-5.6 · aktuell"),
        new("gpt-5.6-sol", "GPT-5.6 Sol · maximale Qualität", "GPT-5.6 · aktuell"),
        new("gpt-5.6", "GPT-5.6 · Alias für Sol", "GPT-5.6 · aktuell"),
        new("gpt-5.4-nano", "GPT-5.4 nano · günstig", "GPT-5.4"),
        new("gpt-5.4-mini", "GPT-5.4 mini · ausgewogen", "GPT-5.4"),
        new("gpt-5.4", "GPT-5.4 · hohe Qualität", "GPT-5.4"),
        new("gpt-5-nano", "GPT-5 nano", "GPT-5"),
        new("gpt-5-mini", "GPT-5 mini", "GPT-5"),
        new("gpt-4.1-nano", "GPT-4.1 nano", "GPT-4.1 · kompatibel"),
        new("gpt-4.1-mini", "GPT-4.1 mini", "GPT-4.1 · kompatibel"),
        new("gpt-4.1", "GPT-4.1", "GPT-4.1 · kompatibel"),
        new("o4-mini", "o4-mini · schnelles Reasoning", "o-Serie · kompatibel"),
        new("o3", "o3 · starkes Reasoning", "o-Serie · kompatibel")
    ];
}
