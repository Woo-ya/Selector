using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinPopClip.App.Views.Controls;

/// <summary>
/// DropShelf-inspired sliding segmented indicator: uses a single full-width pill clipped via
/// animated RectangleGeometry.Rect (220ms, cubic-bezier(0.2, 0, 0, 1)) with synchronized foreground crossfade.
/// Zero layout passes during animation.
/// </summary>
public static class SegmentedIndicator
{
    public static readonly DependencyProperty OwnerProperty = DependencyProperty.RegisterAttached(
        "Owner",
        typeof(ListBox),
        typeof(SegmentedIndicator),
        new PropertyMetadata(null, OnOwnerChanged));

    public static ListBox? GetOwner(DependencyObject element) => (ListBox?)element.GetValue(OwnerProperty);
    public static void SetOwner(DependencyObject element, ListBox? value) => element.SetValue(OwnerProperty, value);

    private static void OnOwnerChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement block || e.NewValue is not ListBox owner) return;

        block.Width = double.NaN;
        block.HorizontalAlignment = HorizontalAlignment.Stretch;
        var clip = new RectangleGeometry();
        block.Clip = clip;
        var placed = false;

        void Place(bool animate)
        {
            if (owner.SelectedItem is not { } selected ||
                owner.ItemContainerGenerator.ContainerFromItem(selected) is not FrameworkElement item ||
                item.ActualWidth <= 0 ||
                VisualTreeHelper.GetParent(block) is not UIElement area)
            {
                block.Visibility = Visibility.Hidden;
                placed = false;
                return;
            }

            var bounds = new Rect(item.TranslatePoint(default, area), item.RenderSize);
            clip.RadiusX = clip.RadiusY = block is Border border ? border.CornerRadius.TopLeft : 0;
            block.Visibility = Visibility.Visible;

            if (animate && placed && owner.IsVisible && SelectorMotion.Enabled)
            {
                clip.BeginAnimation(
                    RectangleGeometry.RectProperty,
                    new RectAnimation(bounds, SelectorMotion.SegmentedSlideDuration)
                    {
                        EasingFunction = SelectorMotion.StandardEase
                    });
                return;
            }

            clip.BeginAnimation(RectangleGeometry.RectProperty, null);
            clip.Rect = bounds;
            placed = true;
        }

        owner.Loaded += (_, _) => owner.Dispatcher.InvokeAsync(() => Place(false));
        owner.IsVisibleChanged += (_, _) =>
        {
            if (owner.IsVisible) owner.Dispatcher.InvokeAsync(() => Place(false));
        };
        owner.SizeChanged += (_, _) => Place(false);
        owner.SelectionChanged += (_, args) =>
        {
            var shouldAnimate = placed;
            owner.Dispatcher.InvokeAsync(() => Place(shouldAnimate));

            if (shouldAnimate &&
                block.Visibility == Visibility.Visible &&
                args.AddedItems.Count > 0 && args.AddedItems[0] is Control added &&
                args.RemovedItems.Count > 0 && args.RemovedItems[0] is Control removed)
            {
                Recolor(added, removed, owner.IsVisible && SelectorMotion.Enabled);
            }
        };
    }

    private static void Recolor(Control added, Control removed, bool animate)
    {
        var addedNow = GetFadingColor(added);
        var removedNow = GetFadingColor(removed);
        added.ClearValue(Control.ForegroundProperty);
        removed.ClearValue(Control.ForegroundProperty);

        if (!animate ||
            added.Foreground is not SolidColorBrush { Color: var selectedColor } ||
            removed.Foreground is not SolidColorBrush { Color: var unselectedColor })
        {
            return;
        }

        FadeForeground(added, addedNow ?? unselectedColor, selectedColor);
        FadeForeground(removed, removedNow ?? selectedColor, unselectedColor);
    }

    private static Color? GetFadingColor(Control item) =>
        item.ReadLocalValue(Control.ForegroundProperty) is SolidColorBrush brush ? brush.Color : null;

    private static void FadeForeground(Control item, Color from, Color to)
    {
        if (from == to) return;
        var brush = new SolidColorBrush(from);
        item.Foreground = brush;
        var anim = new ColorAnimation(to, SelectorMotion.SegmentedSlideDuration)
        {
            EasingFunction = SelectorMotion.StandardEase
        };
        anim.Completed += (_, _) =>
        {
            if (ReferenceEquals(item.ReadLocalValue(Control.ForegroundProperty), brush))
            {
                item.ClearValue(Control.ForegroundProperty);
            }
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }
}
