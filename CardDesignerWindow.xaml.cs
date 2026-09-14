using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Zen;

public partial class CardDesignerWindow : Window
{
    private const double GridSize = 12;
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
    private static double Snap(double value) => Math.Round(value / GridSize) * GridSize;

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
        var order = _fields.Count;
        var field=new CustomFieldDefinition { Name=type == "dropdown" ? "Choice" : type == "checkbox" ? "Option" : "Field", Type=type, X=Math.Max(0,Snap(point.X-108)), Y=Math.Max(0,Snap(point.Y-36)), ExpandedX=12, ExpandedY=12+order*72, CompactX=12, CompactY=12+order*60 };
        if (type=="dropdown") { field.Options.Add("Option 1"); field.Options.Add("Option 2"); }
        _fields.Add(field); RenderFields(); SelectField(field);
    }

    private void RenderFields()
    {
        DesignCanvas.Children.Clear();
        foreach (var field in _fields)
        {
            field.Width=Math.Max(108,Snap(field.Width));
            field.Height=Math.Max(60,Snap(field.Height));
            var host = new Grid { Tag=field, Width=field.Width, Height=field.Height, Cursor=Cursors.SizeAll };
            var border=new Border { Background=Brushes.Transparent, BorderBrush=new SolidColorBrush(Color.FromRgb(139,124,255)), BorderThickness=new Thickness(field==_selected?1:0), Padding=new Thickness(0), Child=BuildFieldPreview(field) };
            host.Children.Add(border); AddResizeHandles(host, field, DesignerSurface.Open);
            host.PreviewMouseLeftButtonDown += Field_MouseDown; host.PreviewMouseMove += Field_MouseMove; host.PreviewMouseLeftButtonUp += Field_MouseUp;
            Canvas.SetLeft(host,field.X); Canvas.SetTop(host,field.Y); DesignCanvas.Children.Add(host);
        }
    }
    private static FrameworkElement BuildFieldPreview(CustomFieldDefinition field)
    {
        if (field.Type == "checkbox")
            return new CheckBox { Content=field.Name, IsChecked=bool.TryParse(field.DefaultValue, out var selected) && selected, FontSize=12, IsHitTestVisible=false, VerticalAlignment=VerticalAlignment.Center };

        var grid = new Grid { IsHitTestVisible=false };
        grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text=field.Name.ToUpperInvariant(), Foreground=new SolidColorBrush(Color.FromRgb(137,146,165)), FontSize=10, FontWeight=FontWeights.Bold, Margin=new Thickness(0,0,0,5) });
        FrameworkElement input;
        if (field.Type == "dropdown")
            input = new ComboBox { ItemsSource=field.Options, SelectedItem=string.IsNullOrWhiteSpace(field.DefaultValue) ? field.Options.FirstOrDefault() : field.DefaultValue, MinHeight=36, FontSize=13, VerticalAlignment=VerticalAlignment.Top };
        else
            input = new TextBox { Text=field.DefaultValue, MinHeight=36, FontSize=13, Padding=new Thickness(9,7,9,7), AcceptsReturn=field.Type=="text", TextWrapping=TextWrapping.Wrap, VerticalContentAlignment=VerticalAlignment.Top, VerticalAlignment=VerticalAlignment.Stretch };
        Grid.SetRow(input, 1);
        grid.Children.Add(input);
        return grid;
    }
    private void AddResizeHandles(Grid host, CustomFieldDefinition field, DesignerSurface surface)
    {
        AddResizeHandle(host, field, surface, true, false, HorizontalAlignment.Right, VerticalAlignment.Stretch, 8, double.NaN, Cursors.SizeWE);
        AddResizeHandle(host, field, surface, false, true, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, 8, Cursors.SizeNS);
        AddResizeHandle(host, field, surface, true, true, HorizontalAlignment.Right, VerticalAlignment.Bottom, 14, 14, Cursors.SizeNWSE);
    }
    private void AddResizeHandle(Grid host, CustomFieldDefinition field, DesignerSurface surface, bool horizontal, bool vertical, HorizontalAlignment horizontalAlignment, VerticalAlignment verticalAlignment, double width, double height, Cursor cursor)
    {
        // A zero-opacity Thumb keeps its full hit area and drag behavior without
        // allowing the platform's default Thumb chrome to leak into the layout.
        var thumb = new Thumb
        {
            HorizontalAlignment=horizontalAlignment,
            VerticalAlignment=verticalAlignment,
            Cursor=cursor,
            Background=Brushes.Transparent,
            Opacity=0,
            Focusable=false,
            IsHitTestVisible=true
        };
        if (!double.IsNaN(width)) thumb.Width=width;
        if (!double.IsNaN(height)) thumb.Height=height;
        double startWidth=0, startHeight=0, accumulatedX=0, accumulatedY=0;
        thumb.DragStarted += (_, _) =>
        {
            startWidth=surface == DesignerSurface.Compact ? field.CompactWidth : surface == DesignerSurface.Expanded ? field.ExpandedWidth : field.Width;
            startHeight=surface == DesignerSurface.Compact ? field.CompactHeight : surface == DesignerSurface.Expanded ? field.ExpandedHeight : field.Height;
            accumulatedX=0; accumulatedY=0;
        };
        thumb.DragDelta += (_, args) =>
        {
            accumulatedX+=args.HorizontalChange;
            accumulatedY+=args.VerticalChange;
            if (surface == DesignerSurface.Compact)
            {
                if (horizontal) field.CompactWidth=Math.Clamp(Snap(startWidth+accumulatedX),60,CompactCanvas.ActualWidth-field.CompactX);
                if (vertical) field.CompactHeight=Math.Clamp(Snap(startHeight+accumulatedY),36,CompactCanvas.ActualHeight-field.CompactY);
                host.Width=field.CompactWidth; host.Height=field.CompactHeight;
            }
            else if (surface == DesignerSurface.Expanded)
            {
                if (horizontal) field.ExpandedWidth=Math.Clamp(Snap(startWidth+accumulatedX),60,ExpandedCanvas.ActualWidth-field.ExpandedX);
                if (vertical) field.ExpandedHeight=Math.Clamp(Snap(startHeight+accumulatedY),36,ExpandedCanvas.ActualHeight-field.ExpandedY);
                host.Width=field.ExpandedWidth; host.Height=field.ExpandedHeight;
            }
            else
            {
                if (horizontal) field.Width=Math.Max(108,Snap(startWidth+accumulatedX));
                if (vertical) field.Height=Math.Max(60,Snap(startHeight+accumulatedY));
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
    private void Field_MouseMove(object sender, MouseEventArgs e) { if(e.LeftButton!=MouseButtonState.Pressed||sender is not FrameworkElement {Tag:CustomFieldDefinition f}||Mouse.Captured is Thumb)return; var p=e.GetPosition(DesignCanvas); var d=p-_dragOrigin; f.X=Math.Max(0,Snap(_fieldOriginX+d.X)); f.Y=Math.Max(0,Snap(_fieldOriginY+d.Y)); Canvas.SetLeft((UIElement)sender,f.X); Canvas.SetTop((UIElement)sender,f.Y); }
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
            field.ExpandedWidth=Math.Min(ExpandedCanvas.Width,Math.Max(60,Snap(field.ExpandedWidth)));
            field.ExpandedHeight=Math.Min(ExpandedCanvas.Height,Math.Max(36,Snap(field.ExpandedHeight)));
            field.ExpandedX=Math.Clamp(Snap(field.ExpandedX),0,ExpandedCanvas.Width-field.ExpandedWidth);
            field.ExpandedY=Math.Clamp(Snap(field.ExpandedY),0,ExpandedCanvas.Height-field.ExpandedHeight);
            var host = new Grid { Tag=field, Width=field.ExpandedWidth, Height=field.ExpandedHeight, Cursor=Cursors.SizeAll };
            var border = new Border { Background=Brushes.Transparent, BorderThickness=new Thickness(0), Padding=new Thickness(0), Child=BuildFieldPreview(field) };
            host.Children.Add(border); AddResizeHandles(host, field, DesignerSurface.Expanded);
            host.PreviewMouseLeftButtonDown += ExpandedField_MouseDown; host.PreviewMouseMove += ExpandedField_MouseMove; host.PreviewMouseLeftButtonUp += ExpandedField_MouseUp;
            Canvas.SetLeft(host,field.ExpandedX); Canvas.SetTop(host,field.ExpandedY); ExpandedCanvas.Children.Add(host);
        }
    }
    private void ExpandedField_MouseDown(object sender, MouseButtonEventArgs e) { if(IsResizeHandle(e)||sender is not FrameworkElement {Tag:CustomFieldDefinition f} element)return; _selected=f; _dragOrigin=e.GetPosition(ExpandedCanvas); _fieldOriginX=f.ExpandedX; _fieldOriginY=f.ExpandedY; element.CaptureMouse(); }
    private void ExpandedField_MouseMove(object sender, MouseEventArgs e) { if(e.LeftButton!=MouseButtonState.Pressed||sender is not FrameworkElement {Tag:CustomFieldDefinition f}||Mouse.Captured is Thumb)return; var p=e.GetPosition(ExpandedCanvas); var d=p-_dragOrigin; f.ExpandedX=Math.Clamp(Snap(_fieldOriginX+d.X),0,Math.Max(0,ExpandedCanvas.ActualWidth-f.ExpandedWidth)); f.ExpandedY=Math.Clamp(Snap(_fieldOriginY+d.Y),0,Math.Max(0,ExpandedCanvas.ActualHeight-f.ExpandedHeight)); Canvas.SetLeft((UIElement)sender,f.ExpandedX); Canvas.SetTop((UIElement)sender,f.ExpandedY); }
    private void ExpandedField_MouseUp(object sender, MouseButtonEventArgs e) { if(sender is FrameworkElement element && Mouse.Captured==element)element.ReleaseMouseCapture(); }

    private void RefreshCompactDesigner()
    {
        var visible = _fields.Where(field => field.ShowOnCollapsed).ToList();
        CompactPalette.ItemsSource = visible;
        CompactCanvas.Children.Clear();
        foreach (var field in visible)
        {
            field.CompactWidth=Math.Min(CompactCanvas.Width,Math.Max(60,Snap(field.CompactWidth)));
            field.CompactHeight=Math.Min(CompactCanvas.Height,Math.Max(36,Snap(field.CompactHeight)));
            field.CompactX=Math.Clamp(Snap(field.CompactX),0,CompactCanvas.Width-field.CompactWidth);
            field.CompactY=Math.Clamp(Snap(field.CompactY),0,CompactCanvas.Height-field.CompactHeight);
            var host = new Grid { Tag=field, Width=field.CompactWidth, Height=field.CompactHeight, Cursor=Cursors.SizeAll };
            var border = new Border { Background=Brushes.Transparent, BorderThickness=new Thickness(0), Padding=new Thickness(0), Child=BuildFieldPreview(field) };
            host.Children.Add(border); AddResizeHandles(host, field, DesignerSurface.Compact);
            host.PreviewMouseLeftButtonDown += CompactField_MouseDown; host.PreviewMouseMove += CompactField_MouseMove; host.PreviewMouseLeftButtonUp += CompactField_MouseUp;
            Canvas.SetLeft(host,field.CompactX); Canvas.SetTop(host,field.CompactY); CompactCanvas.Children.Add(host);
        }
    }
    private void CompactPalette_PreviewMouseMove(object sender, MouseEventArgs e) { if(e.LeftButton==MouseButtonState.Pressed && sender is Button {Tag:CustomFieldDefinition field}) DragDrop.DoDragDrop((DependencyObject)sender,field,DragDropEffects.Move); }
    private void CompactCanvas_DragOver(object sender, DragEventArgs e) { e.Effects=e.Data.GetDataPresent(typeof(CustomFieldDefinition))?DragDropEffects.Move:DragDropEffects.None; e.Handled=true; }
    private void CompactCanvas_Drop(object sender, DragEventArgs e) { if(e.Data.GetData(typeof(CustomFieldDefinition)) is not CustomFieldDefinition field)return; var point=e.GetPosition(CompactCanvas); field.CompactX=Math.Clamp(Snap(point.X-field.CompactWidth/2),0,Math.Max(0,CompactCanvas.ActualWidth-field.CompactWidth)); field.CompactY=Math.Clamp(Snap(point.Y-field.CompactHeight/2),0,Math.Max(0,CompactCanvas.ActualHeight-field.CompactHeight)); RefreshCompactDesigner(); }
    private void CompactField_MouseDown(object sender, MouseButtonEventArgs e) { if(IsResizeHandle(e)||sender is not FrameworkElement {Tag:CustomFieldDefinition f} element)return; _selected=f; _dragOrigin=e.GetPosition(CompactCanvas); _fieldOriginX=f.CompactX; _fieldOriginY=f.CompactY; element.CaptureMouse(); }
    private void CompactField_MouseMove(object sender, MouseEventArgs e) { if(e.LeftButton!=MouseButtonState.Pressed||sender is not FrameworkElement {Tag:CustomFieldDefinition f}||Mouse.Captured is Thumb)return; var p=e.GetPosition(CompactCanvas); var d=p-_dragOrigin; f.CompactX=Math.Clamp(Snap(_fieldOriginX+d.X),0,Math.Max(0,CompactCanvas.ActualWidth-f.CompactWidth)); f.CompactY=Math.Clamp(Snap(_fieldOriginY+d.Y),0,Math.Max(0,CompactCanvas.ActualHeight-f.CompactHeight)); Canvas.SetLeft((UIElement)sender,f.CompactX); Canvas.SetTop((UIElement)sender,f.CompactY); }
    private void CompactField_MouseUp(object sender, MouseButtonEventArgs e) { if(sender is FrameworkElement element && Mouse.Captured==element)element.ReleaseMouseCapture(); }
    private void Save_Click(object sender, RoutedEventArgs e) { _definition.Fields.Clear(); foreach(var field in _fields)_definition.Fields.Add(field); DialogResult=true; }
    private void Cancel_Click(object sender, RoutedEventArgs e)=>DialogResult=false;
}
