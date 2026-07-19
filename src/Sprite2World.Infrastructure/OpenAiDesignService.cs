using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Sprite2World.Application;
using Sprite2World.Domain;

namespace Sprite2World.Infrastructure;

public sealed class OpenAiDesignService(HttpClient http, IOptions<OpenAiOptions> options, IOptions<StorageOptions> storage, IOpenAiApiKeyProvider apiKeyProvider) : IBlueprintService, IAssetClassificationService, ISpriteGenerationService
{
    private readonly OpenAiOptions _options = options.Value;
    private readonly string _dataPath = storage.Value.DataPath;

    public Task<SemanticBlueprint> CreateAsync(string prompt, string model, string reasoningEffort, CancellationToken cancellationToken = default) =>
        RequestBlueprintAsync("""
            Design a complete semantic top-down world blueprint from the requested environment type.
            Never output tile coordinates or sprite placements. Keep IDs short and unique. Every region must be connected and the exit must be reachable.
            Use 5-10 distinct regions for Dungeon or Overworld and 4-10 functional rooms for Interior. Give every region a unique gameplay purpose and a readable name.
            Dungeon: enclosed chambers connected by corridors, with at least one loop and meaningful branches.
            Interior: rooms inside one coherent building footprint, connected logically by doors or hallways; include circulation space and believable room functions.
            Overworld: one continuous outdoor ground surface covering the full map. Regions are only semantic biomes or landmarks on that shared surface, never separated islands, chambers or corridor-shaped areas. Keep decoration sparse and connect landmarks with optional paths or roads that may turn or branch. Use tags such as grass, forest, sand, water, plaza, road or path where appropriate.
            Choose decorationDensity and obstacleDensity deliberately for the requested mood and the available library. These values control how many props the deterministic placement engine attempts to place; it enforces sprite footprints, map bounds and non-overlap. Prefer restrained densities for large trees and buildings so the grass remains visually dominant.
            Start and exit must be in different regions. Connections must form one connected graph, include the requested number of loops, and avoid a single linear chain unless explicitly requested.
            Match the available sprite library without inventing concrete asset IDs. Return a complete blueprint that satisfies the schema.
            """, prompt, model, reasoningEffort, cancellationToken);

    public Task<SemanticBlueprint> ImproveAsync(string prompt, SemanticBlueprint current, ValidationResult? validation, string feedback, string model, string reasoningEffort, CancellationToken cancellationToken = default)
    {
        var input = $"Original request:\n{prompt}\n\nCurrent blueprint:\n{JsonSerializer.Serialize(current, JsonOptions.Indented)}\n\nValidation:\n{JsonSerializer.Serialize(validation, JsonOptions.Indented)}\n\nFeedback:\n{feedback}";
        return RequestBlueprintAsync("Revise the complete semantic blueprint to address the feedback while preserving its environment type. Keep every region connected, start and exit distinct, and the layout non-linear where possible. Dungeon and Overworld need 5-10 regions; Interior needs 4-10 rooms. Return a complete blueprint, never direct tile edits or sprite placements.", input, model, reasoningEffort, cancellationToken);
    }

    public async Task<IReadOnlyList<AssetDefinition>> ClassifyAsync(IReadOnlyList<AssetDefinition> assets, string model, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var result = assets.ToList();
        var knownFolders = assets.Select(asset => asset.Category).Where(category => !string.IsNullOrWhiteSpace(category)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(category => category, StringComparer.OrdinalIgnoreCase).Take(40).ToArray();
        foreach (var batch in assets.Where(a => !a.Excluded && !a.ManualOverride).Chunk(16))
        {
            var content = new List<object>
            {
                new { type = "input_text", text = "Classify only the supplied asset IDs into the allowed roles. Folder names are hints, not truth. Also suggest a short free-text theme and subfolder. Reuse an existing user folder when it fits; otherwise create a concise new one. Allowed roles: Floor, Wall, Door, Obstacle, Decoration, Building, Road, Path, Grass, Sand, Water, Lava, Bridge, StartMarker, ExitMarker, Unused, Unknown. Keep reasons and tags concise.\nExisting folders: " + (knownFolders.Length == 0 ? "General" : string.Join(", ", knownFolders)) + "\n" + string.Join('\n', batch.Select(a => $"{a.Id} | {a.RelativePath} | current folder: {a.Category} | {a.Width}x{a.Height}")) }
            };
            foreach (var asset in batch)
            {
                var physical = PhysicalPath(asset.Url);
                if (!File.Exists(physical)) continue;
                var data = await File.ReadAllBytesAsync(physical, cancellationToken);
                content.Add(new { type = "input_text", text = $"Image for {asset.Id}:" });
                content.Add(new { type = "input_image", image_url = $"data:image/png;base64,{Convert.ToBase64String(data)}", detail = "low" });
            }
            var schema = new
            {
                type = "object", additionalProperties = false, required = new[] { "assets" },
                properties = new
                {
                    assets = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object", additionalProperties = false, required = new[] { "assetId", "role", "confidence", "theme", "subfolder", "tags", "reason" },
                            properties = new
                            {
                                assetId = new { type = "string" },
                                role = new { type = "string", @enum = Enum.GetNames<AssetRole>() },
                                confidence = new { type = "number", minimum = 0, maximum = 1 },
                                theme = new { type = "string" },
                                subfolder = new { type = "string" },
                                tags = new { type = "array", items = new { type = "string" } },
                                reason = new { type = "string" }
                            }
                        }
                    }
                }
            };
            var json = await SendAsync(model, "You are a pixel-art asset librarian. Never invent an asset ID.", new object[] { new { role = "user", content = content.ToArray() } }, "asset_classification", schema, _options.ReasoningEffort, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var returnedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.GetProperty("assets").EnumerateArray())
            {
                var id = item.GetProperty("assetId").GetString();
                var asset = result.FirstOrDefault(a => a.Id == id);
                if (asset is null || asset.ManualOverride || !returnedIds.Add(id!)) continue;
                if (!Enum.TryParse<AssetRole>(item.GetProperty("role").GetString(), true, out var role)) role = AssetRole.Unknown;
                asset.Role = role; asset.Confidence = item.GetProperty("confidence").GetDouble(); asset.ClassificationSource = "OpenAI vision";
                if (asset.Category is "" or "General" or "Allgemein" or "Custom")
                {
                    var theme = NormalizeFolderSegment(item.GetProperty("theme").GetString(), "General");
                    var subfolder = NormalizeFolderSegment(item.GetProperty("subfolder").GetString(), role.ToString());
                    asset.Category = $"{theme}/{subfolder}";
                }
                asset.Tags.Clear(); asset.Tags.AddRange(item.GetProperty("tags").EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).Cast<string>().Take(8));
            }
        }
        return result;
    }

    public async Task<GeneratedSprite> GenerateSpriteAsync(string prompt, int width, int height, string model, SpriteGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        options ??= new();
        if (width is < 1 or > 64 || height is < 1 or > 64) throw new OpenAiException("AI sprite generation supports canvases from 1×1 through 64×64 pixels.");
        var schema = new
        {
            type = "object", additionalProperties = false, required = new[] { "name", "description", "composition", "palette", "qualityChecks", "runs" },
            properties = new
            {
                name = new { type = "string" },
                description = new { type = "string" },
                composition = new { type = "object", additionalProperties = false, required = new[] { "view", "framing", "visibleParts", "structuralRules" }, properties = new { view = new { type = "string" }, framing = new { type = "string" }, visibleParts = new { type = "array", minItems = 1, items = new { type = "string" } }, structuralRules = new { type = "array", minItems = 1, items = new { type = "string" } } } },
                palette = new { type = "array", minItems = 1, maxItems = 16, items = new { type = "string", pattern = "^#[0-9A-Fa-f]{6}$" } },
                qualityChecks = new { type = "object", additionalProperties = false, required = new[] { "completeSubject", "closedSilhouette", "continuousStructuralLines", "consistentLineWeight", "insideCanvas" }, properties = new { completeSubject = new { type = "boolean", @enum = new[] { true } }, closedSilhouette = new { type = "boolean", @enum = new[] { true } }, continuousStructuralLines = new { type = "boolean", @enum = new[] { true } }, consistentLineWeight = new { type = "boolean", @enum = new[] { true } }, insideCanvas = new { type = "boolean", @enum = new[] { true } } } },
                runs = new
                {
                    type = "array", maxItems = width * height,
                    items = new
                    {
                        type = "object", additionalProperties = false, required = new[] { "x", "y", "length", "color" },
                        properties = new
                        {
                            x = new { type = "integer", minimum = 0, maximum = width - 1 },
                            y = new { type = "integer", minimum = 0, maximum = height - 1 },
                            length = new { type = "integer", minimum = 1, maximum = width },
                            color = new { type = "string", pattern = "^#[0-9A-Fa-f]{6}$" }
                        }
                    }
                }
            }
        };
        var instructions = $"""
            You are a senior pixel-art sprite artist producing one production-ready {width}x{height} game sprite on a transparent background.

            ART DIRECTION
            - Requested view: {options.View}. Requested framing: {options.Framing}.
            - Outline: {options.Outline}. Light direction: {options.Lighting}.
            - Palette guidance: {(string.IsNullOrWhiteSpace(options.PaletteHint) ? "Choose a compact, harmonious palette." : options.PaletteHint.Trim())}

            COMPOSITION FIRST
            - Interpret the request as one complete subject. Unless the user explicitly asks for a portrait, bust, face, icon, crop, or front view, show the entire object or the animal/person's full body.
            - For a character or animal, include head, torso, every visible limb, paws/feet, ears and tail when applicable. Prefer a readable side or three-quarter game view over a flat face-only front view.
            - For buildings, furniture and constructed objects, make the outer silhouette closed and complete. Rooflines, floors, window rows, beams and other horizontal/vertical structures must continue without accidental missing pixels.
            - Center the subject, keep 1-3 transparent pixels of breathing room where the canvas permits, never crop required parts, and use enough of the canvas to remain readable.

            PIXEL CONSTRUCTION
            - Plan the silhouette and structural lines before details, then fill coherent pixel clusters.
            - Use hard pixel edges only: no anti-aliasing, gradients, blur, background, shadows outside the subject, isolated noise pixels or unintended holes.
            - Use one consistent light direction, a deliberate outline/line weight, repeated spacing for repeated features, and at most 16 exact palette colors.
            - Preserve straight continuous scanlines where the design calls for straight geometry. Symmetric elements must use matching dimensions unless deliberate asymmetry is described.

            OUTPUT AND SELF-CHECK
            - Return colored horizontal pixel runs only; omitted cells remain transparent. Runs must stay inside the canvas and must never overlap.
            - Before returning, verify the complete subject is visible, its silhouette is closed, required parts exist, structural lines have no accidental gaps, line weight is consistent, and every run fits the canvas.
            - Populate composition and qualityChecks from that inspection. If the request is not drawable, return a simple complete neutral placeholder and explain why briefly.
            """;
        GeneratedSprite? candidate = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var repairContext = candidate is null ? "" : $"""

                The first candidate failed deterministic validation:
                {string.Join("\n", candidate.Quality.Issues.Select(issue => $"- {issue.Message}"))}
                Redraw the complete sprite from scratch and correct every listed issue. Do not merely claim that it is fixed.
                """;
            var input = $"Draw this sprite: {prompt.Trim()}\nCanvas: {width}x{height} pixels.{repairContext}";
            var json = await SendAsync(model, instructions, input, "pixel_sprite", schema, options.ReasoningEffort, cancellationToken);
            candidate = ParseSprite(json, prompt, width, height, string.IsNullOrWhiteSpace(model) ? _options.DefaultModel : model, options, attempt);
            if (candidate.Quality.Passed) return candidate;
        }
        return candidate!;
    }

    private static GeneratedSprite ParseSprite(string json, string prompt, int width, int height, string model, SpriteGenerationOptions options, int attempts)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var paletteList = root.GetProperty("palette").EnumerateArray().Select(item => (item.GetString() ?? "").ToLowerInvariant()).Where(IsHexColor).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var palette = paletteList.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pixels = new Dictionary<(int X, int Y), string>();
        foreach (var run in root.GetProperty("runs").EnumerateArray())
        {
            var x = run.GetProperty("x").GetInt32(); var y = run.GetProperty("y").GetInt32();
            var length = run.GetProperty("length").GetInt32(); var color = run.GetProperty("color").GetString() ?? "#ffffff";
            color = color.ToLowerInvariant();
            if (!IsHexColor(color) || !palette.Contains(color) || x < 0 || y < 0 || y >= height || length < 1) throw new OpenAiException("OpenAI returned an invalid sprite run.");
            // Structured output can still contain intersecting horizontal runs. They are
            // valid pixel data, so apply them in response order and clip the last run at
            // the canvas edge instead of discarding the complete generated sprite.
            var end = Math.Min(width, x + length);
            if (end <= x) continue;
            for (var px = x; px < end; px++) pixels[(px, y)] = color;
        }
        if (pixels.Count == 0) throw new OpenAiException("OpenAI returned an empty sprite.");
        var resultPixels = pixels.OrderBy(item => item.Key.Y).ThenBy(item => item.Key.X).Select(item => new GeneratedSpritePixel(item.Key.X, item.Key.Y, item.Value)).ToArray();
        var compositionJson = root.GetProperty("composition");
        var composition = new GeneratedSpriteComposition(
            compositionJson.GetProperty("view").GetString() ?? options.View,
            compositionJson.GetProperty("framing").GetString() ?? options.Framing,
            compositionJson.GetProperty("visibleParts").EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToArray(),
            compositionJson.GetProperty("structuralRules").EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToArray());
        var quality = SpriteQualityAnalyzer.Analyze(width, height, resultPixels);
        var metadata = new SpriteGenerationMetadata(prompt.Trim(), model, DateTimeOffset.UtcNow, "sprite-ai-2.0", composition.View, composition.Framing, options.Outline, options.Lighting, paletteList, attempts);
        return new(root.GetProperty("name").GetString() ?? "AI sprite", root.GetProperty("description").GetString() ?? "", resultPixels, composition, quality, metadata);
    }

    private async Task<SemanticBlueprint> RequestBlueprintAsync(string instructions, string input, string model, string reasoningEffort, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var schema = BlueprintSchema();
        var json = await SendAsync(model, instructions, input, "semantic_blueprint", schema, reasoningEffort, cancellationToken);
        var blueprint = JsonSerializer.Deserialize<SemanticBlueprint>(json, JsonOptions.Indented) ?? throw new OpenAiException("OpenAI returned an empty blueprint.");
        blueprint = BlueprintGraphRepair.Repair(blueprint);
        var validationErrors = BlueprintValidator.Validate(blueprint);
        if (validationErrors.Count > 0) throw new OpenAiException($"OpenAI returned an invalid blueprint: {string.Join(" ", validationErrors)}");
        return blueprint;
    }

    private async Task<string> SendAsync(string model, string instructions, object input, string schemaName, object schema, string reasoningEffort, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKeyProvider.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model = string.IsNullOrWhiteSpace(model) ? _options.DefaultModel : model,
            instructions,
            input,
            store = false,
            reasoning = new { effort = NormalizeEffort(reasoningEffort) },
            text = new { format = new { type = "json_schema", name = schemaName, strict = true, schema } }
        });
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw FriendlyError(response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        foreach (var output in document.RootElement.GetProperty("output").EnumerateArray())
        {
            if (!output.TryGetProperty("content", out var content)) continue;
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("refusal", out var refusal)) throw new OpenAiException($"The model declined the request: {refusal.GetString()}");
                if (part.TryGetProperty("type", out var type) && type.GetString() == "output_text" && part.TryGetProperty("text", out var text)) return text.GetString() ?? throw new OpenAiException("OpenAI returned empty output text.");
            }
        }
        throw new OpenAiException("OpenAI returned no structured output.");
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(apiKeyProvider.ApiKey)) throw new OpenAiException("OpenAI is locked. Add and validate an API key in Settings first.");
    }
    private string PhysicalPath(string url)
    {
        const string prefix = "/data/";
        if (!url.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Invalid asset URL.");
        var relative = Uri.UnescapeDataString(url[prefix.Length..]).Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(_dataPath, relative));
        var root = Path.GetFullPath(_dataPath) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("Asset path escapes the data directory.");
        return path;
    }
    private static string NormalizeEffort(string effort) => effort.ToLowerInvariant() is "low" or "high" ? effort.ToLowerInvariant() : "medium";
    private static bool IsHexColor(string value) => value.Length == 7 && value[0] == '#' && value[1..].All(Uri.IsHexDigit);
    private static string NormalizeFolderSegment(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().Replace('/', '-').Replace('\\', '-');
        return normalized[..Math.Min(normalized.Length, 40)];
    }
    private static OpenAiException FriendlyError(HttpStatusCode status, string body)
    {
        var detail = "";
        try { using var doc = JsonDocument.Parse(body); detail = doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? ""; } catch (JsonException) { }
        return status switch
        {
            HttpStatusCode.Unauthorized => new("OpenAI authentication failed. Check the configured API key."),
            HttpStatusCode.TooManyRequests => new("OpenAI rate or quota limit reached. Please wait or check project billing."),
            HttpStatusCode.BadRequest when detail.Contains("model", StringComparison.OrdinalIgnoreCase) => new($"The selected OpenAI model is unavailable: {detail}"),
            _ => new($"OpenAI request failed ({(int)status}). {detail}".Trim())
        };
    }
    private static object BlueprintSchema() => new
    {
        type = "object", additionalProperties = false,
        required = new[] { "schemaVersion", "theme", "worldType", "environmentType", "widthHint", "heightHint", "regions", "connections", "startRegionId", "exitRegionId", "requiredLoops", "desiredDeadEnds", "decorationDensity", "obstacleDensity", "seed", "constraints" },
        properties = new
        {
            schemaVersion = new { type = "string", @enum = new[] { "1.0" } }, theme = new { type = "string" }, worldType = new { type = "string", @enum = new[] { "TopDownRooms" } }, environmentType = new { type = "string", @enum = new[] { "Dungeon", "Interior", "Overworld" } },
            widthHint = new { type = "integer", minimum = 32, maximum = 128 }, heightHint = new { type = "integer", minimum = 26, maximum = 128 },
            regions = new { type = "array", minItems = 4, maxItems = 10, items = new { type = "object", additionalProperties = false, required = new[] { "id", "name", "purpose", "size", "tags" }, properties = new { id = new { type = "string" }, name = new { type = "string" }, purpose = new { type = "string" }, size = new { type = "string", @enum = new[] { "Small", "Medium", "Large" } }, tags = new { type = "array", items = new { type = "string" } } } } },
            connections = new { type = "array", items = new { type = "object", additionalProperties = false, required = new[] { "from", "to", "type", "required" }, properties = new { from = new { type = "string" }, to = new { type = "string" }, type = new { type = "string" }, required = new { type = "boolean" } } } },
            startRegionId = new { type = "string" }, exitRegionId = new { type = "string" }, requiredLoops = new { type = "integer", minimum = 0, maximum = 3 }, desiredDeadEnds = new { type = "integer", minimum = 0, maximum = 5 },
            decorationDensity = new { type = "number", minimum = 0, maximum = .5 }, obstacleDensity = new { type = "number", minimum = 0, maximum = .25 }, seed = new { type = "integer" }, constraints = new { type = "array", items = new { type = "string" } }
        }
    };
}

public sealed class OpenAiException(string message) : Exception(message);
