using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Zen;

public sealed class CustomListFieldEditor : StackPanel
{
    private readonly CustomFieldValue _value;
    private readonly List<string> _items;
    private readonly StackPanel _rows = new();
    private readonly TextBox _newItem = new();

    public CustomListFieldEditor(CustomFieldValue value)
    {
        _value = value;
        try { _items = JsonSerializer.Deserialize<List<string>>(value.Value) ?? []; }
        catch { _items = value.Value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList(); }
        Children.Add(_rows);
        var addRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        addRow.ColumnDefinitions.Add(new ColumnDefinition());
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ApplyFieldStyle(_newItem);
        _newItem.MinHeight = 32;
        _newItem.KeyDown += (_, args) => { if (args.Key == Key.Enter) { AddPending(); args.Handled = true; } };
        var add = new Button { Content = "Add", Margin = new Thickness(7, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
        add.Click += (_, _) => AddPending();
        Grid.SetColumn(add, 1);
        addRow.Children.Add(_newItem);
        addRow.Children.Add(add);
        Children.Add(addRow);
        RenderRows();
    }

    private void AddPending()
    {
        var text = _newItem.Text.Trim();
        if (text.Length == 0) return;
        _items.Add(text);
        _newItem.Clear();
        Sync();
        RenderRows();
        _newItem.Focus();
    }

    private void RenderRows()
    {
        _rows.Children.Clear();
        for (var index = 0; index < _items.Count; index++)
        {
            var itemIndex = index;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var editor = new TextBox { Text = _items[index], Padding = new Thickness(8, 5, 8, 5) };
            ApplyFieldStyle(editor);
            editor.TextChanged += (_, _) => { _items[itemIndex] = editor.Text; Sync(); };
            var remove = new Button { Content = "×", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
            remove.Click += (_, _) => { _items.RemoveAt(itemIndex); Sync(); RenderRows(); };
            Grid.SetColumn(remove, 1);
            row.Children.Add(editor);
            row.Children.Add(remove);
            _rows.Children.Add(row);
        }
    }

    private void Sync() => _value.Value = JsonSerializer.Serialize(_items);

    private static void ApplyFieldStyle(Control control)
    {
        if (Application.Current.TryFindResource("Field") is Style style) control.Style = style;
    }
}
