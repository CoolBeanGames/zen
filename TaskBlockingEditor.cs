using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Zen;

public sealed class TaskBlockingEditor : StackPanel
{
    private static readonly Brush NormalBrush = new SolidColorBrush(Color.FromRgb(244, 246, 250));
    private static readonly Brush ValidBrush = new SolidColorBrush(Color.FromRgb(114, 214, 165));
    private static readonly Brush InvalidBrush = new SolidColorBrush(Color.FromRgb(255, 96, 112));
    private static readonly Brush ArchivedBrush = new SolidColorBrush(Color.FromRgb(119, 126, 140));
    private static readonly Brush FieldBrush = new SolidColorBrush(Color.FromRgb(14, 17, 23));
    private static readonly Brush BorderBrush = new SolidColorBrush(Color.FromRgb(38, 44, 56));
    private readonly TextBox _input;
    private readonly ListBox _suggestions;
    private readonly StackPanel _status;
    private ProjectDocument? _document;
    private TaskCard? _target;
    private Func<string?>? _clusterIdProvider;
    private bool _updating;

    public TaskBlockingEditor()
    {
        _input = new TextBox
        {
            MinHeight = 36,
            Padding = new Thickness(10, 7, 10, 7),
            Background = FieldBrush,
            Foreground = NormalBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CaretBrush = Brushes.White,
            FontSize = 13,
            ToolTip = "Enter numeric task IDs separated by commas"
        };
        _input.TextChanged += Input_TextChanged;
        _input.PreviewTextInput += Input_PreviewTextInput;
        _input.PreviewKeyDown += Input_PreviewKeyDown;
        DataObject.AddPastingHandler(_input, Input_Pasting);
        Children.Add(_input);

        _suggestions = new ListBox
        {
            Visibility = Visibility.Collapsed,
            MaxHeight = 112,
            Background = new SolidColorBrush(Color.FromRgb(32, 37, 47)),
            Foreground = NormalBrush,
            BorderThickness = new Thickness(0),
            DisplayMemberPath = nameof(TaskSuggestion.Display)
        };
        _suggestions.MouseLeftButtonUp += (_, _) => AcceptSuggestion();
        Children.Add(_suggestions);

        _status = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
        Children.Add(_status);
    }

    public string Text
    {
        get => _input.Text;
        set
        {
            _updating = true;
            _input.Text = value;
            _updating = false;
            RefreshValidation();
        }
    }

    public event EventHandler? ValueChanged;

    public void Configure(ProjectDocument document, TaskCard target, Func<string?> clusterIdProvider, IEnumerable<int> initialIds)
    {
        _document = document;
        _target = target;
        _clusterIdProvider = clusterIdProvider;
        Text = string.Join(", ", initialIds);
    }

    public void RefreshValidation()
    {
        _status.Children.Clear();
        if (_document is null || _target is null) return;

        var tokens = _input.Text.Split(',', StringSplitOptions.TrimEntries);
        var numericIds = new List<int>();
        var hasSyntaxError = false;
        foreach (var token in tokens.Where(token => token.Length > 0))
        {
            if (!int.TryParse(token, out var index) || index <= 0)
            {
                AddStatus($"{token} — numeric task IDs only", InvalidBrush);
                hasSyntaxError = true;
            }
            else numericIds.Add(index);
        }

        var results = TaskBlockingRules.Evaluate(_document, _target, numericIds, _clusterIdProvider?.Invoke(), true);
        foreach (var result in results)
        {
            var title = string.IsNullOrWhiteSpace(result.Task?.Title) ? result.Task?.Kind.ToString() ?? "Unknown task" : result.Task.Title;
            var suffix = result.Status == TaskBlockerStatus.Valid ? title : $"{title} · {result.Message}";
            AddStatus($"#{result.Index} — {suffix}", result.Status switch
            {
                TaskBlockerStatus.Valid => ValidBrush,
                TaskBlockerStatus.Archived => ArchivedBrush,
                _ => InvalidBrush
            });
        }

        var anyInvalid = hasSyntaxError || results.Any(result => result.Status is not (TaskBlockerStatus.Valid or TaskBlockerStatus.Archived));
        var archivedOnly = !hasSyntaxError && results.Count > 0 && results.All(result => result.Status == TaskBlockerStatus.Archived);
        _input.Foreground = anyInvalid ? InvalidBrush : archivedOnly ? ArchivedBrush : NormalBrush;
        _input.BorderBrush = anyInvalid ? InvalidBrush : archivedOnly ? ArchivedBrush : BorderBrush;
        UpdateSuggestions();
    }

    public bool TryGetIds(out IReadOnlyList<int> ids, out string error)
    {
        ids = [];
        error = string.Empty;
        try
        {
            ids = TaskBlockingRules.ParseIds(_input.Text);
        }
        catch (FormatException exception)
        {
            error = exception.Message;
            return false;
        }

        if (_document is null || _target is null) return true;
        var invalid = TaskBlockingRules.Evaluate(_document, _target, ids, _clusterIdProvider?.Invoke(), true)
            .FirstOrDefault(result => !result.IsValid);
        if (invalid is null) return true;
        error = invalid.Message;
        return false;
    }

    public void FocusInput() => _input.Focus();

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        RefreshValidation();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Input_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !Regex.IsMatch(e.Text, "^[0-9, ]+$");

    private static void Input_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        var text = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? string.Empty;
        if (!Regex.IsMatch(text, "^[0-9, ]*$")) e.CancelCommand();
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up && _suggestions.Visibility == Visibility.Visible)
        {
            var delta = e.Key == Key.Down ? 1 : -1;
            _suggestions.SelectedIndex = Math.Clamp(_suggestions.SelectedIndex + delta, 0, _suggestions.Items.Count - 1);
            _suggestions.ScrollIntoView(_suggestions.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key is Key.Tab or Key.Enter && _suggestions.Visibility == Visibility.Visible)
        {
            AcceptSuggestion();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) _suggestions.Visibility = Visibility.Collapsed;
    }

    private void UpdateSuggestions()
    {
        if (_document is null || _target is null)
        {
            _suggestions.Visibility = Visibility.Collapsed;
            return;
        }
        var token = _input.Text.Split(',').LastOrDefault()?.Trim() ?? string.Empty;
        if (token.Length == 0 || !token.All(char.IsDigit))
        {
            _suggestions.Visibility = Visibility.Collapsed;
            return;
        }

        var alreadySelected = _input.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SkipLast(1).Select(value => int.TryParse(value, out var parsed) ? parsed : -1).ToHashSet();
        var matches = _document.Branches.SelectMany(branch => branch.Tasks.Select(task => new { branch, task }))
            .Where(item => item.task.Kind == CardKind.Task && item.task.Index != _target.Index &&
                           item.task.Index.ToString().StartsWith(token, StringComparison.Ordinal) &&
                           !alreadySelected.Contains(item.task.Index))
            .OrderBy(item => item.task.Index)
            .Take(8)
            .Select(item => new TaskSuggestion(item.task.Index,
                $"#{item.task.Index}  {item.task.Title}{(item.branch.IsArchive || item.task.IsDone ? "  · archived" : string.Empty)}"))
            .ToList();
        _suggestions.ItemsSource = matches;
        _suggestions.SelectedIndex = matches.Count > 0 ? 0 : -1;
        _suggestions.Visibility = matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AcceptSuggestion()
    {
        if (_suggestions.SelectedItem is not TaskSuggestion suggestion) return;
        var parts = _input.Text.Split(',').ToList();
        if (parts.Count == 0) parts.Add(suggestion.Index.ToString());
        else parts[^1] = suggestion.Index.ToString();
        _input.Text = string.Join(", ", parts.Select(part => part.Trim()).Where(part => part.Length > 0));
        _input.CaretIndex = _input.Text.Length;
        _suggestions.Visibility = Visibility.Collapsed;
        _input.Focus();
    }

    private void AddStatus(string text, Brush color) => _status.Children.Add(new TextBlock
    {
        Text = text,
        Foreground = color,
        FontSize = 10,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(1, 0, 0, 2)
    });

    private sealed record TaskSuggestion(int Index, string Display);
}
