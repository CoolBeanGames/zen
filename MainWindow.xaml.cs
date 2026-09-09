using System.Collections.ObjectModel;
using System.IO;
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
    private ProjectDocument? _document;
    private ProjectStore? _store;
    private readonly RecentProjectStore _recentProjects = new();
    private FileSystemWatcher? _projectWatcher;
    private DateTime _ignoreFileEventsUntil;

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
            Interval = TimeSpan.FromSeconds(60)
        };
        _periodicReloadTimer.Tick += PeriodicReloadTimer_Tick;
        SeedBoard();
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
            Id = "uncategorized", Title = "Uncategorized", Branch = null, IsPermanent = true,
            Tasks =
            {
                new TaskCard { Index = 3, Title = "Capture quick ideas here", Task = "Sort them when you know where they belong.", Tags = ["inbox"] }
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
        if (_store is null || _activeDrag is not null || CardEditorPopup.IsOpen)
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
            MessageBox.Show(this, exception.Message, "Invalid task data", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        ProjectReloadTimer_Tick(sender, EventArgs.Empty);
    }

    private void NewColumn_Click(object sender, RoutedEventArgs e) => OpenModal(ModalMode.Column);

    private void AddTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BoardColumn column })
        {
            _taskTarget = column;
            OpenModal(ModalMode.Task);
        }
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
        card.IsLocked = !card.IsLocked;
        SaveProject();
        e.Handled = true;
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
        e.Handled = true;
    }

    private void BeginCardDrag(Border card)
    {
        if (card.DataContext is not TaskCard task) return;
        var source = Columns.FirstOrDefault(column => column.Tasks.Contains(task));
        if (source is null) return;

        _cardClickTimer.Stop();
        _pendingClickCard = null;

        card.ReleaseMouseCapture();
        _activeDrag = new TaskDragPayload(task, source, card);
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
        UpdateEdgeScroll(_lastDragPointer);
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_activeDrag is not null)
        {
            var drag = _activeDrag;
            var target = FindColumnAt(e.GetPosition(RootLayout));
            EndCardDrag();
            if (target is not null && target.Value.Column != drag.Source &&
                drag.Source.Tasks.Remove(drag.Task))
            {
                drag.Task.IsDone = target.Value.Column.Kind == ColumnKind.Archive;
                target.Value.Column.Tasks.Add(drag.Task);
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
        _highlightedColumn.BorderBrush = (Brush)FindResource("BorderBrush");
        _highlightedColumn.BorderThickness = new Thickness(1);
        _highlightedColumn.Background = (Brush)FindResource("ColumnBrush");
        _highlightedColumn = null;
    }

    private void OpenCardEditor(TaskCard card, Border anchor)
    {
        EndCardDragIfActive();
        _editingCard = card;
        EditIdentityText.Text = $"{card.IndexLabel}  ·  ID {card.Id.ToUpperInvariant()}";
        EditTitleInput.Text = card.Title;
        EditTagsInput.Text = string.Join(", ", card.Tags);
        EditTaskInput.Text = card.Task;
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
        EditValidationText.Visibility = Visibility.Collapsed;
        RootLayout.Effect = new BlurEffect
        {
            Radius = 7,
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Quality
        };
        EditorBackdrop.Visibility = Visibility.Visible;
        CardEditorPopup.PlacementTarget = anchor;
        CardEditorPopup.Placement = PlacementMode.MousePoint;
        CardEditorPopup.IsOpen = true;
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
            CardEditorPopup.IsOpen = false;
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
        _editingCard.Tags.Clear();
        foreach (var tag in EditTagsInput.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase))
            _editingCard.Tags.Add(tag);
        _editingCard.Requirements.Clear();
        foreach (var requirement in _editingRequirements)
            _editingCard.Requirements.Add(new TaskRequirement { Id = requirement.Id, Text = requirement.Text, IsDone = requirement.IsDone });
        _editingCard.Flags.Commit = EditCommitFlag.IsChecked == true;
        _editingCard.Flags.Build = EditBuildFlag.IsChecked == true;
        _editingCard.Flags.Release = EditReleaseFlag.IsChecked == true;
        _editingCard.Flags.Merge = EditMergeFlag.IsChecked == true;

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
        CardEditorPopup.IsOpen = false;
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
        CardEditorPopup.StaysOpen = true;
        try
        {
            if (picker.ShowDialog(this) != true) return;
            foreach (var path in picker.FileNames)
            {
                if (_pendingUploadPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                _pendingUploadPaths.Add(path);
                _editingFileNames.Add($"＋ {System.IO.Path.GetFileName(path)}");
            }
        }
        finally
        {
            CardEditorPopup.StaysOpen = false;
        }
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

    private void CancelCardEdit_Click(object sender, RoutedEventArgs e) =>
        CardEditorPopup.IsOpen = false;

    private void CardEditorPopup_Closed(object? sender, EventArgs e)
    {
        _editingCard = null;
        EditValidationText.Visibility = Visibility.Collapsed;
        EditorBackdrop.Visibility = Visibility.Collapsed;
        RootLayout.Effect = null;
        TagSuggestions.Visibility = Visibility.Collapsed;
    }

    private void EditorBackdrop_MouseDown(object sender, MouseButtonEventArgs e) =>
        CardEditorPopup.IsOpen = false;

    private void EndCardDragIfActive()
    {
        if (_activeDrag is not null) EndCardDrag();
    }

    private void OpenModal(ModalMode mode)
    {
        _modalMode = mode;
        NameInput.Text = string.Empty;
        ValidationText.Visibility = Visibility.Collapsed;
        ModalTitle.Text = mode == ModalMode.Column ? "Create a column" : "Add a task";
        ModalSubtitle.Text = mode == ModalMode.Column
            ? "Add another branch or category to this workspace."
            : $"Add a task to {_taskTarget?.Title}.";
        NameLabel.Text = mode == ModalMode.Column ? "COLUMN NAME" : "TASK TITLE";
        ConfirmButton.Content = mode == ModalMode.Column ? "Create column" : "Add task";
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
        else if (_taskTarget is not null)
        {
            _taskTarget.Tasks.Add(new TaskCard
            {
                Index = _nextTaskIndex++,
                Title = name,
                Task = string.Empty,
                Tags = ["task"]
            });
            SaveProject();
            CloseModal();
        }
    }

    private void ColumnMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BoardColumn column } || column.IsPermanent) return;

        var menu = new ContextMenu { PlacementTarget = (Button)sender };
        var archive = new MenuItem { Header = "Archive column", Tag = column };
        archive.Click += ArchiveColumn_Click;
        menu.Items.Add(archive);
        menu.IsOpen = true;
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

    private sealed record TaskDragPayload(TaskCard Task, BoardColumn Source, Border SourceElement);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
}
