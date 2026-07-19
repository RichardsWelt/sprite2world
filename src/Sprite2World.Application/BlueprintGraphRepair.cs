using Sprite2World.Domain;

namespace Sprite2World.Application;

public static class BlueprintGraphRepair
{
    public static SemanticBlueprint Repair(SemanticBlueprint blueprint)
    {
        var orderedIds = blueprint.Regions.Select(region => region.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (orderedIds.Count < 2) return blueprint;
        var ids = orderedIds.ToHashSet(StringComparer.Ordinal);
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        var connections = new List<BlueprintConnection>();

        foreach (var connection in blueprint.Connections)
        {
            if (!ids.Contains(connection.From) || !ids.Contains(connection.To) || connection.From == connection.To) continue;
            if (!edgeKeys.Add(Key(connection.From, connection.To))) continue;
            connections.Add(connection);
        }

        var connectionType = blueprint.EnvironmentType switch
        {
            "Interior" => "Door",
            "Overworld" => "Path",
            _ => "Corridor"
        };

        while (Components(orderedIds, connections).Count > 1)
        {
            var components = Components(orderedIds, connections);
            var from = components[0][0];
            var to = components[1][0];
            connections.Add(new(from, to, connectionType, true));
            edgeKeys.Add(Key(from, to));
        }

        var start = ids.Contains(blueprint.StartRegionId) ? blueprint.StartRegionId : orderedIds[0];
        var exit = ids.Contains(blueprint.ExitRegionId) && blueprint.ExitRegionId != start
            ? blueprint.ExitRegionId
            : orderedIds.First(id => id != start);
        var requiredLoops = Math.Clamp(blueprint.RequiredLoops, 0, 3);
        while (LoopCount(orderedIds.Count, connections) < requiredLoops)
        {
            var candidate = orderedIds.SelectMany((from, index) => orderedIds.Skip(index + 1).Select(to => (from, to)))
                .FirstOrDefault(pair => !edgeKeys.Contains(Key(pair.from, pair.to)));
            if (candidate == default) break;
            connections.Add(new(candidate.from, candidate.to, connectionType, false));
            edgeKeys.Add(Key(candidate.from, candidate.to));
        }

        var degrees = orderedIds.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var connection in connections) { degrees[connection.From]++; degrees[connection.To]++; }
        var achievedDeadEnds = degrees.Count(pair => pair.Value == 1 && pair.Key != start && pair.Key != exit);
        var desiredDeadEnds = Math.Clamp(blueprint.DesiredDeadEnds, 0, Math.Max(0, orderedIds.Count - 2));
        while (desiredDeadEnds > 0 && MaximumLoops(orderedIds.Count - desiredDeadEnds) < requiredLoops) desiredDeadEnds--;
        if (achievedDeadEnds < desiredDeadEnds)
            connections = BuildCanonicalGraph(orderedIds, start, exit, desiredDeadEnds, requiredLoops, connectionType);

        return blueprint with
        {
            Connections = connections,
            StartRegionId = start,
            ExitRegionId = exit,
            RequiredLoops = Math.Min(requiredLoops, LoopCount(orderedIds.Count, connections)),
            DesiredDeadEnds = desiredDeadEnds
        };
    }

    private static List<BlueprintConnection> BuildCanonicalGraph(IReadOnlyList<string> ids, string start, string exit, int desiredDeadEnds, int requiredLoops, string type)
    {
        var leaves = ids.Where(id => id != start && id != exit).Reverse().Take(desiredDeadEnds).ToHashSet(StringComparer.Ordinal);
        var core = ids.Where(id => !leaves.Contains(id)).ToList();
        core.Remove(start); core.Remove(exit); core.Insert(0, start); core.Add(exit);
        var result = new List<BlueprintConnection>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < core.Count; index++) Add(core[index - 1], core[index], true);
        var hub = core.Count > 2 ? core[1] : core[0];
        foreach (var leaf in leaves) Add(hub, leaf, true);
        while (LoopCount(ids.Count, result) < requiredLoops)
        {
            var candidate = core.SelectMany((from, index) => core.Skip(index + 1).Select(to => (from, to))).FirstOrDefault(pair => !keys.Contains(Key(pair.from, pair.to)));
            if (candidate == default) break;
            Add(candidate.from, candidate.to, false);
        }
        return result;

        void Add(string from, string to, bool required)
        {
            if (!keys.Add(Key(from, to))) return;
            result.Add(new(from, to, type, required));
        }
    }

    private static int MaximumLoops(int vertices) => vertices < 3 ? 0 : vertices * (vertices - 1) / 2 - (vertices - 1);

    private static int LoopCount(int vertices, IReadOnlyCollection<BlueprintConnection> connections) => Math.Max(0, connections.Count - vertices + 1);
    private static string Key(string from, string to) => string.CompareOrdinal(from, to) < 0 ? $"{from}\0{to}" : $"{to}\0{from}";

    private static List<List<string>> Components(IReadOnlyList<string> ids, IReadOnlyList<BlueprintConnection> connections)
    {
        var adjacency = ids.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var connection in connections) { adjacency[connection.From].Add(connection.To); adjacency[connection.To].Add(connection.From); }
        var remaining = ids.ToHashSet(StringComparer.Ordinal);
        var result = new List<List<string>>();
        while (remaining.Count > 0)
        {
            var component = new List<string>();
            var queue = new Queue<string>();
            var first = ids.First(remaining.Contains); queue.Enqueue(first); remaining.Remove(first);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue(); component.Add(current);
                foreach (var next in adjacency[current].Where(remaining.Remove)) queue.Enqueue(next);
            }
            result.Add(component);
        }
        return result;
    }
}
