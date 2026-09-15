using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Data;

namespace Zen;

public sealed class FractionToStarConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fraction = value is double d ? Math.Clamp(d, 0, 1) : 0;
        return new GridLength(Invert ? 1 - fraction : fraction, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ColumnKind { Work, Archive }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CardKind { Task, Note, Break, Cleanup, Merge }

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
    public ObservableCollection<CustomCardDefinition> CustomCardTypes { get; set; } = [];
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
    public TaskCard()
    {
        AttachRequirements(_requirements);
        AttachNotes(_notes);
    }

    private string _title = string.Empty;
    private string _task = string.Empty;
    private bool _isDone;
    private bool _isCollapsed;
    private bool _isLocked;
    private bool _isBug;
    private bool _isInProgress;
    private bool _isAwaitingFeedback;
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
    [JsonIgnore] public ObservableCollection<CustomCompactField> CustomCompactFields { get; } = [];
    [JsonIgnore] public ObservableCollection<CustomCompactField> CustomExpandedFields { get; } = [];
    public ObservableCollection<CardFile> Files { get; set; } = [];
    public string? CustomTypeId { get; set; }
    public Dictionary<string, string> CustomValues { get; set; } = [];
    private ObservableCollection<TaskRequirement> _requirements = [];
    public ObservableCollection<TaskRequirement> Requirements
    {
        get => _requirements;
        set
        {
            if (ReferenceEquals(_requirements, value)) return;
            DetachRequirements(_requirements);
            _requirements = value ?? [];
            AttachRequirements(_requirements);
            RaiseRequirementProgressChanged();
        }
    }
    private ObservableCollection<TaskNote> _notes = [];
    public ObservableCollection<TaskNote> Notes
    {
        get => _notes;
        set
        {
            if (ReferenceEquals(_notes, value)) return;
            DetachNotes(_notes);
            _notes = value ?? [];
            AttachNotes(_notes);
            RaiseNotesChanged();
        }
    }
    public CardFlags Flags { get; set; } = new();
    public bool IsDone { get => _isDone; set => SetField(ref _isDone, value); }
    public bool IsLocked { get => _isLocked; set => SetField(ref _isLocked, value); }
    public bool IsAwaitingFeedback { get => _isAwaitingFeedback; set => SetField(ref _isAwaitingFeedback, value); }
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (!SetField(ref _isCollapsed, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomCardWidth)));
        }
    }
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
    [JsonIgnore]
    public bool IsInProgress
    {
        get => _isInProgress;
        internal set
        {
            if (_isInProgress == value) return;
            _isInProgress = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInProgress)));
        }
    }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public string IndexLabel => $"#{Index:000}";
    [JsonIgnore] public string KindLabel => Kind switch { CardKind.Note => "NOTE", CardKind.Break => "BREAK", CardKind.Cleanup => "CLEANUP", CardKind.Merge => "MERGE", _ => string.Empty };
    [JsonIgnore] public bool IsNote => Kind == CardKind.Note;
    [JsonIgnore] public bool IsBreak => Kind == CardKind.Break;
    [JsonIgnore] public bool IsCleanup => Kind == CardKind.Cleanup;
    [JsonIgnore] public bool IsMerge => Kind == CardKind.Merge;
    [JsonIgnore] public bool HasDueDate => DueDate.HasValue;
    [JsonIgnore] public bool HasStartedDate => StartedDate.HasValue;
    [JsonIgnore] public bool HasPriority => Priority.HasValue;
    [JsonIgnore] public string PriorityLabel => Priority?.ToString().ToUpperInvariant() ?? string.Empty;
    [JsonIgnore] public string RequirementProgress => $"{Requirements.Count(r => r.IsDone)}/{Requirements.Count}";
    [JsonIgnore] public bool HasRequirements => Requirements.Count > 0;
    [JsonIgnore] public double RequirementProgressFraction => Requirements.Count == 0 ? 0 : (double)Requirements.Count(r => r.IsDone) / Requirements.Count;
    [JsonIgnore] public bool HasNotes => Notes.Count > 0;
    [JsonIgnore] public string NotesCountLabel => Notes.Count == 1 ? "1 note" : $"{Notes.Count} notes";
    [JsonIgnore] public bool HasCustomCompactFields => CustomCompactFields.Count > 0;
    [JsonIgnore] public double CompactLayoutHeight { get; internal set; } = 96;
    [JsonIgnore] public bool IsCustomCard => !string.IsNullOrWhiteSpace(CustomTypeId);
    [JsonIgnore] public bool HasCustomExpandedFields => CustomExpandedFields.Count > 0;
    [JsonIgnore] public double ExpandedLayoutHeight { get; internal set; } = 168;
    [JsonIgnore] public double CustomCardWidth => IsCollapsed ? 208 : 244;
    [JsonIgnore] public string CustomBackground { get; internal set; } = "#1D222C";
    [JsonIgnore] public string CustomBorderColor { get; internal set; } = "#2A303D";
    [JsonIgnore] public string CustomHeaderTextColor { get; internal set; } = "#8992A5";
    [JsonIgnore] public Thickness CustomBorderThickness { get; internal set; } = new(1);
    public event PropertyChangedEventHandler? PropertyChanged;

    private void AttachRequirements(ObservableCollection<TaskRequirement> requirements)
    {
        requirements.CollectionChanged += OnRequirementsCollectionChanged;
        foreach (var requirement in requirements) requirement.PropertyChanged += OnRequirementPropertyChanged;
    }

    private void DetachRequirements(ObservableCollection<TaskRequirement> requirements)
    {
        requirements.CollectionChanged -= OnRequirementsCollectionChanged;
        foreach (var requirement in requirements) requirement.PropertyChanged -= OnRequirementPropertyChanged;
    }

    private void OnRequirementsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (TaskRequirement requirement in e.OldItems) requirement.PropertyChanged -= OnRequirementPropertyChanged;
        if (e.NewItems is not null)
            foreach (TaskRequirement requirement in e.NewItems) requirement.PropertyChanged += OnRequirementPropertyChanged;
        RaiseRequirementProgressChanged();
    }

    private void OnRequirementPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskRequirement.IsDone)) RaiseRequirementProgressChanged();
    }

    private void RaiseRequirementProgressChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RequirementProgress)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRequirements)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RequirementProgressFraction)));
    }

    private void AttachNotes(ObservableCollection<TaskNote> notes) => notes.CollectionChanged += OnNotesCollectionChanged;
    private void DetachNotes(ObservableCollection<TaskNote> notes) => notes.CollectionChanged -= OnNotesCollectionChanged;
    private void OnNotesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RaiseNotesChanged();
    private void RaiseNotesChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasNotes)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotesCountLabel)));
    }
    public void NotifyCustomCompactLayoutChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCustomCompactFields)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompactLayoutHeight)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCustomCard)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCustomExpandedFields)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedLayoutHeight)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomBackground)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomBorderColor)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomBorderThickness)));
    }
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
    public int Index { get; set; }
    [JsonIgnore] public string IndexLabel => $"[{Index}]";
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

public sealed class TaskNote : INotifyPropertyChanged
{
    private string _text = string.Empty;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get => _text; set => SetField(ref _text, value); }
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

public sealed class CustomCardDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Custom card";
    public string Instructions { get; set; } = string.Empty;
    public string CardColor { get; set; } = "#1D222C";
    public string OutlineColor { get; set; } = "#2A303D";
    public string HeaderTextColor { get; set; } = "#8992A5";
    public string MainTextColor { get; set; } = "#F4F6FA";
    public string TextBoxColor { get; set; } = "#0E1117";
    public double OutlineWidth { get; set; } = 1;
    public bool ShrinkExpandedToContent { get; set; }
    public bool ShrinkCompactToContent { get; set; }
    public ObservableCollection<CustomFieldDefinition> Fields { get; set; } = [];
}

public sealed class CustomFieldDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Field";
    public string Type { get; set; } = "text";
    public string DefaultValue { get; set; } = string.Empty;
    public ObservableCollection<string> Options { get; set; } = [];
    public bool ShowOnExpanded { get; set; } = true;
    public bool ShowOnCollapsed { get; set; } = true;
    public double X { get; set; } = 24;
    public double Y { get; set; } = 24;
    public double Width { get; set; } = 220;
    public double Height { get; set; } = 72;
    public double ExpandedX { get; set; } = 12;
    public double ExpandedY { get; set; } = 12;
    public double ExpandedWidth { get; set; } = 204;
    public double ExpandedHeight { get; set; } = 60;
    public double CompactX { get; set; } = 12;
    public double CompactY { get; set; } = 12;
    public double CompactWidth { get; set; } = 168;
    public double CompactHeight { get; set; } = 48;
}

public sealed class CustomFieldValue : INotifyPropertyChanged
{
    private string _value = string.Empty;
    public string FieldId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Value { get => _value; set { if (_value == value) return; _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CustomCompactField
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "text";
    public string Value { get; set; } = string.Empty;
    public string HeaderTextColor { get; set; } = "#8992A5";
    public string MainTextColor { get; set; } = "#F4F6FA";
    public string TextBoxColor { get; set; } = "#0E1117";
    public bool IsCompactView { get; set; }
    public string ImagePath { get; set; } = string.Empty;
    public IReadOnlyList<TagChip> TagViews { get; set; } = [];
    public bool HasImagePreview => !string.IsNullOrWhiteSpace(ImagePath);
    public bool HasTags => TagViews.Count > 0;
    public bool HideHeader => Type == "label" || (Type == "list" && IsCompactView);
    public bool IsChecked => bool.TryParse(Value, out var value) && value;
    public string DisplayValue => Type switch
    {
        "label" => Value,
        "list" when IsCompactView => $"{Name}: {CustomListCodec.Parse(Value).Count}",
        "list" => string.Join(Environment.NewLine, CustomListCodec.Parse(Value).Select(item => $"• {item.Replace("\r", " ").Replace("\n", " ")}")),
        "files" => string.IsNullOrWhiteSpace(Value) ? "No files attached" : Value,
        "tags" => string.IsNullOrWhiteSpace(Value) ? "No tags" : Value,
        _ => Value
    };
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public static class CustomListCodec
{
    public static List<string> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        if (value.TrimStart().StartsWith('['))
        {
            try { return JsonSerializer.Deserialize<List<string>>(value) ?? []; }
            catch (JsonException) { }
        }
        return value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public static string Serialize(IEnumerable<string> items) => JsonSerializer.Serialize(items);
}
