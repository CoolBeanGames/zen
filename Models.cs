using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Zen;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ColumnKind { Work, Archive }

public sealed class ProjectDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string ProjectId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled project";
    public int NextCardIndex { get; set; } = 1;
    public ObservableCollection<ProjectTag> TagCatalog { get; set; } = [];
    public ObservableCollection<BoardColumn> Branches { get; set; } = [];

    // One-way migration support for project files created by the earlier schema.
    [JsonPropertyName("columns"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObservableCollection<BoardColumn>? LegacyColumns
    {
        get => null;
        set
        {
            if (value is { Count: > 0 } && Branches.Count == 0) Branches = value;
        }
    }
}

public sealed class BoardColumn : INotifyPropertyChanged
{
    private string _title = string.Empty;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("name")]
    public string Title { get => _title; set => SetField(ref _title, value); }
    public string? Branch { get; set; }
    public bool IsArchive { get; set; }
    [JsonPropertyName("isSystem")]
    public bool IsPermanent { get; set; }
    public ObservableCollection<TaskCard> Tasks { get; set; } = [];
    [JsonIgnore]
    public ColumnKind Kind
    {
        get => IsArchive ? ColumnKind.Archive : ColumnKind.Work;
        set => IsArchive = value == ColumnKind.Archive;
    }
    [JsonIgnore] public string KindLabel => IsArchive ? "HISTORY" : Branch is null ? "CATEGORY" : $"BRANCH · {Branch}";
    [JsonIgnore] public string KindGlyph => IsArchive ? "↙" : Branch is null ? "◆" : "⑂";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class TaskCard : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _task = string.Empty;
    private bool _isDone;
    private bool _isCollapsed;
    private bool _isLocked;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Index { get; set; }
    public string Title { get => _title; set => SetField(ref _title, value); }
    public string Task { get => _task; set => SetField(ref _task, value); }
    public ObservableCollection<string> Tags { get; set; } = [];
    [JsonIgnore] public ObservableCollection<TagChip> TagViews { get; } = [];
    public ObservableCollection<CardFile> Files { get; set; } = [];
    public ObservableCollection<TaskRequirement> Requirements { get; set; } = [];
    public CardFlags Flags { get; set; } = new();
    public bool IsDone { get => _isDone; set => SetField(ref _isDone, value); }
    public bool IsLocked { get => _isLocked; set => SetField(ref _isLocked, value); }
    public bool IsCollapsed { get => _isCollapsed; set => SetField(ref _isCollapsed, value); }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public string IndexLabel => $"#{Index:000}";
    [JsonIgnore] public string RequirementProgress => $"{Requirements.Count(r => r.IsDone)}/{Requirements.Count}";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        UpdatedAt = DateTimeOffset.UtcNow;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class ProjectTag
{
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#5B6070";
}

public sealed class TagChip
{
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#5B6070";
}

public sealed class TaskRequirement : INotifyPropertyChanged
{
    private string _text = string.Empty;
    private bool _isDone;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get => _text; set => SetField(ref _text, value); }
    public bool IsDone { get => _isDone; set => SetField(ref _isDone, value); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class CardFile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    [JsonIgnore] public string AbsolutePath { get; set; } = string.Empty;
    [JsonIgnore] public bool IsImage { get; set; }
}

public sealed class CardFlags : INotifyPropertyChanged
{
    private bool _commit, _build, _release, _merge;
    public bool Commit { get => _commit; set => SetField(ref _commit, value); }
    public bool Build { get => _build; set => SetField(ref _build, value); }
    public bool Release { get => _release; set => SetField(ref _release, value); }
    public bool Merge { get => _merge; set => SetField(ref _merge, value); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
