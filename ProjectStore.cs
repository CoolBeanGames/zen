using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace Zen;

public sealed class ProjectStore
{
    public const string DataFileName = "zen.tasks.json";
    public const string PromptFileName = "prompt.txt";
    public const string FilesDirectoryName = "files";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly string[] TagColors =
    [
        "#6557B8", "#2F7A68", "#9A6334", "#386D9A", "#8A4F78",
        "#57742F", "#815B32", "#3F6F77", "#704F9B", "#8C553E"
    ];

    public string RootDirectory { get; }
    public string DataPath => Path.Combine(RootDirectory, DataFileName);
    public string FilesPath => Path.Combine(RootDirectory, FilesDirectoryName);
    public ProjectStore(string rootDirectory) => RootDirectory = Path.GetFullPath(rootDirectory);

    public ProjectDocument OpenOrCreate()
    {
        Directory.CreateDirectory(FilesPath);
        EnsurePromptFile();
        var document = File.Exists(DataPath)
            ? JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(DataPath), JsonOptions)
                ?? throw new InvalidDataException($"Could not read {DataFileName}.")
            : CreateDocument();
        Normalize(document);
        HydrateFiles(document);
        HydrateTags(document);
        Save(document);
        return document;
    }

    public void Save(ProjectDocument document)
    {
        Normalize(document);
        HydrateFiles(document);
        HydrateTags(document);
        var temporaryPath = DataPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporaryPath, DataPath, true);
    }

    public CardFile ImportFile(string sourcePath)
    {
        Directory.CreateDirectory(FilesPath);
        var originalName = Path.GetFileName(sourcePath);
        var destinationName = originalName;
        var stem = Path.GetFileNameWithoutExtension(originalName);
        var extension = Path.GetExtension(originalName);
        var suffix = 2;
        while (File.Exists(Path.Combine(FilesPath, destinationName)))
            destinationName = $"{stem}-{suffix++}{extension}";
        var destination = Path.Combine(FilesPath, destinationName);
        File.Copy(sourcePath, destination);
        return new CardFile
        {
            Name = originalName,
            RelativePath = $"files/{destinationName}",
            Size = new FileInfo(destination).Length,
            AbsolutePath = destination,
            IsImage = IsImageExtension(extension)
        };
    }

    private void EnsurePromptFile()
    {
        var destination = Path.Combine(RootDirectory, PromptFileName);
        if (File.Exists(destination)) return;
        var configuredPrompt = new SettingsStore().Load().GlobalPrompt;
        File.WriteAllText(destination, string.IsNullOrWhiteSpace(configuredPrompt) ? ReadEmbeddedPrompt() : configuredPrompt);
    }

    public static string ReadEmbeddedPrompt()
    {
        using var source = typeof(ProjectStore).Assembly.GetManifestResourceStream("Zen.prompt.txt")
            ?? throw new FileNotFoundException("The embedded agent prompt is missing.");
        using var reader = new StreamReader(source);
        return reader.ReadToEnd();
    }

    private ProjectDocument CreateDocument() => new()
    {
        Name = new DirectoryInfo(RootDirectory).Name,
        Branches =
        [
            new BoardColumn { Id = "main", Title = "main", Branch = "main", IsPermanent = true },
            new BoardColumn { Id = "archive", Title = "Archived", Branch = null, IsArchive = true, IsPermanent = true }
        ]
    };

    public static void Normalize(ProjectDocument document)
    {
        document.Branches ??= [];
        document.TagCatalog ??= [];
        var duplicateTags = document.TagCatalog.GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.Skip(1)).ToList();
        foreach (var duplicate in duplicateTags) document.TagCatalog.Remove(duplicate);
        var bugDefinition = document.TagCatalog.FirstOrDefault(tag => tag.Name.Equals("bug", StringComparison.OrdinalIgnoreCase));
        if (bugDefinition is null)
            document.TagCatalog.Add(new ProjectTag { Name = "bug", Color = "#7A2632" });
        else
            bugDefinition.Color = "#7A2632";
        var inProgressDefinition = document.TagCatalog.FirstOrDefault(tag => tag.Name.Equals("in progress", StringComparison.OrdinalIgnoreCase));
        if (inProgressDefinition is null)
            document.TagCatalog.Add(new ProjectTag { Name = "in progress", Color = "#8B7CFF" });
        else
            inProgressDefinition.Color = "#8B7CFF";
        var main = EnsureSystemColumn(document, "main", "main", false);
        main.Branch = "main";
        var archive = EnsureSystemColumn(document, "archive", "Archived", true);
        archive.Branch = null;
        var uncategorized = document.Branches.FirstOrDefault(column => column.Id.Equals("uncategorized", StringComparison.OrdinalIgnoreCase));
        if (uncategorized is not null)
        {
            foreach (var card in uncategorized.Tasks.ToList()) main.Tasks.Add(card);
            document.Branches.Remove(uncategorized);
        }
        var usedIndexes = new HashSet<int>();
        var processedCardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maximumIndex = 0;
        foreach (var column in document.Branches.ToList())
        {
            column.Tasks ??= [];
            if (!column.IsPermanent && !column.IsArchive && string.IsNullOrWhiteSpace(column.Branch))
                column.Branch = CreateBranchName(column.Title, column.Id);
            foreach (var card in column.Tasks.ToList())
            {
                // Completed cards moved into Archive during this pass are encountered
                // again when Archive is visited. Process each immutable identity once.
                if (!processedCardIds.Add(card.Id)) continue;
                card.Tags ??= [];
                card.Files ??= [];
                card.Requirements ??= [];
                card.Flags ??= new CardFlags();
                if (card.Kind == CardKind.Note)
                {
                    card.Tags.Clear();
                    card.Files.Clear();
                    card.Requirements.Clear();
                    card.Flags = new CardFlags();
                    card.DueDate = null;
                    card.StartedDate = null;
                    card.Priority = null;
                }
                else if (card.Kind is CardKind.Break or CardKind.Cleanup)
                {
                    card.Title = string.Empty;
                    card.Task = string.Empty;
                    card.Tags.Clear();
                    card.Files.Clear();
                    card.Requirements.Clear();
                    card.Flags = new CardFlags();
                    card.DueDate = null;
                    card.StartedDate = null;
                    card.Priority = null;
                }
                var normalizedTags = card.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                card.Tags.Clear();
                foreach (var tag in normalizedTags) card.Tags.Add(tag);
                foreach (var tag in card.Tags)
                {
                    if (document.TagCatalog.Any(item => item.Name.Equals(tag, StringComparison.OrdinalIgnoreCase))) continue;
                    document.TagCatalog.Add(new ProjectTag
                    {
                        Name = tag,
                        Color = tag.Equals("bug", StringComparison.OrdinalIgnoreCase)
                            ? "#7A2632"
                            : TagColors[Random.Shared.Next(TagColors.Length)]
                    });
                }
                if (card.Index <= 0 || !usedIndexes.Add(card.Index))
                {
                    card.Index = Math.Max(document.NextCardIndex, maximumIndex + 1);
                    usedIndexes.Add(card.Index);
                }
                maximumIndex = Math.Max(maximumIndex, card.Index);
                if (card.IsDone && column != archive)
                {
                    column.Tasks.Remove(card);
                    archive.Tasks.Add(card);
                }
                else if (column == archive) card.IsDone = true;
            }
            var orderedTasks = column.Tasks.OrderByDescending(card => card.Tags.Contains("bug", StringComparer.OrdinalIgnoreCase)).ToList();
            if (!orderedTasks.SequenceEqual(column.Tasks))
            {
                column.Tasks.Clear();
                foreach (var card in orderedTasks) column.Tasks.Add(card);
            }
        }
        document.NextCardIndex = Math.Max(document.NextCardIndex, maximumIndex + 1);
    }

    private void HydrateFiles(ProjectDocument document)
    {
        foreach (var file in document.Branches.SelectMany(branch => branch.Tasks).SelectMany(card => card.Files))
        {
            var relativePath = file.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var absolutePath = Path.GetFullPath(Path.Combine(RootDirectory, relativePath));
            if (!absolutePath.StartsWith(RootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            file.AbsolutePath = absolutePath;
            file.IsImage = IsImageExtension(Path.GetExtension(file.Name));
        }
    }

    private static void HydrateTags(ProjectDocument document)
    {
        var catalog = document.TagCatalog.ToDictionary(tag => tag.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var card in document.Branches.SelectMany(branch => branch.Tasks))
        {
            card.IsBug = card.Tags.Contains("bug", StringComparer.OrdinalIgnoreCase);
            card.IsInProgress = card.Tags.Contains("in progress", StringComparer.OrdinalIgnoreCase);
            card.TagViews.Clear();
            foreach (var tag in card.Tags)
            {
                var definition = catalog[tag];
                card.TagViews.Add(new TagChip { Name = tag, Color = definition.Color });
            }
        }
    }

    private static bool IsImageExtension(string extension) => extension.ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff";

    public static string CreateBranchName(string title, string? fallbackId = null)
    {
        var result = new string(title.Trim().ToLowerInvariant().Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '/' ? character : '-').ToArray());
        while (result.Contains("--", StringComparison.Ordinal)) result = result.Replace("--", "-", StringComparison.Ordinal);
        result = result.Trim('-', '/', '.');
        var fallback = fallbackId ?? Guid.NewGuid().ToString("N");
        var token = fallback[..Math.Min(8, fallback.Length)];
        return string.IsNullOrWhiteSpace(result) ? $"branch-{token}" : result;
    }

    private static BoardColumn EnsureSystemColumn(ProjectDocument document, string id, string title, bool isArchive)
    {
        var column = document.Branches.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (column is null)
        {
            column = new BoardColumn { Id = id, Title = title, IsArchive = isArchive, IsPermanent = true };
            document.Branches.Add(column);
        }
        column.IsArchive = isArchive;
        column.IsPermanent = true;
        return column;
    }
}
