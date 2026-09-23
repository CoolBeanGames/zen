using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Zen;

public partial class SignaturesWindow : Window
{
    private const string AllAgents = "All agents";
    private readonly ProjectDocument _document;
    private readonly Action _save;
    private readonly ObservableCollection<ProjectSignature> _visibleSignatures = [];
    private bool _initialized;

    public SignaturesWindow(ProjectDocument document, Action save)
    {
        InitializeComponent();
        _document = document;
        _save = save;
        SignatureList.ItemsSource = _visibleSignatures;
        RefreshAgentFilter();
        _initialized = true;
        RefreshSignatures();
        Loaded += (_, _) => UpdateScrollButtons();
    }

    private void RefreshAgentFilter()
    {
        var selected = AgentFilter.SelectedItem as string ?? AllAgents;
        var agents = new[] { AllAgents }.Concat(_document.Signatures.Select(signature => signature.Agent)
            .Where(agent => !string.IsNullOrWhiteSpace(agent)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(agent => agent)).ToList();
        AgentFilter.ItemsSource = agents;
        AgentFilter.SelectedItem = agents.Contains(selected, StringComparer.OrdinalIgnoreCase) ? selected : AllAgents;
    }

    private void RefreshSignatures()
    {
        if (!_initialized) return;
        var selectedDate = DateFilter.SelectedDate?.Date;
        var selectedAgent = AgentFilter.SelectedItem as string;
        var taskId = TaskFilter.Text.Trim().TrimStart('#');
        var matches = _document.Signatures
            .Where(signature => selectedDate is null || signature.SignedAt.ToLocalTime().Date == selectedDate)
            .Where(signature => string.IsNullOrWhiteSpace(selectedAgent) || selectedAgent == AllAgents || signature.Agent.Equals(selectedAgent, StringComparison.OrdinalIgnoreCase))
            .Where(signature => taskId.Length == 0 || signature.TaskId.Equals(taskId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(signature => signature.SignedAt)
            .ToList();
        _visibleSignatures.Clear();
        foreach (var signature in matches) _visibleSignatures.Add(signature);
        EmptyText.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Dispatcher.BeginInvoke(UpdateScrollButtons, DispatcherPriority.Loaded);
    }

    private void Filter_Changed(object sender, EventArgs e) => RefreshSignatures();

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        DateFilter.SelectedDate = null;
        AgentFilter.SelectedItem = AllAgents;
        TaskFilter.Clear();
        RefreshSignatures();
    }

    private void AddSignature_Click(object sender, RoutedEventArgs e)
    {
        var message = UserMessageInput.Text.Trim();
        if (message.Length == 0)
        {
            ValidationText.Visibility = Visibility.Visible;
            UserMessageInput.Focus();
            return;
        }
        ValidationText.Visibility = Visibility.Collapsed;
        _document.Signatures.Add(new ProjectSignature
        {
            Message = message,
            TaskId = "N/A",
            Agent = "User",
            SignedAt = DateTimeOffset.Now
        });
        _save();
        UserMessageInput.Clear();
        RefreshAgentFilter();
        DateFilter.SelectedDate = null;
        AgentFilter.SelectedItem = AllAgents;
        TaskFilter.Clear();
        RefreshSignatures();
        Dispatcher.BeginInvoke(() => SignatureScroller.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    private void SignatureScroller_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateScrollButtons();
    private void SignatureScroller_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateScrollButtons();
    private void ScrollTop_Click(object sender, RoutedEventArgs e) => SignatureScroller.ScrollToTop();
    private void ScrollBottom_Click(object sender, RoutedEventArgs e) => SignatureScroller.ScrollToEnd();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateScrollButtons()
    {
        var scrollable = SignatureScroller.ScrollableHeight > 1;
        ScrollTopButton.Visibility = scrollable && SignatureScroller.VerticalOffset > 1 ? Visibility.Visible : Visibility.Collapsed;
        ScrollBottomButton.Visibility = scrollable && SignatureScroller.VerticalOffset < SignatureScroller.ScrollableHeight - 1 ? Visibility.Visible : Visibility.Collapsed;
    }
}
