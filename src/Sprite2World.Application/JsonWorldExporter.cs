using System.Text.Json;
using System.Text.Json.Serialization;
using Sprite2World.Domain;

namespace Sprite2World.Application;

public sealed class JsonWorldExporter : IWorldExporter
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public string ExportJson(ProjectExport project) => JsonSerializer.Serialize(project, Options);
}
