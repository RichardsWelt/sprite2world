using Sprite2World.Domain;

namespace Sprite2World.Web.Services;

public sealed class UiLocalizer
{
    public event Action? Changed;
    public string Language { get; private set; } = "en";
    public bool IsGerman => Language == "de";

    public string Get(string english, string german) => IsGerman ? german : english;

    public void SetLanguage(string? language)
    {
        var normalized = string.Equals(language, "de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
        if (Language == normalized) return;
        Language = normalized;
        Changed?.Invoke();
    }

    public string Role(AssetRole role) => role switch
    {
        AssetRole.Floor => Get("Floor", "Boden"), AssetRole.Wall => Get("Wall", "Wand"), AssetRole.Door => Get("Door", "Tür"),
        AssetRole.Obstacle => Get("Obstacle", "Hindernis"), AssetRole.Decoration => Get("Decoration", "Dekoration"), AssetRole.Building => Get("Building", "Gebäude"),
        AssetRole.Road => Get("Road", "Straße"), AssetRole.Path => Get("Path", "Pfad"), AssetRole.Grass => Get("Grass", "Gras"), AssetRole.Sand => Get("Sand", "Sand"),
        AssetRole.Water => Get("Water", "Wasser"), AssetRole.Lava => Get("Lava", "Lava"), AssetRole.Bridge => Get("Bridge", "Brücke"),
        AssetRole.StartMarker => Get("Start", "Start"), AssetRole.ExitMarker => Get("Exit", "Ausgang"), AssetRole.Unused => Get("Unused", "Ungenutzt"),
        _ => Get("Unknown", "Unbekannt")
    };

    public string ValidationMessage(ValidationIssue issue) => issue.CheckId switch
    {
        "start.exists" => issue.Passed ? Get("Start cell is valid and walkable.", "Das Startfeld ist gültig und begehbar.") : Get("Start cell is missing or blocked.", "Das Startfeld fehlt oder ist blockiert."),
        "exit.exists" => issue.Passed ? Get("Exit cell is valid and walkable.", "Das Ausgangsfeld ist gültig und begehbar.") : Get("Exit cell is missing or blocked.", "Das Ausgangsfeld fehlt oder ist blockiert."),
        "exit.reachable" => issue.Passed ? Get("Exit is reachable from the start.", "Der Ausgang ist vom Start erreichbar.") : Get("Exit cannot be reached from the start.", "Der Ausgang ist vom Start nicht erreichbar."),
        "regions.connected" => issue.Passed ? Get("All regions are connected.", "Alle Regionen sind verbunden.") : Get("At least one region is isolated.", "Mindestens eine Region ist isoliert."),
        "rooms.overlap" => issue.Passed ? Get("Rooms do not overlap illegally.", "Räume überlappen sich nicht unzulässig.") : Get("Illegal room overlap detected.", "Unzulässige Raumüberlappung erkannt."),
        "collision.consistent" => issue.Passed ? Get("Collision map matches tile walkability.", "Die Kollisionskarte entspricht der Begehbarkeit.") : Get("Collision data is inconsistent.", "Die Kollisionsdaten sind inkonsistent."),
        "assets.valid" => issue.Passed ? Get("All tile references resolve to imported assets.", "Alle Kachelreferenzen verweisen auf importierte Assets.") : Get("The map references an unknown asset.", "Die Karte verweist auf ein unbekanntes Asset."),
        "assets.role.floor" => issue.Passed ? Get("Floor asset is available.", "Ein Boden-Asset ist verfügbar.") : Get("No floor asset mapped; debug placeholders are used.", "Kein Boden-Asset zugeordnet; Debug-Platzhalter werden verwendet."),
        "assets.role.wall" => issue.Passed ? Get("Wall asset is available.", "Ein Wand-Asset ist verfügbar.") : Get("No wall asset mapped; debug placeholders are used.", "Kein Wand-Asset zugeordnet; Debug-Platzhalter werden verwendet."),
        "map.boundaries" => issue.Passed ? Get("Map boundaries are valid.", "Die Kartengrenzen sind gültig.") : Get("Map dimensions or cells are invalid.", "Kartengröße oder Zellen sind ungültig."),
        _ => issue.Message
    };

    public string VersionSource(string source) => source switch
    {
        "Deterministic demo" => Get("Deterministic demo", "Deterministische Demo"),
        "OpenAI blueprint" => Get("OpenAI blueprint", "OpenAI-Entwurf"),
        "Deterministic blueprint" => Get("Deterministic blueprint", "Deterministischer Entwurf"),
        "AI feedback" => Get("AI feedback", "KI-Feedback"),
        "Local feedback" => Get("Local feedback", "Lokales Feedback"),
        "Saved project" => Get("Saved project", "Gespeichertes Projekt"),
        _ => source
    };
}
