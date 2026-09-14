using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace Zen;

public partial class CardStudioWindow : Window
{
    private CustomCardDefinition? _selected;
    private bool _loading;
    public ObservableCollection<CustomCardDefinition> Definitions { get; }

    public CardStudioWindow(IEnumerable<CustomCardDefinition> definitions)
    {
        InitializeComponent();
        Definitions = new(definitions.Select(Clone));
        TypesList.ItemsSource = Definitions;
        if (Definitions.Count > 0) TypesList.SelectedIndex = 0;
    }

    private static CustomCardDefinition Clone(CustomCardDefinition source) => new()
    {
        Id = source.Id, Name = source.Name, Instructions = source.Instructions,
        CardColor = source.CardColor, OutlineColor = source.OutlineColor, OutlineWidth = source.OutlineWidth,
        Fields = new(source.Fields.Select(field => new CustomFieldDefinition { Id = field.Id, Name = field.Name, Type = field.Type, DefaultValue = field.DefaultValue, ShowOnCollapsed = field.ShowOnCollapsed, Options = new(field.Options), X = field.X, Y = field.Y, Width = field.Width, Height = field.Height, ExpandedX = field.ExpandedX, ExpandedY = field.ExpandedY, ExpandedWidth = field.ExpandedWidth, ExpandedHeight = field.ExpandedHeight, CompactX = field.CompactX, CompactY = field.CompactY, CompactWidth = field.CompactWidth, CompactHeight = field.CompactHeight }))
    };

    private void TypesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = TypesList.SelectedItem as CustomCardDefinition;
        _loading = true;
        Editor.IsEnabled = _selected is not null;
        NameInput.Text = _selected?.Name ?? string.Empty;
        InstructionsInput.Text = _selected?.Instructions ?? string.Empty;
        FieldsList.ItemsSource = _selected?.Fields;
        _loading = false;
    }

    private void DefinitionChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _selected is null) return;
        _selected.Name = NameInput.Text;
        _selected.Instructions = InstructionsInput.Text;
        TypesList.Items.Refresh();
    }

    private void AddType_Click(object sender, RoutedEventArgs e) { var item = new CustomCardDefinition(); Definitions.Add(item); TypesList.SelectedItem = item; NameInput.Focus(); NameInput.SelectAll(); }
    private void DeleteType_Click(object sender, RoutedEventArgs e) { if (_selected is null) return; Definitions.Remove(_selected); TypesList.SelectedIndex = Definitions.Count > 0 ? 0 : -1; }
    private void AddField_Click(object sender, RoutedEventArgs e) { if (_selected is null) return; _selected.Fields.Add(new CustomFieldDefinition()); }
    private void OpenDesigner_Click(object sender, RoutedEventArgs e) { if (_selected is null) return; var designer = new CardDesignerWindow(_selected) { Owner = this }; if (designer.ShowDialog() == true) FieldsList.Items.Refresh(); }
    private void RemoveField_Click(object sender, RoutedEventArgs e) { if (_selected is not null && sender is Button { Tag: CustomFieldDefinition field }) _selected.Fields.Remove(field); }
    private void Save_Click(object sender, RoutedEventArgs e) { if (Definitions.Any(item => string.IsNullOrWhiteSpace(item.Name))) { MessageBox.Show(this, "Every card type needs a name."); return; } DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
