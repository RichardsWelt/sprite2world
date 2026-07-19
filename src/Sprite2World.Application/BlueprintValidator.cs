using Sprite2World.Domain;

namespace Sprite2World.Application;

public static class BlueprintValidator
{
    public static IReadOnlyList<string> Validate(SemanticBlueprint blueprint)
    {
        var errors = new List<string>();
        if (blueprint.SchemaVersion != "1.0" || blueprint.WorldType != "TopDownRooms" || blueprint.EnvironmentType is not ("Dungeon" or "Interior" or "Overworld")) errors.Add("Unsupported schema, world type or environment type.");
        var minimum = blueprint.EnvironmentType == "Interior" ? 4 : 5;
        if (blueprint.Regions.Count < minimum || blueprint.Regions.Count > 10) errors.Add($"{blueprint.EnvironmentType} needs {minimum}-10 regions.");
        var ids = blueprint.Regions.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != blueprint.Regions.Count || ids.Any(string.IsNullOrWhiteSpace)) errors.Add("Region IDs must be non-empty and unique.");
        if (!ids.Contains(blueprint.StartRegionId) || !ids.Contains(blueprint.ExitRegionId)) errors.Add("Start or exit references an unknown region.");
        if (blueprint.StartRegionId == blueprint.ExitRegionId) errors.Add("Start and exit must be different regions.");

        var edges = new HashSet<string>(StringComparer.Ordinal);
        var adjacency = ids.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var connection in blueprint.Connections)
        {
            if (!ids.Contains(connection.From) || !ids.Contains(connection.To)) { errors.Add("A connection references an unknown region."); continue; }
            if (connection.From == connection.To) { errors.Add($"Region '{connection.From}' has a self-connection."); continue; }
            var key = string.CompareOrdinal(connection.From, connection.To) < 0 ? $"{connection.From}\0{connection.To}" : $"{connection.To}\0{connection.From}";
            if (!edges.Add(key)) errors.Add($"Duplicate connection between '{connection.From}' and '{connection.To}'.");
            adjacency[connection.From].Add(connection.To); adjacency[connection.To].Add(connection.From);
        }
        if (ids.Count > 0)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal); var queue = new Queue<string>(); queue.Enqueue(ids.First()); visited.Add(ids.First());
            while (queue.Count > 0) foreach (var next in adjacency[queue.Dequeue()].Where(visited.Add)) queue.Enqueue(next);
            if (visited.Count != ids.Count) errors.Add("The region graph is disconnected.");
        }
        var loops = Math.Max(0, edges.Count - ids.Count + (ids.Count == 0 ? 0 : 1));
        if (loops < blueprint.RequiredLoops) errors.Add($"The graph has {loops} loop(s), but {blueprint.RequiredLoops} are required.");
        var deadEnds = adjacency.Count(pair => pair.Value.Count == 1 && pair.Key != blueprint.StartRegionId && pair.Key != blueprint.ExitRegionId);
        if (deadEnds < blueprint.DesiredDeadEnds) errors.Add($"The graph has {deadEnds} optional dead end(s), but {blueprint.DesiredDeadEnds} are requested.");
        return errors.Distinct().ToArray();
    }
}
