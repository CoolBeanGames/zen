using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Zen;

public partial class ClusterEditorWindow : Window
{
    private readonly ClusterDefinition _source;
    private readonly ObservableCollection<TaskRequirement> _requirements = [];
    private readonly ObservableCollection<TaskNote> _notes = [];

    public ClusterDefinition? Result { get; private set; }

    public ClusterEditorWindow(ClusterDefinition source)
    {
        InitializeComponent();
        _source = source;
        NameInput.Text = source.Name;
        DescriptionInput.Text = source.Description;
        TagsInput.Text = string.Join(", ", source.Tags);
        ColorInput.Text = source.Color;
        AwaitingFeedbackInput.IsChecked = source.IsAwaitingFeedback;
        DoNotArchiveInput.IsChecked = source.DoNotArchive;
        LockedInput.IsChecked = source.IsLocked;
        CommitInput.IsChecked = source.Flags.Commit;
        MergeInput.IsChecked = source.Flags.Merge;
        BuildInput.IsChecked = source.Flags.Build;
        ReleaseInput.IsChecked = source.Flags.Release;
        foreach (var item in source.Requirements)
            _requirements.Add(new TaskRequirement { Id = item.Id, Index = item.Index, Text = item.Text, IsDone = item.IsDone });
        foreach (var item in source.Notes)
            _notes.Add(new TaskNote { Id = item.Id, Text = item.Text });
        RequirementsList.ItemsSource = _requirements;
        NotesList.ItemsSource = _notes;
    }

    private void ChooseColor_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ColorPickerWindow(ColorInput.Text) { Owner = this };
        if (picker.ShowDialog() == true) ColorInput.Text = picker.SelectedHex;
    }

    private void AddRequirement_Click(object sender, RoutedEventArgs e) => AddRequirement();
    private void NewRequirementInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { AddRequirement(); e.Handled = true; } }
    private void AddRequirement()
    {
        var text = NewRequirementInput.Text.Trim();
        if (text.Length == 0) return;
        _requirements.Add(new TaskRequirement { Index = _requirements.Count + 1, Text = text });
        NewRequirementInput.Clear();
    }
    private void RemoveRequirement_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TaskRequirement item }) return;
        _requirements.Remove(item);
        for (var index = 0; index < _requirements.Count; index++) _requirements[index].Index = index + 1;
        RequirementsList.Items.Refresh();
    }

    private void AddNote_Click(object sender, RoutedEventArgs e) => AddNote();
    private void NewNoteInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { AddNote(); e.Handled = true; } }
    private void AddNote()
    {
        var text = NewNoteInput.Text.Trim();
        if (text.Length == 0) return;
        _notes.Add(new TaskNote { Text = text });
        NewNoteInput.Clear();
    }
    private void RemoveNote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TaskNote item }) _notes.Remove(item);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameInput.Text.Trim();
        if (name.Length == 0) { ValidationText.Visibility = Visibility.Visible; NameInput.Focus(); return; }
        var color = ColorInput.Text.Trim();
        if (color.Length is not (4 or 5 or 7 or 9) || !color.StartsWith('#') || !color[1..].All(Uri.IsHexDigit)) color = "#514890";
        Result = new ClusterDefinition
        {
            Id = _source.Id,
            Name = name,
            Description = DescriptionInput.Text.Trim(),
            Color = color,
            Tags = new(TagsInput.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)),
            Requirements = new(_requirements.Select(item => new TaskRequirement { Id = item.Id, Index = item.Index, Text = item.Text.Trim(), IsDone = item.IsDone }).Where(item => item.Text.Length > 0)),
            Notes = new(_notes.Select(item => new TaskNote { Id = item.Id, Text = item.Text.Trim() }).Where(item => item.Text.Length > 0)),
            Flags = new CardFlags
            {
                Commit = CommitInput.IsChecked == true,
                Merge = MergeInput.IsChecked == true,
                Build = BuildInput.IsChecked == true,
                Release = ReleaseInput.IsChecked == true
            },
            IsCollapsed = _source.IsCollapsed,
            IsLocked = LockedInput.IsChecked == true,
            IsAwaitingFeedback = AwaitingFeedbackInput.IsChecked == true,
            DoNotArchive = DoNotArchiveInput.IsChecked == true,
            CreatedAt = _source.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
