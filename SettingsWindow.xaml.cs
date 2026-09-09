using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;

namespace Zen;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly RecentProjectStore _projectStore;
    private readonly ObservableCollection<RecentProject> _projects;
    public ZenSettings Settings { get; }

    public SettingsWindow(RecentProjectStore projectStore)
    {
        InitializeComponent();
        _projectStore = projectStore;
        Settings = _settingsStore.Load();
        PromptInput.Text = Settings.GlobalPrompt;
        ReloadInput.Text = Settings.ReloadSeconds.ToString();
        _projects = new ObservableCollection<RecentProject>(_projectStore.Load());
        ProjectsList.ItemsSource = _projects;
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Zen {version?.ToString(3) ?? "development"}";
    }

    private void RemoveProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RecentProject project }) return;
        var result = MessageBox.Show(this,
            $"Remove {project.Name} from Zen?\n\nZen will back up zen.tasks.json locally and delete prompt.txt from the project folder.",
            "Remove project", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        try
        {
            var backup = _projectStore.Remove(project.Path);
            _projects.Remove(project);
            MessageBox.Show(this, $"Project removed.\n\nTask backup:\n{backup}", "Project removed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not remove project", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ReloadInput.Text, out var seconds) || seconds is < 5 or > 3600)
        {
            SettingsValidation.Text = "Reload frequency must be between 5 and 3600 seconds.";
            SettingsValidation.Visibility = Visibility.Visible;
            return;
        }
        Settings.GlobalPrompt = PromptInput.Text;
        Settings.ReloadSeconds = seconds;
        _settingsStore.Save(Settings);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
