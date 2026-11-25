using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace WinPopClip.App.Views.Controls;

/// <summary>
/// Renders a tactile lifted ghost preview of a dragged plugin row and a crisp accent insertion
/// indicator line on top of the plugin ListBox during drag-to-reorder.
/// </summary>
public sealed class PluginDragAdorner : Adorner
{
    private readonly VisualCollection _children;
    private readonly Canvas _canvas;
    private readonly Border _ghostCard;
    private readonly Grid _insertionGuide;

    private double _ghostTop;
    private double _insertY;

    public PluginDragAdorner(UIElement adornedElement, ListBoxItem sourceItem, Brush accentBrush, Brush surfaceBrush)
        : base(adornedElement)
    {
        IsHitTestVisible = false;
        _children = new VisualCollection(this);

        var width = Math.Max(80, sourceItem.ActualWidth);
        var height = Math.Max(36, sourceItem.ActualHeight);

        var snapshotBrush = new VisualBrush(sourceItem)
        {
            Opacity = 0.96,
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top
        };

        _ghostCard = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(7),
            Background = surfaceBrush,
            BorderBrush = accentBrush,
            BorderThickness = new Thickness(1.3),
            Opacity = 0.94,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1.015, 1.015),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 20,
                ShadowDepth = 6,
                Opacity = 0.34
            },
            Child = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = snapshotBrush
            }
        };

        // Accent insertion bar with subtle end dots
        _insertionGuide = new Grid
        {
            Width = width,
            Height = 8,
            IsHitTestVisible = false
        };
        _insertionGuide.Children.Add(new Border
        {
            Height = 2.5,
            CornerRadius = new CornerRadius(1.25),
            Background = accentBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0)
        });
        _insertionGuide.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = accentBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        });
        _insertionGuide.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = accentBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        });

        _canvas = new Canvas { IsHitTestVisible = false };
        _canvas.Children.Add(_insertionGuide);
        _canvas.Children.Add(_ghostCard);
        _children.Add(_canvas);
    }

    public void UpdatePosition(double ghostTopY, double insertLineY)
    {
        _ghostTop = ghostTopY;
        _insertY = insertLineY;

        Canvas.SetLeft(_ghostCard, 0);
        Canvas.SetTop(_ghostCard, _ghostTop);

        Canvas.SetLeft(_insertionGuide, 0);
        Canvas.SetTop(_insertionGuide, _insertY - 4);
        InvalidateVisual();
    }

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    protected override Size MeasureOverride(Size constraint)
    {
        _canvas.Measure(constraint);
        return AdornedElement.RenderSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _canvas.Arrange(new Rect(new Point(0, 0), finalSize));
        return finalSize;
    }
}
