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
        Save(document);
        return document;
    }

    public void Save(ProjectDocument document)
    {
        Normalize(document);
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
        using var source = typeof(ProjectStore).Assembly.GetManifestResourceStream("Zen.prompt.txt")
            ?? throw new FileNotFoundException("The embedded agent prompt is missing.");
        using var target = File.Create(destination);
        source.CopyTo(target);
    }

    private ProjectDocument CreateDocument() => new()
    {
        Name = new DirectoryInfo(RootDirectory).Name,
        Branches =
        [
            new BoardColumn { Id = "main", Title = "main", Branch = "main", IsPermanent = true },
            new BoardColumn { Id = "uncategorized", Title = "Uncategorized", Branch = null, IsPermanent = true },
            new BoardColumn { Id = "archive", Title = "Archived", Branch = null, IsArchive = true, IsPermanent = true }
        ]
    };

    public static void Normalize(ProjectDocument document)
    {
        document.Branches ??= [];
        var main = EnsureSystemColumn(document, "main", "main", false);
        main.Branch = "main";
        var uncategorized = EnsureSystemColumn(document, "uncategorized", "Uncategorized", false);
        uncategorized.Branch = null;
        var archive = EnsureSystemColumn(document, "archive", "Archived", true);
        archive.Branch = null;
        var usedIndexes = new HashSet<int>();
        var maximumIndex = 0;
        foreach (var column in document.Branches.ToList())
        {
            column.Tasks ??= [];
            if (!column.IsPermanent && !column.IsArchive && string.IsNullOrWhiteSpace(column.Branch))
                column.Branch = CreateBranchName(column.Title, column.Id);
            foreach (var card in column.Tasks.ToList())
            {
                card.Tags ??= [];
                card.Files ??= [];
                card.Requirements ??= [];
                card.Flags ??= new CardFlags();
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
