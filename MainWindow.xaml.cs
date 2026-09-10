using System.Collections.ObjectModel;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Zen;

public partial class MainWindow : Window
{
    private BoardColumn? _taskTarget;
    private ModalMode _modalMode;
    private Point _dragStart;
    private Border? _pressedCard;
    private TaskDragPayload? _activeDrag;
    private Border? _dragPreview;
    private Border? _dropIndicator;
    private Border? _highlightedColumn;
    private bool _isBoardPanning;
    private Point _panStart;
    private double _panStartOffset;
    private double _panStartVerticalOffset;
    private readonly DispatcherTimer _edgeScrollTimer;
    private readonly DispatcherTimer _cardClickTimer;
    private readonly DispatcherTimer _projectReloadTimer;
    private readonly DispatcherTimer _periodicReloadTimer;
    private Vector _edgeScrollVelocity;
    private Point _lastDragPointer;
    private TaskCard? _pendingClickCard;
    private TaskCard? _openEditorOnReleaseFor;
    private TaskCard? _editingCard;
    private int _nextTaskIndex = 4;
    private readonly ObservableCollection<TaskRequirement> _editingRequirements = [];
    private readonly ObservableCollection<string> _editingFileNames = [];
    private readonly List<string> _pendingUploadPaths = [];
    private readonly List<string> _temporaryClipboardPaths = [];
    private ProjectDocument? _document;
    private ProjectStore? _store;
    private readonly RecentProjectStore _recentProjects = new();
    private readonly SettingsStore _settingsStore = new();
    private FileSystemWatcher? _projectWatcher;
    private DateTime _ignoreFileEventsUntil;
    private CardKind _newCardKind = CardKind.Task;
    private BoardColumn? _pressedColumn;
    private Point _columnDragStart;
    private bool _isColumnDragging;
    private bool _manualReloadRequested;

    public ObservableCollection<BoardColumn> Columns { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _edgeScrollTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _edgeScrollTimer.Tick += EdgeScrollTimer_Tick;
        _cardClickTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(250, GetDoubleClickTime() + 30))
        };
        _cardClickTimer.Tick += CardClickTimer_Tick;
        _projectReloadTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _projectReloadTimer.Tick += ProjectReloadTimer_Tick;
        _periodicReloadTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(_settingsStore.Load().ReloadSeconds)
        };
        _periodicReloadTimer.Tick += PeriodicReloadTimer_Tick;
        try { PromptEnvironment.Sync(_settingsStore, _settingsStore.Load()); } catch { /* PATH is best-effort */ }
        SeedBoard();
        Loaded += (_, _) => OpenLastProject();
    }

    private void OpenLastProject()
    {
        var lastProject = _recentProjects.Load().FirstOrDefault();
        if (lastProject is not null) OpenProjectFolder(lastProject.Path);
    }

    private void SeedBoard()
    {
        Columns.Add(new BoardColumn
        {
            Id = "main", Title = "main", Branch = "main", IsPermanent = true,
            Tasks =
            {
                new TaskCard { Index = 1, Title = "Shape the project workspace", Task = "Establish the board shell and interaction model.", Tags = ["foundation"] },
                new TaskCard { Index = 2, Title = "Define the first data model", Task = "Decide how branches, tasks, and personal categories persist.", Tags = ["next"] }
            }
        });
        Columns.Add(new BoardColumn
        {
            Id = "archive", Title = "Archived", Branch = null, IsArchive = true, IsPermanent = true
        });
    }

    private void ProjectMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ProjectMenuButton, Placement = PlacementMode.Bottom };
        foreach (var project in _recentProjects.Load())
        {
            var item = new MenuItem { Header = project.Name, ToolTip = project.Path, Tag = project.Path };
            item.Click += KnownProject_Click;
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var create = new MenuItem { Header = "＋  New project…" };
        create.Click += NewProject_Click;
        menu.Items.Add(create);
        menu.IsOpen = true;
    }

    private void KnownProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string path }) OpenProjectFolder(path);
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Choose a Zen project folder",
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;
        OpenProjectFolder(picker.FolderName);
    }

    private void OpenProjectFolder(string folderPath)
    {
        try
        {
            var store = new ProjectStore(folderPath);
            var document = store.OpenOrCreate();
            _store = store;
            ApplyDocument(document);
            StartProjectWatcher();
            _recentProjects.Remember(store.RootDirectory, document.Name);
            ReloadButton.IsEnabled = true;
            LaunchProjectButton.IsEnabled = true;
            _periodicReloadTimer.Start();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not open project", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveProject()
    {
        if (_store is null || _document is null) return;
        _document.NextCardIndex = _nextTaskIndex;
        try
        {
            _ignoreFileEventsUntil = DateTime.UtcNow.AddSeconds(1);
            _store.Save(_document);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not save project", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyDocument(ProjectDocument document)
    {
        _document = document;
        Columns.Clear();
        foreach (var column in document.Branches) Columns.Add(column);
        document.Branches = Columns;
        _nextTaskIndex = document.NextCardIndex;
        ProjectNameText.Text = document.Name;
        ProjectNameText.ToolTip = _store?.RootDirectory;
        UpdateExecutableButtons();
    }

    private void UpdateExecutableButtons()
    {
        SetExecutableButtonState(LaunchLatestExeButton, _document?.LatestExePath, "build");
        SetExecutableButtonState(LaunchLatestReleaseButton, _document?.LatestReleasePath, "release");
    }

    private void SetExecutableButtonState(Button button, string? configuredPath, string kind)
    {
        button.IsEnabled = _store is not null && !string.IsNullOrWhiteSpace(configuredPath);
        button.ToolTip = button.IsEnabled
            ? $"Launch {configuredPath}"
            : $"No {kind} executable has been recorded";
    }

    private void LaunchLatestExe_Click(object sender, RoutedEventArgs e) => LaunchConfiguredExecutable(false);

    private void LaunchLatestRelease_Click(object sender, RoutedEventArgs e) => LaunchConfiguredExecutable(true);

    private void LaunchConfiguredExecutable(bool release)
    {
        if (_store is null || _document is null) return;
        var configuredPath = release ? _document.LatestReleasePath : _document.LatestExePath;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            UpdateExecutableButtons();
            return;
        }

        string executablePath;
        try
        {
            executablePath = Path.IsPathRooted(configuredPath)
                ? Path.GetFullPath(configuredPath)
                : Path.GetFullPath(Path.Combine(_store.RootDirectory, configuredPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ClearConfiguredExecutablePath(release, "The saved executable path is invalid. It was removed from the project.");
            return;
        }

        if (!File.Exists(executablePath))
        {
            ClearConfiguredExecutablePath(release, "The saved executable no longer exists. Its path was removed from the project.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? _store.RootDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Could not launch the executable: {exception.Message}",
                "Launch failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearConfiguredExecutablePath(bool release, string message)
    {
        if (_document is null) return;
        if (release) _document.LatestReleasePath = null;
        else _document.LatestExePath = null;
        SaveProject();
        UpdateExecutableButtons();
        MessageBox.Show(this, message, "Executable not found", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void StartProjectWatcher()
    {
        _projectWatcher?.Dispose();
        if (_store is null) return;
        _projectWatcher = new FileSystemWatcher(_store.RootDirectory, ProjectStore.DataFileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _projectWatcher.Changed += ProjectFileChanged;
        _projectWatcher.Created += ProjectFileChanged;
        _projectWatcher.Renamed += ProjectFileChanged;
    }

    private void ProjectFileChanged(object sender, FileSystemEventArgs e)
    {
        if (DateTime.UtcNow < _ignoreFileEventsUntil) return;
        Dispatcher.BeginInvoke(() =>
        {
            _projectReloadTimer.Stop();
            _projectReloadTimer.Start();
        });
    }

    private void ProjectReloadTimer_Tick(object? sender, EventArgs e)
    {
        _projectReloadTimer.Stop();
        if (_store is null || _activeDrag is not null || CardEditorHost.Visibility == Visibility.Visible || ModalScrim.Visibility == Visibility.Visible)
        {
            _projectReloadTimer.Start();
            return;
        }

        try
        {
            _ignoreFileEventsUntil = DateTime.UtcNow.AddSeconds(1);
            var horizontalOffset = BoardScroller.HorizontalOffset;
            var verticalOffset = BoardScroller.VerticalOffset;
            ApplyDocument(_store.OpenOrCreate());
            Dispatcher.BeginInvoke(() =>
            {
                BoardScroller.ScrollToHorizontalOffset(horizontalOffset);
                BoardScroller.ScrollToVerticalOffset(verticalOffset);
            });
        }
        catch (IOException)
        {
            _projectReloadTimer.Start();
        }
        catch (JsonException exception)
        {
            // Editors and agents may replace the JSON over several filesystem
            // events. Keep the last valid board visible and quietly retry instead
            // of opening one warning window per intermediate write.
            _projectReloadTimer.Start();
            if (_manualReloadRequested)
                MessageBox.Show(this, exception.Message, "Invalid task data", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _manualReloadRequested = false;
        }
    }

    private void PeriodicReloadTimer_Tick(object? sender, EventArgs e)
    {
        _projectReloadTimer.Stop();
        _projectReloadTimer.Start();
    }

    private void ReloadProject_Click(object sender, RoutedEventArgs e)
    {
        _projectReloadTimer.Stop();
        _manualReloadRequested = true;
        ProjectReloadTimer_Tick(sender, EventArgs.Empty);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_recentProjects) { Owner = this };
        if (window.ShowDialog() == true)
            _periodicReloadTimer.Interval = TimeSpan.FromSeconds(window.Settings.ReloadSeconds);
    }

    private void LaunchProject_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = LaunchProjectButton, Placement = PlacementMode.Bottom };
        menu.Items.Add(CreateLaunchMenu("Process all eligible tasks in zen.tasks.json branch by branch."));
        menu.IsOpen = true;
    }

    private MenuItem CreateLaunchMenu(string scopeInstruction)
    {
        var launch = new MenuItem { Header = "Launch" };
        AddMenuItem(launch, "Codex", (_, _) => LaunchAgent("codex", scopeInstruction));
        AddMenuItem(launch, "Claude", (_, _) => LaunchAgent("claude", scopeInstruction));
        return launch;
    }

    private void LaunchAgent(string agent, string scopeInstruction)
    {
        if (_store is null) return;
        var instruction = File.Exists(PromptEnvironment.PromptPath)
            ? $"Ignore any prompt.txt inside the project folder. Read only the current canonical instructions at \"{PromptEnvironment.PromptPath}\", then {scopeInstruction}"
            : $"Read prompt.txt, then {scopeInstruction}";
        var command = agent == "codex"
            ? $"codex --dangerously-bypass-approvals-and-sandbox \"{instruction.Replace("\"", "\\\"")}\""
            : $"claude --dangerously-skip-permissions \"{instruction.Replace("\"", "\\\"")}\"";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k {command}",
                WorkingDirectory = _store.RootDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Could not launch {agent}: {exception.Message}", "Agent launch failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void NewColumn_Click(object sender, RoutedEventArgs e) => OpenModal(ModalMode.Column);

    private void AddTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BoardColumn column })
            ShowAddMenu((Button)sender, column);
    }

    private void ShowAddMenu(FrameworkElement anchor, BoardColumn column)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        AddCardTypeItem(menu, "Task", CardKind.Task, column);
        AddCardTypeItem(menu, "Note — information only", CardKind.Note, column);
        AddCardTypeItem(menu, "Break — stop agents here", CardKind.Break, column);
        AddCardTypeItem(menu, "Bug — priority task", CardKind.Task, column, true);
        menu.IsOpen = true;
    }

    private void AddCardTypeItem(ItemsControl menu, string header, CardKind kind, BoardColumn column, bool bug = false)
    {
        var item = new MenuItem { Header = header, Tag = new NewCardRequest(column, kind, bug) };
        item.Click += NewCardType_Click;
        menu.Items.Add(item);
    }

    private void NewCardType_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: NewCardRequest request }) return;
        // A background reload can rebuild the column collection between opening this
        // menu and clicking an item, leaving request.Column detached from the board.
        // Re-resolve by stable id so the new card is never added to an orphan column.
        var target = ResolveColumn(request.Column);
        if (target is null) return;
        _taskTarget = target;
        _newCardKind = request.Kind;
        if (request.Kind == CardKind.Break)
        {
            target.Tasks.Add(new TaskCard { Index = _nextTaskIndex++, Kind = CardKind.Break });
            SaveProject();
            _taskTarget = null;
            return;
        }
        OpenModal(ModalMode.Task, request.Bug);
    }

    // Map a possibly-stale column reference back to the live instance on the board.
    private BoardColumn? ResolveColumn(BoardColumn? column)
    {
        if (column is null) return null;
        return Columns.FirstOrDefault(candidate => ReferenceEquals(candidate, column))
            ?? Columns.FirstOrDefault(candidate => candidate.Id.Equals(column.Id, StringComparison.OrdinalIgnoreCase));
    }

    private void TaskCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: TaskCard task } card) return;
        if (e.ClickCount > 1)
        {
            _cardClickTimer.Stop();
            _pendingClickCard = null;
            _pressedCard?.ReleaseMouseCapture();
            _pressedCard = null;
            _openEditorOnReleaseFor = task;
            e.Handled = true;
            return;
        }
        if (task.IsLocked) return;
        _pressedCard = card;
        _dragStart = e.GetPosition(card);
        card.CaptureMouse();
    }

    private void TaskCard_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: TaskCard card }) return;

        if (_openEditorOnReleaseFor == card)
        {
            _openEditorOnReleaseFor = null;
            OpenCardEditor(card, (Border)sender);
            e.Handled = true;
            return;
        }

        _pendingClickCard = card;
        _cardClickTimer.Stop();
        _cardClickTimer.Start();
        e.Handled = true;
    }

    private void TaskCard_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: TaskCard card }) return;
        _cardClickTimer.Stop();
        _pendingClickCard = null;
        ShowCardMenu((Border)sender, card);
        e.Handled = true;
    }

    private bool CanAttachToCard(TaskCard card)
    {
        var column = Columns.FirstOrDefault(candidate => candidate.Tasks.Contains(card));
        return _store is not null && !card.IsLocked && !card.IsNote && !card.IsBreak &&
               column is { IsLocked: false, IsArchive: false };
    }

    private void TaskCard_DragEnter(object sender, DragEventArgs e) => UpdateFileDropTarget(sender, e);

    private void TaskCard_DragOver(object sender, DragEventArgs e) => UpdateFileDropTarget(sender, e);

    private void UpdateFileDropTarget(object sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: TaskCard card } border || !CanAttachToCard(card) ||
            !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        e.Effects = paths?.Any(File.Exists) == true ? DragDropEffects.Copy : DragDropEffects.None;
        if (e.Effects == DragDropEffects.Copy)
        {
            border.BorderBrush = (Brush)FindResource("AccentBrush");
            border.BorderThickness = new Thickness(2);
        }
        e.Handled = true;
    }

    private void TaskCard_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.ClearValue(Border.BorderBrushProperty);
            border.ClearValue(Border.BorderThicknessProperty);
        }
        e.Handled = true;
    }

    private void TaskCard_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: TaskCard card } border) return;
        border.ClearValue(Border.BorderBrushProperty);
        border.ClearValue(Border.BorderThicknessProperty);
        e.Handled = true;
        if (!CanAttachToCard(card) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        try
        {
            foreach (var path in paths.Where(File.Exists))
                card.Files.Add(_store!.ImportFile(path));
            SaveProject();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not attach file", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowCardMenu(Border anchor, TaskCard card)
    {
        var column = Columns.First(c => c.Tasks.Contains(card));
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.MousePoint };
        menu.Items.Add(CreateNewCardMenu(column));
        AddMenuItem(menu, "Edit", (_, _) => OpenCardEditor(card, anchor));
        AddMenuItem(menu, "Delete", (_, _) => DeleteCard(card));
        var move = new MenuItem { Header = "Move to" };
        foreach (var destination in Columns.Where(candidate => candidate != column))
            AddMenuItem(move, destination.Title, (_, _) => MoveCard(card, column, destination));
        menu.Items.Add(move);
        menu.Items.Add(new Separator());
        AddMenuItem(menu, card.IsCollapsed ? "Expand" : "Collapse", (_, _) => { card.IsCollapsed = !card.IsCollapsed; SaveProject(); });
        AddMenuItem(menu, card.IsLocked ? "Unlock" : "Lock", (_, _) => { card.IsLocked = !card.IsLocked; SaveProject(); });
        AddMenuItem(menu, card.IsBug ? "Remove bug flag" : "Mark as bug", (_, _) => ToggleBug(card));
        if (column.IsLocked)
            menu.Items.Add(new MenuItem { Header = "Launch — branch locked", IsEnabled = false });
        else
            menu.Items.Add(CreateLaunchMenu($"Process only task #{card.Index} ({card.Id}) in branch '{column.Branch ?? column.Title}'."));
        menu.IsOpen = true;
    }

    private static void AddMenuItem(ItemsControl parent, string header, RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = header };
        item.Click += handler;
        parent.Items.Add(item);
    }

    private MenuItem CreateNewCardMenu(BoardColumn column)
    {
        var add = new MenuItem { Header = "New card" };
        AddCardTypeItem(add, "Task", CardKind.Task, column);
        AddCardTypeItem(add, "Note — information only", CardKind.Note, column);
        AddCardTypeItem(add, "Break — stop agents here", CardKind.Break, column);
        AddCardTypeItem(add, "Bug — priority task", CardKind.Task, column, true);
        return add;
    }

    private void DeleteCard(TaskCard card)
    {
        if (MessageBox.Show(this, $"Delete #{card.Index:000} · {card.Title}? Attached files will remain.", "Delete card", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Columns.FirstOrDefault(column => column.Tasks.Contains(card))?.Tasks.Remove(card);
        SaveProject();
    }

    private void MoveCard(TaskCard card, BoardColumn source, BoardColumn destination)
    {
        if (!source.Tasks.Remove(card)) return;
        card.IsDone = destination.IsArchive;
        destination.Tasks.Add(card);
        SaveProject();
    }

    private void ToggleBug(TaskCard card)
    {
        var existing = card.Tags.FirstOrDefault(tag => tag.Equals("bug", StringComparison.OrdinalIgnoreCase));
        if (existing is null) card.Tags.Insert(0, "bug"); else card.Tags.Remove(existing);
        SaveProject();
    }

    private void CardClickTimer_Tick(object? sender, EventArgs e)
    {
        _cardClickTimer.Stop();
        if (_pendingClickCard is not null)
        {
            _pendingClickCard.IsCollapsed = !_pendingClickCard.IsCollapsed;
            SaveProject();
        }
        _pendingClickCard = null;
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isBoardPanning || e.LeftButton != MouseButtonState.Pressed)
            return;

        if (_pressedCard is null && _pressedColumn is not null)
        {
            var columnPointer = e.GetPosition(RootLayout);
            if (!_isColumnDragging && Math.Abs(columnPointer.X - _columnDragStart.X) < SystemParameters.MinimumHorizontalDragDistance)
                return;
            if (!_isColumnDragging)
            {
                _isColumnDragging = true;
                Mouse.Capture(RootLayout, CaptureMode.SubTree);
            }
            var columnTarget = FindColumnAt(columnPointer)?.Column;
            if (columnTarget is not null && columnTarget != _pressedColumn)
            {
                var targetIndex = Columns.IndexOf(columnTarget);
                Columns.Move(Columns.IndexOf(_pressedColumn), targetIndex);
            }
            RootLayout.Cursor = Cursors.SizeWE;
            e.Handled = true;
            return;
        }

        if (_activeDrag is null)
        {
            if (_pressedCard is null) return;
            var current = e.GetPosition(_pressedCard);
            if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            BeginCardDrag(_pressedCard);
        }

        if (_activeDrag is null) return;
        var pointer = e.GetPosition(RootLayout);
        _lastDragPointer = pointer;
        MoveDragPreview(pointer);
        UpdateEdgeScroll(pointer);
        var target = FindColumnAt(pointer);
        if (target is null)
            ClearColumnHighlight();
        else
            HighlightColumn(target.Value.Border);
        UpdateDropIndicator(pointer, target);
        e.Handled = true;
    }

    // Collect the card template roots inside a column, top to bottom. Matching on
    // TemplatedParent skips the inner decoration borders that inherit the same
    // TaskCard DataContext.
    private static void CollectCardBorders(DependencyObject node, List<Border> into)
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is Border { DataContext: TaskCard, TemplatedParent: ContentPresenter } border)
                into.Add(border);
            else
                CollectCardBorders(child, into);
        }
    }

    // Resolve the drop slot from the pointer's Y against real card bounds so the
    // indicator does not flicker in the gaps between cards.
    private (double Left, double Width, double Top)? ResolveDropSlot(Border columnBorder, Point pointer)
    {
        var cards = new List<Border>();
        CollectCardBorders(columnBorder, cards);
        if (cards.Count == 0)
        {
            var columnOrigin = columnBorder.TranslatePoint(new Point(0, 0), RootLayout);
            return (columnOrigin.X + 13, Math.Max(0, columnBorder.ActualWidth - 26), columnOrigin.Y + columnBorder.ActualHeight - 6);
        }

        foreach (var card in cards)
        {
            if (card.ActualHeight <= 0) continue;
            var origin = card.TranslatePoint(new Point(0, 0), RootLayout);
            var after = pointer.Y > origin.Y + card.ActualHeight / 2;
            if (pointer.Y < origin.Y + card.ActualHeight || card == cards[^1])
                return (origin.X, card.ActualWidth, (after ? origin.Y + card.ActualHeight : origin.Y) - 3);
        }
        return null;
    }

    private void UpdateDropIndicator(Point pointer, (Border Border, BoardColumn Column)? column)
    {
        if (_activeDrag is null || column is null)
        {
            if (_dropIndicator is not null) _dropIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        var slot = ResolveDropSlot(column.Value.Border, pointer);
        if (slot is null)
        {
            if (_dropIndicator is not null) _dropIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        _dropIndicator ??= CreateDropIndicator();
        DragOverlay.Visibility = Visibility.Visible;
        _dropIndicator.Width = slot.Value.Width;
        Canvas.SetLeft(_dropIndicator, Math.Round(slot.Value.Left));
        Canvas.SetTop(_dropIndicator, Math.Round(slot.Value.Top));
        _dropIndicator.Visibility = Visibility.Visible;
    }

    private Border CreateDropIndicator()
    {
        var indicator = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = (Brush)FindResource("AccentBrush"),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.5 }
        };
        DragOverlay.Children.Add(indicator);
        return indicator;
    }

    private void BeginCardDrag(Border card)
    {
        if (card.DataContext is not TaskCard task) return;
        var source = Columns.FirstOrDefault(column => column.Tasks.Contains(task));
        if (source is null) return;

        _cardClickTimer.Stop();
        _pendingClickCard = null;

        card.ReleaseMouseCapture();
        _activeDrag = new TaskDragPayload(task, source, source.Tasks.IndexOf(task), card);
        CreateDragPreview(card);
        MoveDragPreview(Mouse.GetPosition(RootLayout));

        card.Opacity = 0.16;
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        card.RenderTransform = new ScaleTransform(0.98, 0.98);
        RootLayout.Cursor = Cursors.Hand;
        Mouse.Capture(RootLayout, CaptureMode.SubTree);
    }

    private void CreateDragPreview(Border card)
    {
        // A single dedicated overlay is deterministic: clearing it can never leave
        // old preview adorners attached to disconnected card visuals.
        DragOverlay.Children.Clear();
        var width = Math.Max(1, (int)Math.Ceiling(card.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(card.ActualHeight));
        var snapshot = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        snapshot.Render(card);

        _dragPreview = new Border
        {
            Width = card.ActualWidth,
            Height = card.ActualHeight,
            CornerRadius = new CornerRadius(10),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(1.5),
            Background = (Brush)FindResource("CardBrush"),
            RenderTransform = new ScaleTransform(1.025, 1.025),
            RenderTransformOrigin = new Point(0.5, 0.5),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 24,
                ShadowDepth = 10,
                Opacity = 0.65
            },
            Child = new Image { Source = snapshot, Stretch = Stretch.Fill }
        };
        DragOverlay.Children.Add(_dragPreview);
        DragOverlay.Visibility = Visibility.Visible;
    }

    private void MoveDragPreview(Point pointer)
    {
        if (_dragPreview is null) return;
        Canvas.SetLeft(_dragPreview, pointer.X - _dragStart.X + 10);
        Canvas.SetTop(_dragPreview, pointer.Y - _dragStart.Y - 8);
    }

    private void UpdateEdgeScroll(Point pointer)
    {
        const double edgeZone = 72;
        const double minimumSpeed = 3;
        const double maximumSpeed = 22;
        var viewportOrigin = BoardScroller.TranslatePoint(new Point(0, 0), RootLayout);
        var localX = pointer.X - viewportOrigin.X;
        var localY = pointer.Y - viewportOrigin.Y;
        var horizontal = 0d;
        var vertical = 0d;

        if (localX >= 0 && localX < edgeZone && BoardScroller.HorizontalOffset > 0)
            horizontal = -EdgeSpeed(localX, edgeZone, minimumSpeed, maximumSpeed);
        else if (localX <= BoardScroller.ActualWidth && localX > BoardScroller.ActualWidth - edgeZone &&
                 BoardScroller.HorizontalOffset < BoardScroller.ScrollableWidth)
            horizontal = EdgeSpeed(BoardScroller.ActualWidth - localX, edgeZone, minimumSpeed, maximumSpeed);

        if (localY >= 0 && localY < edgeZone && BoardScroller.VerticalOffset > 0)
            vertical = -EdgeSpeed(localY, edgeZone, minimumSpeed, maximumSpeed);
        else if (localY <= BoardScroller.ActualHeight && localY > BoardScroller.ActualHeight - edgeZone &&
                 BoardScroller.VerticalOffset < BoardScroller.ScrollableHeight)
            vertical = EdgeSpeed(BoardScroller.ActualHeight - localY, edgeZone, minimumSpeed, maximumSpeed);

        _edgeScrollVelocity = new Vector(horizontal, vertical);
        if (_edgeScrollVelocity.LengthSquared > 0)
        {
            if (!_edgeScrollTimer.IsEnabled) _edgeScrollTimer.Start();
        }
        else
        {
            _edgeScrollTimer.Stop();
        }
    }

    private static double EdgeSpeed(double distance, double zone, double minimum, double maximum)
    {
        var intensity = 1 - Math.Clamp(distance / zone, 0, 1);
        return minimum + ((maximum - minimum) * intensity * intensity);
    }

    private void EdgeScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (_activeDrag is null)
        {
            _edgeScrollTimer.Stop();
            return;
        }

        BoardScroller.ScrollToHorizontalOffset(BoardScroller.HorizontalOffset + _edgeScrollVelocity.X);
        BoardScroller.ScrollToVerticalOffset(BoardScroller.VerticalOffset + _edgeScrollVelocity.Y);
        MoveDragPreview(_lastDragPointer);

        var target = FindColumnAt(_lastDragPointer);
        if (target is null) ClearColumnHighlight();
        else HighlightColumn(target.Value.Border);
        UpdateDropIndicator(_lastDragPointer, target);
        UpdateEdgeScroll(_lastDragPointer);
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedColumn is not null)
        {
            var changed = _isColumnDragging;
            _pressedColumn = null;
            _isColumnDragging = false;
            RootLayout.ClearValue(CursorProperty);
            if (Mouse.Captured == RootLayout) Mouse.Capture(null);
            if (changed) SaveProject();
            if (changed) e.Handled = true;
        }
        if (_activeDrag is not null)
        {
            var drag = _activeDrag;
            var pointer = e.GetPosition(RootLayout);
            var target = FindColumnAt(pointer);
            var cardDrop = FindCardDropAt(pointer);
            EndCardDrag();
            if (target is not null && drag.Source.Tasks.Remove(drag.Task))
            {
                drag.Task.IsDone = target.Value.Column.Kind == ColumnKind.Archive;
                var insertionIndex = cardDrop?.Card == drag.Task
                    ? Math.Min(drag.SourceIndex, target.Value.Column.Tasks.Count)
                    : cardDrop is null ? target.Value.Column.Tasks.Count : target.Value.Column.Tasks.IndexOf(cardDrop.Value.Card) + (cardDrop.Value.After ? 1 : 0);
                target.Value.Column.Tasks.Insert(Math.Clamp(insertionIndex, 0, target.Value.Column.Tasks.Count), drag.Task);
                SaveProject();
            }
            e.Handled = true;
        }
        else if (_pressedCard is not null)
        {
            _pressedCard.ReleaseMouseCapture();
            _pressedCard = null;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _activeDrag is not null)
        {
            EndCardDrag();
            e.Handled = true;
        }
    }

    private void Window_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_activeDrag is not null && Mouse.Captured != RootLayout)
            EndCardDrag();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_activeDrag is not null)
            EndCardDrag();
        if (_pressedColumn is not null)
        {
            _pressedColumn = null;
            _isColumnDragging = false;
            RootLayout.ClearValue(CursorProperty);
            if (Mouse.Captured == RootLayout) Mouse.Capture(null);
        }
    }

    private (Border Border, BoardColumn Column)? FindColumnAt(Point point)
    {
        var element = RootLayout.InputHitTest(point) as DependencyObject;
        while (element is not null)
        {
            if (element is Border { DataContext: BoardColumn column } border)
                return (border, column);
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private (TaskCard Card, bool After)? FindCardDropAt(Point point)
    {
        var element = RootLayout.InputHitTest(point) as DependencyObject;
        while (element is not null)
        {
            if (element is Border { DataContext: TaskCard card } border)
                return (card, point.Y > border.TranslatePoint(new Point(0, border.ActualHeight / 2), RootLayout).Y);
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void EndCardDrag()
    {
        if (_activeDrag is not null)
        {
            _activeDrag.SourceElement.Opacity = 1;
            _activeDrag.SourceElement.RenderTransform = Transform.Identity;
        }
        ClearColumnHighlight();
        DragOverlay.Children.Clear();
        DragOverlay.Visibility = Visibility.Collapsed;
        _dragPreview = null;
        _dropIndicator = null;
        _edgeScrollVelocity = default;
        _edgeScrollTimer.Stop();
        _activeDrag = null;
        _pressedCard = null;
        RootLayout.ClearValue(CursorProperty);
        if (Mouse.Captured is not null) Mouse.Capture(null);
    }

    private void HighlightColumn(Border column)
    {
        if (_highlightedColumn == column) return;
        ClearColumnHighlight();
        _highlightedColumn = column;
        column.BorderBrush = (Brush)FindResource("AccentBrush");
        column.BorderThickness = new Thickness(2);
        column.Background = (Brush)FindResource("AccentSoftBrush");
    }

    private void ClearColumnHighlight()
    {
        if (_highlightedColumn is null) return;
        _highlightedColumn.ClearValue(Border.BorderBrushProperty);
        _highlightedColumn.ClearValue(Border.BorderThicknessProperty);
        _highlightedColumn.ClearValue(Border.BackgroundProperty);
        _highlightedColumn = null;
    }

    private void OpenCardEditor(TaskCard card, Border anchor)
    {
        EndCardDragIfActive();
        if (card.IsBreak) return;
        _editingCard = card;
        EditIdentityText.Text = $"{card.IndexLabel}  ·  ID {card.Id.ToUpperInvariant()}";
        EditTitleInput.Text = card.Title;
        EditTagsInput.Text = string.Join(", ", card.Tags.Where(tag =>
            !tag.Equals("bug", StringComparison.OrdinalIgnoreCase) &&
            !tag.Equals("in progress", StringComparison.OrdinalIgnoreCase)));
        EditBugFlag.IsChecked = card.IsBug;
        EditInProgressFlag.IsChecked = card.Tags.Contains("in progress", StringComparer.OrdinalIgnoreCase);
        EditTaskInput.Text = card.Task;
        EditPromptLabel.Text = card.IsNote ? "NOTE" : "TASK PROMPT";
        EditAdvancedFields.Visibility = card.IsNote ? Visibility.Collapsed : Visibility.Visible;
        _editingRequirements.Clear();
        foreach (var requirement in card.Requirements)
            _editingRequirements.Add(new TaskRequirement { Id = requirement.Id, Text = requirement.Text, IsDone = requirement.IsDone });
        EditRequirementsList.ItemsSource = _editingRequirements;
        NewRequirementInput.Text = string.Empty;
        _pendingUploadPaths.Clear();
        _editingFileNames.Clear();
        foreach (var file in card.Files) _editingFileNames.Add(file.Name);
        EditFilesList.ItemsSource = _editingFileNames;
        EditCommitFlag.IsChecked = card.Flags.Commit;
        EditBuildFlag.IsChecked = card.Flags.Build;
        EditReleaseFlag.IsChecked = card.Flags.Release;
        EditMergeFlag.IsChecked = card.Flags.Merge;
        EditStartedDate.SelectedDate = card.StartedDate;
        EditDueDate.SelectedDate = card.DueDate;
        EditPriority.SelectedIndex = card.Priority.HasValue ? (int)card.Priority.Value + 1 : 0;
        EditValidationText.Visibility = Visibility.Collapsed;
        EditorShell.Height = Math.Max(360, RootLayout.ActualHeight - 48);
        EditorShell.MaxHeight = EditorShell.Height;
        CardEditorHost.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() =>
        {
            EditTitleInput.Focus();
            EditTitleInput.SelectAll();
        });
    }

    private void ApplyCardEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_editingCard is null)
        {
            CloseCardEditor();
            return;
        }

        var title = EditTitleInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            EditValidationText.Visibility = Visibility.Visible;
            EditTitleInput.Focus();
            return;
        }

        _editingCard.Title = title;
        _editingCard.Task = EditTaskInput.Text.Trim();
        if (!_editingCard.IsNote)
        {
            _editingCard.Tags.Clear();
            if (EditBugFlag.IsChecked == true) _editingCard.Tags.Add("bug");
            if (EditInProgressFlag.IsChecked == true) _editingCard.Tags.Add("in progress");
            foreach (var tag in EditTagsInput.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Where(tag => !tag.Equals("bug", StringComparison.OrdinalIgnoreCase) &&
                                       !tag.Equals("in progress", StringComparison.OrdinalIgnoreCase))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                _editingCard.Tags.Add(tag);
            _editingCard.Requirements.Clear();
            foreach (var requirement in _editingRequirements)
                _editingCard.Requirements.Add(new TaskRequirement { Id = requirement.Id, Text = requirement.Text, IsDone = requirement.IsDone });
            _editingCard.Flags.Commit = EditCommitFlag.IsChecked == true;
            _editingCard.Flags.Build = EditBuildFlag.IsChecked == true;
            _editingCard.Flags.Release = EditReleaseFlag.IsChecked == true;
            _editingCard.Flags.Merge = EditMergeFlag.IsChecked == true;
            _editingCard.StartedDate = EditStartedDate.SelectedDate;
            _editingCard.DueDate = EditDueDate.SelectedDate;
            _editingCard.Priority = EditPriority.SelectedIndex > 0 ? (CardPriority?)(EditPriority.SelectedIndex - 1) : null;
        }

        if (_store is not null)
        {
            try
            {
                foreach (var sourcePath in _pendingUploadPaths)
                    _editingCard.Files.Add(_store.ImportFile(sourcePath));
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "Could not attach file", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        SaveProject();
        CloseCardEditor();
    }

    private void AddRequirement_Click(object sender, RoutedEventArgs e) => AddPendingRequirement();

    private void NewRequirementInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddPendingRequirement();
            e.Handled = true;
        }
    }

    private void AddPendingRequirement()
    {
        var text = NewRequirementInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        _editingRequirements.Add(new TaskRequirement { Text = text });
        NewRequirementInput.Text = string.Empty;
        NewRequirementInput.Focus();
    }

    private void RemoveRequirement_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TaskRequirement requirement })
            _editingRequirements.Remove(requirement);
    }

    private void AttachFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null)
        {
            MessageBox.Show(this, "Choose a project folder before attaching files.", "No project selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new OpenFileDialog { Title = "Attach files to this card", Multiselect = true, CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        foreach (var path in picker.FileNames)
            QueuePendingFile(path);
    }

    private void CardEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || _editingCard is null ||
            _editingCard.IsNote || _store is null)
            return;

        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                foreach (var path in Clipboard.GetFileDropList().Cast<string>().Where(File.Exists))
                    QueuePendingFile(path);
                e.Handled = true;
                return;
            }

            if (!Clipboard.ContainsImage()) return;
            var image = Clipboard.GetImage();
            if (image is null) return;
            var temporaryPath = Path.Combine(Path.GetTempPath(), $"Zen-paste-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(temporaryPath)) encoder.Save(stream);
            _temporaryClipboardPaths.Add(temporaryPath);
            QueuePendingFile(temporaryPath);
            e.Handled = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not paste attachment", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void QueuePendingFile(string path)
    {
        if (_pendingUploadPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        _pendingUploadPaths.Add(path);
        _editingFileNames.Add($"＋ {Path.GetFileName(path)}");
    }

    private void EditTagsInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TagSuggestions is null) return;
        var text = EditTagsInput.Text;
        var comma = text.LastIndexOf(',');
        var token = text[(comma + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            TagSuggestions.Visibility = Visibility.Collapsed;
            return;
        }

        var alreadyUsed = text[..Math.Max(0, comma + 1)].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var knownTags = _document?.TagCatalog.Select(tag => tag.Name)
            ?? Columns.SelectMany(column => column.Tasks).SelectMany(card => card.Tags);
        var matches = knownTags.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(tag => tag.StartsWith(token, StringComparison.OrdinalIgnoreCase) &&
                          !tag.Equals(token, StringComparison.OrdinalIgnoreCase) &&
                          !alreadyUsed.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .OrderBy(tag => tag).Take(6).ToList();
        TagSuggestions.ItemsSource = matches;
        TagSuggestions.SelectedIndex = matches.Count > 0 ? 0 : -1;
        TagSuggestions.Visibility = matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EditTagsInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && TagSuggestions.Visibility == Visibility.Visible &&
            TagSuggestions.SelectedItem is string suggestion)
        {
            AcceptTagSuggestion(suggestion);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && TagSuggestions.Visibility == Visibility.Visible && TagSuggestions.Items.Count > 0)
        {
            TagSuggestions.SelectedIndex = Math.Min(TagSuggestions.Items.Count - 1, TagSuggestions.SelectedIndex + 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && TagSuggestions.Visibility == Visibility.Visible && TagSuggestions.Items.Count > 0)
        {
            TagSuggestions.SelectedIndex = Math.Max(0, TagSuggestions.SelectedIndex - 1);
            e.Handled = true;
        }
    }

    private void TagSuggestions_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (TagSuggestions.SelectedItem is string suggestion) AcceptTagSuggestion(suggestion);
    }

    private void AcceptTagSuggestion(string suggestion)
    {
        var text = EditTagsInput.Text;
        var comma = text.LastIndexOf(',');
        var prefix = comma >= 0 ? text[..(comma + 1)] + " " : string.Empty;
        EditTagsInput.Text = prefix + suggestion + ", ";
        EditTagsInput.CaretIndex = EditTagsInput.Text.Length;
        TagSuggestions.Visibility = Visibility.Collapsed;
        EditTagsInput.Focus();
    }

    private void CancelCardEdit_Click(object sender, RoutedEventArgs e) => CloseCardEditor();

    // The editor is an in-window overlay rather than a Popup so it stays clipped
    // to the Zen window and never floats above other applications.
    private void CloseCardEditor()
    {
        if (CardEditorHost.Visibility != Visibility.Visible) return;
        CardEditorHost.Visibility = Visibility.Collapsed;
        foreach (var path in _temporaryClipboardPaths)
        {
            try { File.Delete(path); }
            catch { /* Temporary clipboard files are best-effort cleanup. */ }
        }
        _temporaryClipboardPaths.Clear();
        _editingCard = null;
        EditValidationText.Visibility = Visibility.Collapsed;
        TagSuggestions.Visibility = Visibility.Collapsed;
    }

    private void EditorBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => CloseCardEditor();

    private void EndCardDragIfActive()
    {
        if (_activeDrag is not null) EndCardDrag();
    }

    private void OpenModal(ModalMode mode, bool bug = false)
    {
        _modalMode = mode;
        NameInput.Text = string.Empty;
        ValidationText.Visibility = Visibility.Collapsed;
        ModalTitle.Text = mode == ModalMode.Column ? "Create a column" : $"Add a {_newCardKind.ToString().ToLowerInvariant()}";
        ModalSubtitle.Text = mode == ModalMode.Column
            ? "Add another branch or category to this workspace."
            : $"Add a task to {_taskTarget?.Title}.";
        NameLabel.Text = mode == ModalMode.Column ? "COLUMN NAME" : $"{_newCardKind.ToString().ToUpperInvariant()} TITLE";
        ConfirmButton.Content = mode == ModalMode.Column ? "Create column" : $"Add {_newCardKind.ToString().ToLowerInvariant()}";
        ConfirmButton.Tag = bug;
        ModalScrim.Visibility = Visibility.Visible;
        NameInput.Focus();
    }

    private void ConfirmModal_Click(object sender, RoutedEventArgs e)
    {
        var name = NameInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowValidation(_modalMode == ModalMode.Column ? "Give this column a name." : "Give this task a title.");
            return;
        }

        if (_modalMode == ModalMode.Column)
        {
            if (Columns.Any(c => c.Title.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                ShowValidation("A column with that name already exists.");
                return;
            }
            var columnId = Guid.NewGuid().ToString("N");
            var branchBase = ProjectStore.CreateBranchName(name, columnId);
            var branchName = branchBase;
            var branchSuffix = 2;
            while (Columns.Any(c => string.Equals(c.Branch, branchName, StringComparison.OrdinalIgnoreCase)))
                branchName = $"{branchBase}-{branchSuffix++}";
            var column = new BoardColumn
            {
                Id = columnId,
                Title = name,
                Branch = branchName
            };
            Columns.Insert(Columns.Count - 1, column);
            SaveProject();
            CloseModal();
            Dispatcher.BeginInvoke(() => BoardScroller.ScrollToRightEnd());
        }
        else if (ResolveColumn(_taskTarget) is { } target)
        {
            var card = new TaskCard
            {
                Index = _nextTaskIndex++,
                Kind = _newCardKind,
                Title = name,
                Task = string.Empty,
                Tags = _newCardKind switch { CardKind.Note => ["note"], CardKind.Break => ["break"], _ => ["task"] }
            };
            if (ConfirmButton.Tag is true) card.Tags.Insert(0, "bug");
            target.Tasks.Add(card);
            SaveProject();
            CloseModal();
        }
        else if (_taskTarget is not null)
        {
            ShowValidation("That column is no longer on the board. Close this dialog and try again.");
        }
    }

    private void ColumnMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BoardColumn column }) ShowColumnMenu((Button)sender, column);
    }

    private void Column_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BoardColumn column } element || FindDataContext<TaskCard>(e.OriginalSource as DependencyObject) is not null) return;
        ShowColumnMenu(element, column);
        e.Handled = true;
    }

    private void ShowColumnMenu(Control anchor, BoardColumn column) => ShowColumnMenu((FrameworkElement)anchor, column);

    private void ShowColumnMenu(FrameworkElement anchor, BoardColumn column)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.MousePoint };
        menu.Items.Add(CreateNewCardMenu(column));
        AddMenuItem(menu, column.IsCollapsed ? "Expand column" : "Collapse column", (_, _) => ToggleColumn(column));
        var lockItem = new MenuItem
        {
            Header = column.IsLocked ? "Unlock branch" : "Lock branch",
            Icon = new TextBlock { Text = column.IsLocked ? "🔓" : "🔒", FontSize = 12, Foreground = (Brush)FindResource("LockBrush") }
        };
        lockItem.Click += (_, _) => { column.IsLocked = !column.IsLocked; SaveProject(); };
        menu.Items.Add(lockItem);
        if (column.IsLocked)
            menu.Items.Add(new MenuItem { Header = "Launch — branch locked", IsEnabled = false });
        else
            menu.Items.Add(CreateLaunchMenu($"Process the eligible queue in branch '{column.Branch ?? column.Title}'."));
        if (!column.IsPermanent)
        {
            menu.Items.Add(new Separator());
            var archive = new MenuItem { Header = "Archive column", Tag = column };
            archive.Click += ArchiveColumn_Click;
            menu.Items.Add(archive);
        }
        menu.IsOpen = true;
    }

    private void ToggleColumnCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BoardColumn column }) ToggleColumn(column);
    }

    private void ToggleColumn(BoardColumn column)
    {
        column.IsCollapsed = !column.IsCollapsed;
        SaveProject();
    }

    private void ColumnHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BoardColumn column } ||
            FindDataContext<TaskCard>(e.OriginalSource as DependencyObject) is not null ||
            FindVisualAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        _pressedColumn = column;
        _columnDragStart = e.GetPosition(RootLayout);
    }

    private void ColumnHeader_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isColumnDragging) _pressedColumn = null;
    }

    private static T? FindDataContext<T>(DependencyObject? element) where T : class
    {
        while (element is not null)
        {
            if (element is FrameworkElement { DataContext: T value }) return value;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T value) return value;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void ArchiveColumn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: BoardColumn column }) return;
        var archive = Columns.First(c => c.Kind == ColumnKind.Archive);
        foreach (var task in column.Tasks)
        {
            task.IsDone = true;
            archive.Tasks.Add(task);
        }
        Columns.Remove(column);
        SaveProject();
    }

    private void ClearArchive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BoardColumn { IsArchive: true } archive } || archive.Tasks.Count == 0) return;
        var result = MessageBox.Show(this,
            $"Remove all {archive.Tasks.Count} archived card(s)? Attached files will remain in the project files folder.",
            "Clear archive", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        archive.Tasks.Clear();
        SaveProject();
    }

    private void BoardScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            BoardScroller.ScrollToHorizontalOffset(BoardScroller.HorizontalOffset - e.Delta);
        else
            BoardScroller.ScrollToVerticalOffset(BoardScroller.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void BoardScroller_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _activeDrag is not null) return;
        _isBoardPanning = true;
        _panStart = e.GetPosition(BoardScroller);
        _panStartOffset = BoardScroller.HorizontalOffset;
        _panStartVerticalOffset = BoardScroller.VerticalOffset;
        BoardScroller.Cursor = Cursors.SizeAll;
        BoardScroller.CaptureMouse();
        e.Handled = true;
    }

    private void BoardScroller_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isBoardPanning || e.MiddleButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(BoardScroller);
        BoardScroller.ScrollToHorizontalOffset(_panStartOffset - (current.X - _panStart.X));
        BoardScroller.ScrollToVerticalOffset(_panStartVerticalOffset - (current.Y - _panStart.Y));
        e.Handled = true;
    }

    private void BoardScroller_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isBoardPanning || e.ChangedButton != MouseButton.Middle) return;
        EndBoardPan();
        e.Handled = true;
    }

    private void BoardScroller_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isBoardPanning) EndBoardPan();
    }

    private void EndBoardPan()
    {
        _isBoardPanning = false;
        BoardScroller.Cursor = Cursors.Arrow;
        if (Mouse.Captured == BoardScroller) BoardScroller.ReleaseMouseCapture();
    }

    private void ScrollLeft_Click(object sender, RoutedEventArgs e) =>
        BoardScroller.ScrollToHorizontalOffset(BoardScroller.HorizontalOffset - 680);

    private void ScrollRight_Click(object sender, RoutedEventArgs e) =>
        BoardScroller.ScrollToHorizontalOffset(BoardScroller.HorizontalOffset + 680);

    private void NameInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ConfirmModal_Click(sender, e);
        if (e.Key == Key.Escape) CloseModal();
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }

    private void CancelModal_Click(object sender, RoutedEventArgs e) => CloseModal();
    private void ModalScrim_MouseDown(object sender, MouseButtonEventArgs e) => CloseModal();
    private void ModalCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void CloseModal()
    {
        ModalScrim.Visibility = Visibility.Collapsed;
        _taskTarget = null;
    }

    private enum ModalMode { Column, Task }

    private sealed record TaskDragPayload(TaskCard Task, BoardColumn Source, int SourceIndex, Border SourceElement);
    private sealed record NewCardRequest(BoardColumn Column, CardKind Kind, bool Bug);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
}
