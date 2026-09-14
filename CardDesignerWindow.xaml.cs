using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private bool _paletteDragArmed;
    private Guid? _lastPaletteDropToken;

    private sealed record PaletteDrag(string Type, Guid Token);
    private enum DesignerSurface { Open, Expanded, Compact }

    public CardDesignerWindow(CustomCardDefinition definition)
    {
        InitializeComponent();
        _definition = definition;
        Heading.Text = $"Design {definition.Name}";
        _fields = new(definition.Fields.Select(Clone));
        Loaded += (_, _) => { RenderFields(); RefreshExpandedDesigner(); RefreshCompactDesigner(); };
    }

    private static CustomFieldDefinition Clone(CustomFieldDefinition f) => new() { Id=f.Id, Name=f.Name, Type=f.Type, DefaultValue=f.DefaultValue, Options=new(f.Options), ShowOnCollapsed=f.ShowOnCollapsed, X=f.X, Y=f.Y, Width=f.Width, Height=f.Height, ExpandedX=f.ExpandedX, ExpandedY=f.ExpandedY, ExpandedWidth=f.ExpandedWidth, ExpandedHeight=f.ExpandedHeight, CompactX=f.CompactX, CompactY=f.CompactY, CompactWidth=f.CompactWidth, CompactHeight=f.CompactHeight };

    private void Palette_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _paletteDragArmed = true;

    private void Palette_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_paletteDragArmed || e.LeftButton != MouseButtonState.Pressed || sender is not Button { Tag: string type }) return;
        _paletteDragArmed = false;
        DragDrop.DoDragDrop((DependencyObject)sender, new PaletteDrag(type, Guid.NewGuid()), DragDropEffects.Copy);
    }
    private void DesignCanvas_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(typeof(PaletteDrag)) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled=true; }
    private void DesignCanvas_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(PaletteDrag)) is not PaletteDrag drag || _lastPaletteDropToken == drag.Token) return;
        _lastPaletteDropToken = drag.Token;
        var type = drag.Type;
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
            var host = new Grid { Tag=field, Width=field.Width, Height=field.Height, Cursor=Cursors.SizeAll };
            var border=new Border { Background=new SolidColorBrush(Color.FromRgb(29,34,44)), BorderBrush=new SolidColorBrush(field==_selected ? Color.FromRgb(139,124,255) : Color.FromRgb(57,65,81)), BorderThickness=new Thickness(field==_selected?2:1), CornerRadius=new CornerRadius(8), Padding=new Thickness(10) };
            border.Child=new StackPanel { Children = { new TextBlock { Text=field.Name, FontWeight=FontWeights.SemiBold }, new TextBlock { Text=field.Type.ToUpperInvariant(), Foreground=new SolidColorBrush(Color.FromRgb(137,146,165)), FontSize=10 } } };
            host.Children.Add(border); AddResizeHandles(host, field, DesignerSurface.Open);
            host.PreviewMouseLeftButtonDown += Field_MouseDown; host.PreviewMouseMove += Field_MouseMove; host.PreviewMouseLeftButtonUp += Field_MouseUp;
            Canvas.SetLeft(host,field.X); Canvas.SetTop(host,field.Y); DesignCanvas.Children.Add(host);
        }
    }
    private void AddResizeHandles(Grid host, CustomFieldDefinition field, DesignerSurface surface)
    {
        AddResizeHandle(host, field, surface, true, false, HorizontalAlignment.Right, VerticalAlignment.Stretch, 8, double.NaN, Cursors.SizeWE);
        AddResizeHandle(host, field, surface, false, true, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, 8, Cursors.SizeNS);
        AddResizeHandle(host, field, surface, true, true, HorizontalAlignment.Right, VerticalAlignment.Bottom, 14, 14, Cursors.SizeNWSE, true);
    }
    private void AddResizeHandle(Grid host, CustomFieldDefinition field, DesignerSurface surface, bool horizontal, bool vertical, HorizontalAlignment horizontalAlignment, VerticalAlignment verticalAlignment, double width, double height, Cursor cursor, bool visible=false)
    {
        var thumb = new Thumb { HorizontalAlignment=horizontalAlignment, VerticalAlignment=verticalAlignment, Cursor=cursor, Background=visible ? new SolidColorBrush(Color.FromRgb(139,124,255)) : Brushes.Transparent, Opacity=visible ? 0.9 : 1 };
        if (!double.IsNaN(width)) thumb.Width=width;
        if (!double.IsNaN(height)) thumb.Height=height;
        thumb.DragDelta += (_, args) =>
        {
            if (surface == DesignerSurface.Compact)
            {
                if (horizontal) field.CompactWidth=Math.Max(90,field.CompactWidth+args.HorizontalChange);
                if (vertical) field.CompactHeight=Math.Max(42,field.CompactHeight+args.VerticalChange);
                host.Width=field.CompactWidth; host.Height=field.CompactHeight;
            }
            else if (surface == DesignerSurface.Expanded)
            {
                if (horizontal) field.ExpandedWidth=Math.Max(90,field.ExpandedWidth+args.HorizontalChange);
                if (vertical) field.ExpandedHeight=Math.Max(42,field.ExpandedHeight+args.VerticalChange);
                host.Width=field.ExpandedWidth; host.Height=field.ExpandedHeight;
            }
            else
            {
                if (horizontal) field.Width=Math.Max(110,field.Width+args.HorizontalChange);
                if (vertical) field.Height=Math.Max(54,field.Height+args.VerticalChange);
                host.Width=field.Width; host.Height=field.Height;
            }
            args.Handled=true;
        };
        thumb.DragCompleted += (_, _) => { if(surface == DesignerSurface.Compact) RefreshCompactDesigner(); else if(surface == DesignerSurface.Expanded) RefreshExpandedDesigner(); else RenderFields(); };
        host.Children.Add(thumb);
    }
    private static bool IsResizeHandle(MouseButtonEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current is not null)
        {
            if (current is Thumb) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }
    private void Field_MouseDown(object sender, MouseButtonEventArgs e) { if(IsResizeHandle(e)||sender is not FrameworkElement {Tag:CustomFieldDefinition f} element)return; _selected=f; _dragOrigin=e.GetPosition(DesignCanvas); _fieldOriginX=f.X; _fieldOriginY=f.Y; element.CaptureMouse(); }
    private void Field_MouseMove(object sender, MouseEventArgs e) { if(e.LeftButton!=MouseButtonState.Pressed||sender is not FrameworkElement {Tag:CustomFieldDefinition f}||Mouse.Captured is Thumb)return; var p=e.GetPosition(DesignCanvas); var d=p-_dragOrigin; f.X=Math.Max(0,_fieldOriginX+d.X); f.Y=Math.Max(0,_fieldOriginY+d.Y); Canvas.SetLeft((UIElement)sender,f.X); Canvas.SetTop((UIElement)sender,f.Y); }
    private void Field_MouseUp(object sender, MouseButtonEventArgs e) { if(sender is FrameworkElement element && Mouse.Captured==element)element.ReleaseMouseCapture(); if(_selected is not null)SelectField(_selected); }
    private void SelectField(CustomFieldDefinition field) { _selected=field; _loading=true; Inspector.Visibility=Visibility.Visible; FieldName.Text=field.Name; FieldType.SelectedIndex=Math.Max(0,new[]{"text","number","checkbox","dropdown"}.ToList().IndexOf(field.Type)); DefaultValue.Text=field.DefaultValue; OptionsInput.Text=string.Join(", ",field.Options); OptionsHost.Visibility=field.Type=="dropdown"?Visibility.Visible:Visibility.Collapsed; CollapsedVisible.IsChecked=field.ShowOnCollapsed; _loading=false; RenderFields(); }
    private void InspectorChanged(object sender, RoutedEventArgs e) { if(_loading||_selected is null)return; _selected.Name=FieldName.Text; _selected.Type=(FieldType.SelectedItem as ComboBoxItem)?.Content?.ToString()??"text"; _selected.DefaultValue=DefaultValue.Text; _selected.ShowOnCollapsed=CollapsedVisible.IsChecked==true; _selected.Options.Clear(); foreach(var item in OptionsInput.Text.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))_selected.Options.Add(item); OptionsHost.Visibility=_selected.Type=="dropdown"?Visibility.Visible:Visibility.Collapsed; RenderFields(); RefreshExpandedDesigner(); RefreshCompactDesigner(); }
    private void RemoveElement_Click(object sender, RoutedEventArgs e) { if(_selected is null)return; _fields.Remove(_selected); _selected=null; Inspector.Visibility=Visibility.Collapsed; RenderFields(); RefreshExpandedDesigner(); RefreshCompactDesigner(); }
    private void DesignerTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) { RefreshExpandedDesigner(); RefreshCompactDesigner(); } }

    private void RefreshExpandedDesigner()
    {
        ExpandedCanvas.Children.Clear();
        foreach (var field in _fields)
        {
            var host = new Grid { Tag=field, Width=field.ExpandedWidth, Height=field.ExpandedHeight, Cursor=Cursors.SizeAll };
            var border = new Border { Background=new SolidColorBrush(Color.FromRgb(29,34,44)), BorderBrush=new SolidColorBrush(Color.FromRgb(57,65,81)), BorderThickness=new Thickness(1), CornerRadius=new CornerRadius(7), Padding=new Thickness(8) };
            border.Child = new StackPanel { Children = { new TextBlock { Text=field.Name, Foreground=new SolidColorBrush(Color.FromRgb(137,146,165)), FontSize=9 }, new TextBlock { Text=field.DefaultValue, FontWeight=FontWeights.SemiBold, TextTrimming=TextTrimming.CharacterEllipsis } } };
            host.Children.Add(border); AddResizeHandles(host, field, DesignerSurface.Expanded);
            host.PreviewMouseLeftButtonDown += ExpandedField_MouseDown; host.PreviewMouseMove += ExpandedField_MouseMove; host.PreviewMouseLeftButtonUp += ExpandedField_MouseUp;
            Canvas.SetLeft(host,field.ExpandedX); Canvas.SetTop(host,field.ExpandedY); ExpandedCanvas.Children.Add(host);
        }
    }
    private void ExpandedField_MouseDown(object sender, MouseButtonEventArgs e) { if(IsResizeHandle(e)||sender is not FrameworkElement {Tag:CustomFieldDefinition f} element)return; _selected=f; _dragOrigin=e.GetPosition(ExpandedCanvas); _fieldOriginX=f.ExpandedX; _fieldOriginY=f.ExpandedY; element.CaptureMouse(); }
    private void ExpandedField_MouseMove(object sender, MouseEventArgs e) { if(e.LeftButton!=MouseButtonState.Pressed||sender is not FrameworkElement {Tag:CustomFieldDefinition f}||Mouse.Captured is Thumb)return; var p=e.GetPosition(ExpandedCanvas); var d=p-_dragOrigin; f.ExpandedX=Math.Max(0,_fieldOriginX+d.X); f.ExpandedY=Math.Max(0,_fieldOriginY+d.Y); Canvas.SetLeft((UIElement)sender,f.ExpandedX); Canvas.SetTop((UIElement)sender,f.ExpandedY); }
    private void ExpandedField_MouseUp(object sender, MouseButtonEventArgs e) { if(sender is FrameworkElement element && Mouse.Captured==element)element.ReleaseMouseCapture(); }

    private void RefreshCompactDesigner()
    {
        var visible = _fields.Where(field => field.ShowOnCollapsed).ToList();
        CompactPalette.ItemsSource = visible;
        CompactCanvas.Children.Clear();
        foreach (var field in visible)
        {
            var host = new Grid { Tag=field, Width=field.CompactWidth, Height=field.CompactHeight, Cursor=Cursors.SizeAll };
            var border = new Border { Background=new SolidColorBrush(Color.FromRgb(29,34,44)), BorderBrush=new SolidColorBrush(Color.FromRgb(57,65,81)), BorderThickness=new Thickness(1), CornerRadius=new CornerRadius(7), Padding=new Thickness(8) };
            border.Child = new TextBlock { Text=field.Name, FontWeight=FontWeights.SemiBold, TextTrimming=TextTrimming.CharacterEllipsis };
            host.Children.Add(border); AddResizeHandles(host, field, DesignerSurface.Compact);
            host.PreviewMouseLeftButtonDown += CompactField_MouseDown; host.PreviewMouseMove += CompactField_MouseMove; host.PreviewMouseLeftButtonUp += CompactField_MouseUp;
            Canvas.SetLeft(host,field.CompactX); Canvas.SetTop(host,field.CompactY); CompactCanvas.Children.Add(host);
        }
    }
    private void CompactPalette_PreviewMouseMove(object sender, MouseEventArgs e) { if(e.LeftButton==MouseButtonState.Pressed && sender is Button {Tag:CustomFieldDefinition field}) DragDrop.DoDragDrop((DependencyObject)sender,field,DragDropEffects.Move); }
    private void CompactCanvas_DragOver(object sender, DragEventArgs e) { e.Effects=e.Data.GetDataPresent(typeof(CustomFieldDefinition))?DragDropEffects.Move:DragDropEffects.None; e.Handled=true; }
    private void CompactCanvas_Drop(object sender, DragEventArgs e) { if(e.Data.GetData(typeof(CustomFieldDefinition)) is not CustomFieldDefinition field)return; var point=e.GetPosition(CompactCanvas); field.CompactX=Math.Max(0,point.X-field.CompactWidth/2); field.CompactY=Math.Max(0,point.Y-field.CompactHeight/2); RefreshCompactDesigner(); }
    private void CompactField_MouseDown(object sender, MouseButtonEventArgs e) { if(IsResizeHandle(e)||sender is not FrameworkElement {Tag:CustomFieldDefinition f} element)return; _selected=f; _dragOrigin=e.GetPosition(CompactCanvas); _fieldOriginX=f.CompactX; _fieldOriginY=f.CompactY; element.CaptureMouse(); }
    private void CompactField_MouseMove(object sender, MouseEventArgs e) { if(e.LeftButton!=MouseButtonState.Pressed||sender is not FrameworkElement {Tag:CustomFieldDefinition f}||Mouse.Captured is Thumb)return; var p=e.GetPosition(CompactCanvas); var d=p-_dragOrigin; f.CompactX=Math.Max(0,_fieldOriginX+d.X); f.CompactY=Math.Max(0,_fieldOriginY+d.Y); Canvas.SetLeft((UIElement)sender,f.CompactX); Canvas.SetTop((UIElement)sender,f.CompactY); }
    private void CompactField_MouseUp(object sender, MouseButtonEventArgs e) { if(sender is FrameworkElement element && Mouse.Captured==element)element.ReleaseMouseCapture(); }
    private void Save_Click(object sender, RoutedEventArgs e) { _definition.Fields.Clear(); foreach(var field in _fields)_definition.Fields.Add(field); DialogResult=true; }
    private void Cancel_Click(object sender, RoutedEventArgs e)=>DialogResult=false;
}
