using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Zen;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ColumnKind { Work, Archive }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CardKind { Task, Note, Break }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CardPriority { Low, Normal, High, Critical }

public sealed class ProjectDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string ProjectId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled project";
    public int NextCardIndex { get; set; } = 1;
    public string? LatestExePath { get; set; }
    public string? LatestReleasePath { get; set; }
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
    private bool _isCollapsed;
    private bool _isLocked;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("name")]
    public string Title { get => _title; set => SetField(ref _title, value); }
    public string? Branch { get; set; }
    public bool IsArchive { get; set; }
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (!SetField(ref _isCollapsed, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CollapseGlyph)));
        }
    }
    [JsonPropertyName("isSystem")]
    public bool IsPermanent { get; set; }
    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (!SetField(ref _isLocked, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LockGlyph)));
        }
    }
    [JsonIgnore] public string LockGlyph => IsLocked ? "🔒" : string.Empty;
    public ObservableCollection<TaskCard> Tasks { get; set; } = [];
    [JsonIgnore]
    public ColumnKind Kind
    {
        get => IsArchive ? ColumnKind.Archive : ColumnKind.Work;
        set => IsArchive = value == ColumnKind.Archive;
    }
    [JsonIgnore] public string KindLabel => IsArchive ? "HISTORY" : Branch is null ? "CATEGORY" : $"BRANCH · {Branch}";
    [JsonIgnore] public string KindGlyph => IsArchive ? "↙" : Branch is null ? "◆" : "⑂";
    [JsonIgnore] public string CollapseGlyph => IsCollapsed ? "▾" : "▴";
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public sealed class TaskCard : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _task = string.Empty;
    private bool _isDone;
    private bool _isCollapsed;
    private bool _isLocked;
    private bool _isBug;
    private DateTime? _dueDate;
    private DateTime? _startedDate;
    private CardPriority? _priority;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Index { get; set; }
    public CardKind Kind { get; set; } = CardKind.Task;
    public DateTime? DueDate
    {
        get => _dueDate;
        set { if (SetField(ref _dueDate, value)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDueDate))); }
    }
    public DateTime? StartedDate
    {
        get => _startedDate;
        set { if (SetField(ref _startedDate, value)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasStartedDate))); }
    }
    public CardPriority? Priority
    {
        get => _priority;
        set
        {
            if (!SetField(ref _priority, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPriority)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PriorityLabel)));
        }
    }
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
    [JsonIgnore]
    public bool IsBug
    {
        get => _isBug;
        internal set
        {
            if (_isBug == value) return;
            _isBug = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBug)));
        }
    }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public string IndexLabel => $"#{Index:000}";
    [JsonIgnore] public string KindLabel => Kind switch { CardKind.Note => "NOTE", CardKind.Break => "BREAK", _ => string.Empty };
    [JsonIgnore] public bool IsNote => Kind == CardKind.Note;
    [JsonIgnore] public bool IsBreak => Kind == CardKind.Break;
    [JsonIgnore] public bool HasDueDate => DueDate.HasValue;
    [JsonIgnore] public bool HasStartedDate => StartedDate.HasValue;
    [JsonIgnore] public bool HasPriority => Priority.HasValue;
    [JsonIgnore] public string PriorityLabel => Priority?.ToString().ToUpperInvariant() ?? string.Empty;
    [JsonIgnore] public string RequirementProgress => $"{Requirements.Count(r => r.IsDone)}/{Requirements.Count}";
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        UpdatedAt = DateTimeOffset.UtcNow;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
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
