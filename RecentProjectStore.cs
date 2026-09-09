using System.IO;
using System.Text.Json;

namespace Zen;

public sealed class RecentProject
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTimeOffset LastOpenedAt { get; set; }
}

public sealed class RecentProjectStore
{
    private readonly string _dataPath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RecentProjectStore()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zen");
        _dataPath = Path.Combine(directory, "known-projects.json");
    }

    public IReadOnlyList<RecentProject> Load()
    {
        if (!File.Exists(_dataPath)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<RecentProject>>(File.ReadAllText(_dataPath), JsonOptions) ?? [])
                .Where(project => Directory.Exists(project.Path))
                .OrderByDescending(project => project.LastOpenedAt)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Remember(string path, string name)
    {
        var fullPath = Path.GetFullPath(path);
        var projects = Load().Where(project => !project.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase)).ToList();
        projects.Insert(0, new RecentProject { Name = name, Path = fullPath, LastOpenedAt = DateTimeOffset.UtcNow });
        var directory = Path.GetDirectoryName(_dataPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _dataPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(projects.Take(20), JsonOptions));
        File.Move(temporaryPath, _dataPath, true);
    }
}
