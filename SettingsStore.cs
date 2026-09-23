using System.IO;
using System.Text.Json;

namespace Zen;

public sealed class ZenSettings
{
    public int ReloadSeconds { get; set; } = 60;
    public string GlobalPrompt { get; set; } = string.Empty;
    public string PromptPathEntry { get; set; } = string.Empty;
    public string OperatorPathEntry { get; set; } = string.Empty;
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zen");
    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public ZenSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<ZenSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new ZenSettings();
        }
        catch (JsonException) { }
        return new ZenSettings { GlobalPrompt = ProjectStore.ReadEmbeddedPrompt() };
    }

    public void Save(ZenSettings settings)
    {
        settings.ReloadSeconds = Math.Clamp(settings.ReloadSeconds, 5, 3600);
        Directory.CreateDirectory(DataDirectory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, true);
    }

    public static string BackupProjectData(string projectPath)
    {
        var source = Path.Combine(projectPath, ProjectStore.DataFileName);
        var backupRoot = Path.Combine(DataDirectory, "project-backups");
        Directory.CreateDirectory(backupRoot);
        var safeName = string.Concat(new DirectoryInfo(projectPath).Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var destination = Path.Combine(backupRoot, $"{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        if (File.Exists(source)) File.Copy(source, destination, true);
        return destination;
    }
}
