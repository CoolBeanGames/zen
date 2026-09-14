using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Zen;

public partial class CardDesignerWindow : Window
{
    private readonly CustomCardDefinition _definition;
    private readonly ObservableCollection<CustomFieldDefinition> _fields;
    private CustomFieldDefinition? _selected;
    private Point _dragOrigin;
    private double _fieldOriginX, _fieldOriginY;
    private bool _loading;

    public CardDesignerWindow(CustomCardDefinition definition)
    {
        InitializeComponent();
        _definition = definition;
        Heading.Text = $"Design {definition.Name}";
        _fields = new(definition.Fields.Select(Clone));
        Loaded += (_, _) => RenderFields();
    }

    private static CustomFieldDefinition Clone(CustomFieldDefinition f) => new() { Id=f.Id, Name=f.Name, Type=f.Type, DefaultValue=f.DefaultValue, Options=new(f.Options), ShowOnCollapsed=f.ShowOnCollapsed, X=f.X, Y=f.Y, Width=f.Width, Height=f.Height };

    private void Palette_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && sender is Button { Tag: string type }) DragDrop.DoDragDrop((DependencyObject)sender, type, DragDropEffects.Copy);
    }
    private void DesignCanvas_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(typeof(string)) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled=true; }
    private void DesignCanvas_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(string)) is not string type) return;
        var point=e.GetPosition(DesignCanvas);
        var field=new CustomFieldDefinition { Name=type == "dropdown" ? "Choice" : type == "checkbox" ? "Option" : "Field", Type=type, X=Math.Max(0,point.X-110), Y=Math.Max(0,point.Y-36) };
        if (type=="dropdown") { field.Options.Add("Option 1"); field.Options.Add("Option 2"); }
        _fields.Add(field); RenderFields(); SelectField(field);
    }

    private void RenderFields()
    {
        DesignCanvas.Children.Clear();
        foreach (var field in _fields)
        {
            var border=new Border { Tag=field, Width=field.Width, Height=field.Height, Background=new SolidColorBrush(Color.FromRgb(29,34,44)), BorderBrush=new SolidColorBrush(field==_selected ? Color.FromRgb(139,124,255) : Color.FromRgb(57,65,81)), BorderThickness=new Thickness(field==_selected?2:1), CornerRadius=new CornerRadius(8), Padding=new Thickness(10), Cursor=Cursors.SizeAll };
            border.Child=new StackPanel { Children = { new TextBlock { Text=field.Name, FontWeight=FontWeights.SemiBold }, new TextBlock { Text=field.Type.ToUpperInvariant(), Foreground=new SolidColorBrush(Color.FromRgb(137,146,165)), FontSize=10 } } };
            border.PreviewMouseLeftButtonDown += Field_MouseDown; border.PreviewMouseMove += Field_MouseMove; border.PreviewMouseLeftButtonUp += Field_MouseUp;
            Canvas.SetLeft(border,field.X); Canvas.SetTop(border,field.Y); DesignCanvas.Children.Add(border);
        }
    }
    private void Field_MouseDown(object sender, MouseButtonEventArgs e) { if(sender is not Border {Tag:CustomFieldDefinition f} b)return; _selected=f; _dragOrigin=e.GetPosition(DesignCanvas); _fieldOriginX=f.X; _fieldOriginY=f.Y; b.CaptureMouse(); RenderFields(); }
    private void Field_MouseMove(object sender, MouseEventArgs e) { if(e.LeftButton!=MouseButtonState.Pressed||sender is not Border {Tag:CustomFieldDefinition f})return; var p=e.GetPosition(DesignCanvas); var d=p-_dragOrigin; f.X=Math.Max(0,_fieldOriginX+d.X); f.Y=Math.Max(0,_fieldOriginY+d.Y); Canvas.SetLeft((UIElement)sender,f.X); Canvas.SetTop((UIElement)sender,f.Y); }
    private void Field_MouseUp(object sender, MouseButtonEventArgs e) { if(sender is Border b)b.ReleaseMouseCapture(); if(_selected is not null)SelectField(_selected); }
    private void SelectField(CustomFieldDefinition field) { _selected=field; _loading=true; Inspector.Visibility=Visibility.Visible; FieldName.Text=field.Name; FieldType.SelectedIndex=Math.Max(0,new[]{"text","number","checkbox","dropdown"}.ToList().IndexOf(field.Type)); DefaultValue.Text=field.DefaultValue; OptionsInput.Text=string.Join(", ",field.Options); OptionsHost.Visibility=field.Type=="dropdown"?Visibility.Visible:Visibility.Collapsed; CollapsedVisible.IsChecked=field.ShowOnCollapsed; _loading=false; RenderFields(); }
    private void InspectorChanged(object sender, RoutedEventArgs e) { if(_loading||_selected is null)return; _selected.Name=FieldName.Text; _selected.Type=(FieldType.SelectedItem as ComboBoxItem)?.Content?.ToString()??"text"; _selected.DefaultValue=DefaultValue.Text; _selected.ShowOnCollapsed=CollapsedVisible.IsChecked==true; _selected.Options.Clear(); foreach(var item in OptionsInput.Text.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))_selected.Options.Add(item); OptionsHost.Visibility=_selected.Type=="dropdown"?Visibility.Visible:Visibility.Collapsed; RenderFields(); }
    private void RemoveElement_Click(object sender, RoutedEventArgs e) { if(_selected is null)return; _fields.Remove(_selected); _selected=null; Inspector.Visibility=Visibility.Collapsed; RenderFields(); }
    private void Save_Click(object sender, RoutedEventArgs e) { _definition.Fields.Clear(); foreach(var field in _fields)_definition.Fields.Add(field); DialogResult=true; }
    private void Cancel_Click(object sender, RoutedEventArgs e)=>DialogResult=false;
}
