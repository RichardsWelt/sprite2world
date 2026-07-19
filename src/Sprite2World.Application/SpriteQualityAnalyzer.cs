namespace Sprite2World.Application;

public static class SpriteQualityAnalyzer
{
    public static SpriteQualityReport Analyze(int width, int height, IReadOnlyList<GeneratedSpritePixel> pixels)
    {
        var issues = new List<SpriteQualityIssue>();
        var occupied = pixels.Where(p => p.X >= 0 && p.Y >= 0 && p.X < width && p.Y < height).Select(p => (p.X, p.Y)).ToHashSet();
        if (occupied.Count != pixels.Count) issues.Add(new("outside-canvas", "Pixels extend beyond the canvas."));
        if (occupied.Count == 0) return new(false, 0, 0, 0, 0, [new("empty", "The sprite is empty.")]);

        var components = 0;
        var isolated = 0;
        var remaining = occupied.ToHashSet();
        while (remaining.Count > 0)
        {
            components++;
            var first = remaining.First();
            var queue = new Queue<(int X, int Y)>();
            queue.Enqueue(first); remaining.Remove(first);
            var size = 0;
            while (queue.Count > 0)
            {
                var point = queue.Dequeue(); size++;
                foreach (var next in Neighbors(point).Where(remaining.Remove)) queue.Enqueue(next);
            }
            if (size == 1) isolated++;
        }

        var minX = occupied.Min(p => p.X); var maxX = occupied.Max(p => p.X);
        var minY = occupied.Min(p => p.Y); var maxY = occupied.Max(p => p.Y);
        var boundingArea = (maxX - minX + 1) * (maxY - minY + 1);
        var fill = occupied.Count / (double)(width * height);
        if (components > 4) issues.Add(new("fragmented", $"The sprite is split into {components} disconnected parts."));
        if (isolated > 0) issues.Add(new("isolated-pixels", $"The sprite contains {isolated} isolated noise pixel(s)."));
        if (fill < .035) issues.Add(new("too-small", "The subject uses too little of the canvas."));
        if (fill > .92) issues.Add(new("no-breathing-room", "The subject fills almost the entire canvas and may be cropped."));
        if (minX == 0 || minY == 0 || maxX == width - 1 || maxY == height - 1) issues.Add(new("touches-edge", "The subject touches the canvas edge; verify that it is not cropped."));

        var holes = CountTransparentHoles(occupied, minX, minY, maxX, maxY);
        if (holes > Math.Max(8, boundingArea / 12)) issues.Add(new("excessive-holes", "The silhouette contains many enclosed transparent gaps."));
        return new(issues.Count == 0, components, isolated, holes, fill, issues);
    }

    private static int CountTransparentHoles(HashSet<(int X, int Y)> occupied, int minX, int minY, int maxX, int maxY)
    {
        var transparent = new HashSet<(int X, int Y)>();
        for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++) if (!occupied.Contains((x, y))) transparent.Add((x, y));
        var outside = new HashSet<(int X, int Y)>();
        var queue = new Queue<(int X, int Y)>();
        foreach (var point in transparent.Where(p => p.X == minX || p.X == maxX || p.Y == minY || p.Y == maxY).ToArray()) if (outside.Add(point)) queue.Enqueue(point);
        while (queue.Count > 0)
        {
            var point = queue.Dequeue();
            foreach (var next in Neighbors(point).Where(p => p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY && transparent.Contains(p)).Where(outside.Add)) queue.Enqueue(next);
        }
        return transparent.Count - outside.Count;
    }

    private static IEnumerable<(int X, int Y)> Neighbors((int X, int Y) p)
    {
        yield return (p.X - 1, p.Y); yield return (p.X + 1, p.Y); yield return (p.X, p.Y - 1); yield return (p.X, p.Y + 1);
    }
}
