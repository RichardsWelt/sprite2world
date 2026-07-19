using System.Text.Json;
using Microsoft.Extensions.Options;
using Sprite2World.Contracts;

namespace Sprite2World.Infrastructure;

public sealed class ProjectFileStore(IOptions<StorageOptions> options)
{
    private readonly string _root = Path.Combine(options.Value.DataPath, "projects");

    public async Task SaveAsync(SaveProjectRequest project, CancellationToken cancellationToken = default)
    {
        var directory = GetProjectDirectory(project.ProjectId); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "project.json"), JsonSerializer.Serialize(project, JsonOptions.Indented), cancellationToken);
    }
    public async Task SavePreviewAsync(string id, byte[] png, CancellationToken cancellationToken = default)
    {
        var directory = GetProjectDirectory(id); Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "preview.png"), png, cancellationToken);
    }
    public bool HasPreview(string id) => File.Exists(Path.Combine(GetProjectDirectory(id), "preview.png"));
    public async Task<SaveProjectRequest?> LoadAsync(string id, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(GetProjectDirectory(id), "project.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<SaveProjectRequest>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions.Indented) : null;
    }
    public IEnumerable<ProjectSummary> List()
    {
        if (!Directory.Exists(_root)) yield break;
        foreach (var file in Directory.EnumerateFiles(_root, "project.json", SearchOption.AllDirectories))
        {
            SaveProjectRequest? project = null;
            try { project = JsonSerializer.Deserialize<SaveProjectRequest>(File.ReadAllText(file), JsonOptions.Indented); } catch (JsonException) { }
            if (project is not null) yield return new(project.ProjectId, project.Name, File.GetLastWriteTimeUtc(file));
        }
    }
    public Task DeleteAsync(string id)
    {
        var directory = GetProjectDirectory(id); if (Directory.Exists(directory)) Directory.Delete(directory, true); return Task.CompletedTask;
    }
    private string GetProjectDirectory(string id)
    {
        var safe = new string(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (safe != id || string.IsNullOrEmpty(safe)) throw new InvalidDataException("Invalid project identifier.");
        return Path.Combine(_root, safe);
    }
}
