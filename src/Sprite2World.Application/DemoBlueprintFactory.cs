using Sprite2World.Domain;

namespace Sprite2World.Application;

public static class DemoBlueprintFactory
{
    public const string DungeonPrompt = "Create a compact abandoned dungeon with six rooms, one main loop, two dead ends and a hidden treasure room. Put the exit in the largest room.";

    public static SemanticBlueprint Create(int seed = 424242) => new()
    {
        Theme = "Abandoned Dungeon",
        EnvironmentType = "Dungeon",
        WidthHint = 56,
        HeightHint = 40,
        RequiredLoops = 1,
        DesiredDeadEnds = 2,
        DecorationDensity = .18,
        ObstacleDensity = .07,
        Seed = seed,
        StartRegionId = "entrance",
        ExitRegionId = "sanctum",
        Regions =
        [
            new("entrance", "Forgotten Entrance", "Safe starting chamber", "Small", ["safe", "sparse"]),
            new("hall", "Pillared Hall", "Central navigation hub", "Large", ["landmark"]),
            new("archive", "Flooded Archive", "Exploration branch", "Medium", ["water", "lore"]),
            new("armory", "Ruined Armory", "Optional dead end", "Small", ["loot"]),
            new("crypt", "Dust Crypt", "Hidden treasure room", "Medium", ["secret", "treasure"]),
            new("well", "Forgotten Well", "Optional atmospheric dead end", "Small", ["water", "dead-end"]),
            new("sanctum", "Moon Sanctum", "Final chamber and exit", "Large", ["exit"])
        ],
        Connections =
        [
            new("entrance", "hall"), new("hall", "archive"), new("archive", "sanctum"),
            new("hall", "crypt"), new("crypt", "sanctum"), new("hall", "armory"), new("archive", "well")
        ],
        Constraints = ["Exit must be reachable", "Keep a clear route from entrance to sanctum"]
    };

    public static SemanticBlueprint CreateForEnvironment(string? environment, int seed = 424242) => environment switch
    {
        "Interior" => new()
        {
            Theme = "Townhouse Interior", EnvironmentType = "Interior", WidthHint = 58, HeightHint = 38, RequiredLoops = 0, DesiredDeadEnds = 2,
            DecorationDensity = .16, ObstacleDensity = .04, Seed = seed, StartRegionId = "foyer", ExitRegionId = "terrace",
            Regions =
            [
                new("foyer", "Foyer", "Building entrance and circulation", "Small", ["entrance", "hallway"]),
                new("living", "Living Room", "Main social room", "Large", ["windows", "seating"]),
                new("kitchen", "Kitchen", "Food preparation", "Medium", ["worktop"]),
                new("office", "Office", "Quiet optional room", "Small", ["dead-end"]),
                new("storage", "Storage", "Utility dead end", "Small", ["dead-end"]),
                new("terrace", "Terrace Door", "Rear exit", "Medium", ["exit", "windows"])
            ],
            Connections = [new("foyer", "living", "Door"), new("living", "kitchen", "WideDoor"), new("living", "office", "Door"), new("kitchen", "storage", "Door"), new("kitchen", "terrace", "Door")],
            Constraints = ["Keep all rooms inside one rectangular building shell", "Use doors at room transitions"]
        },
        "Overworld" => new()
        {
            Theme = "Coastal Woodland", EnvironmentType = "Overworld", WidthHint = 72, HeightHint = 52, RequiredLoops = 1, DesiredDeadEnds = 2,
            DecorationDensity = .12, ObstacleDensity = .035, Seed = seed, StartRegionId = "village", ExitRegionId = "cliff",
            Regions =
            [
                new("village", "Trailhead Village", "Safe starting landmark", "Medium", ["plaza", "grass"]),
                new("grove", "Pine Grove", "Woodland route", "Large", ["forest", "grass"]),
                new("beach", "Quiet Beach", "Open coastal zone", "Large", ["sand", "water"]),
                new("pond", "Reed Pond", "Optional natural dead end", "Small", ["water", "dead-end"]),
                new("ruins", "Hill Ruins", "Optional landmark dead end", "Medium", ["stone", "dead-end"]),
                new("crossroads", "Old Crossroads", "Route junction", "Medium", ["road", "path"]),
                new("cliff", "Beacon Cliff", "Final overlook and exit", "Medium", ["rock", "exit"])
            ],
            Connections = [new("village", "grove", "Path"), new("village", "beach", "Road"), new("grove", "crossroads", "Path"), new("beach", "crossroads", "Path"), new("grove", "pond", "Path"), new("crossroads", "ruins", "Path"), new("crossroads", "cliff", "Road")],
            Constraints = ["Cover the complete map with one continuous outdoor ground surface", "Keep decoration sparse", "Connect landmarks with readable paths"]
        },
        _ => Create(seed)
    };

    public static SemanticBlueprint Improve(SemanticBlueprint source, string feedback)
    {
        var lower = feedback.ToLowerInvariant();
        var loops = source.RequiredLoops;
        var decoration = source.DecorationDensity;
        var obstacles = source.ObstacleDensity;
        var width = source.WidthHint;
        var height = source.HeightHint;
        if (lower.Contains("loop") || lower.Contains("linear")) loops = Math.Min(3, loops + 1);
        if (lower.Contains("empty") || lower.Contains("decoration")) decoration = Math.Min(.45, decoration + .1);
        if (lower.Contains("crowd")) { decoration = Math.Max(.05, decoration - .08); obstacles = Math.Max(.02, obstacles - .04); }
        if (lower.Contains("large")) { width = Math.Max(36, width - 8); height = Math.Max(28, height - 6); }
        if (lower.Contains("small")) { width = Math.Min(96, width + 8); height = Math.Min(72, height + 6); }
        return source with { RequiredLoops = loops, DecorationDensity = decoration, ObstacleDensity = obstacles, WidthHint = width, HeightHint = height, Seed = source.Seed + 1 };
    }
}
