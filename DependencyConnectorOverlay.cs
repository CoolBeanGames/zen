using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Zen;

public sealed class DependencyConnectorOverlay : FrameworkElement
{
    private static readonly Pen ConnectorPen = new(
        new SolidColorBrush(Color.FromArgb(210, 139, 124, 255)), 1.6)
    {
        LineJoin = PenLineJoin.Round,
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round
    };
    private static readonly Brush ArrowBrush = new SolidColorBrush(Color.FromArgb(235, 139, 124, 255));
    private FrameworkElement? _layoutRoot;

    public DependencyConnectorOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;
        DataContextChanged += (_, _) => InvalidateVisual();
        Loaded += (_, _) => AttachLayoutRoot();
        Unloaded += (_, _) => DetachLayoutRoot();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (DataContext is not BoardColumn column || _layoutRoot is null || column.IsCollapsed) return;

        var cardBorders = Descendants<Border>(_layoutRoot)
            .Where(border => border.Tag is TaskCard && border.IsVisible && border.ActualHeight > 0)
            .GroupBy(border => ((TaskCard)border.Tag).Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var byIndex = column.Tasks.ToDictionary(task => task.Index);

        foreach (var current in column.Tasks.Where(task => task.BlockedByTaskIds.Count > 0))
        {
            if (!cardBorders.TryGetValue(current.Id, out var currentBorder)) continue;
            var currentPoint = CardPoint(currentBorder);
            for (var blockerOffset = 0; blockerOffset < current.BlockedByTaskIds.Count; blockerOffset++)
            {
                var blockerIndex = current.BlockedByTaskIds[blockerOffset];
                if (!byIndex.TryGetValue(blockerIndex, out var blocker) || !cardBorders.TryGetValue(blocker.Id, out var blockerBorder)) continue;
                var blockerPoint = CardPoint(blockerBorder);
                var spineX = Math.Max(2, Math.Min(currentPoint.X, blockerPoint.X) - 7 - blockerOffset * 3);
                var endpointX = Math.Max(currentPoint.X, blockerPoint.X) + 2;

                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(new Point(endpointX, currentPoint.Y), false, false);
                    context.LineTo(new Point(spineX, currentPoint.Y), true, false);
                    context.LineTo(new Point(spineX, blockerPoint.Y), true, false);
                    context.LineTo(new Point(endpointX, blockerPoint.Y), true, false);
                }
                geometry.Freeze();
                drawingContext.DrawGeometry(null, ConnectorPen, geometry);

                var arrow = new StreamGeometry();
                using (var context = arrow.Open())
                {
                    context.BeginFigure(new Point(endpointX + 6, blockerPoint.Y), true, true);
                    context.LineTo(new Point(endpointX - 1, blockerPoint.Y - 4), true, false);
                    context.LineTo(new Point(endpointX - 1, blockerPoint.Y + 4), true, false);
                }
                arrow.Freeze();
                drawingContext.DrawGeometry(ArrowBrush, null, arrow);
            }
        }
    }

    private Point CardPoint(FrameworkElement element)
    {
        try
        {
            var topLeft = element.TranslatePoint(new Point(0, 0), this);
            return new Point(topLeft.X + 15, topLeft.Y + Math.Min(30, element.ActualHeight / 2));
        }
        catch (InvalidOperationException)
        {
            return new Point();
        }
    }

    private void AttachLayoutRoot()
    {
        _layoutRoot = Parent as FrameworkElement;
        if (_layoutRoot is not null) _layoutRoot.LayoutUpdated += LayoutRoot_LayoutUpdated;
    }

    private void DetachLayoutRoot()
    {
        if (_layoutRoot is not null) _layoutRoot.LayoutUpdated -= LayoutRoot_LayoutUpdated;
        _layoutRoot = null;
    }

    private void LayoutRoot_LayoutUpdated(object? sender, EventArgs e) => InvalidateVisual();

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
