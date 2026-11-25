using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace WinPopClip.App.Views.Controls;

/// <summary>
/// Overview navigation card inspired by DropShelf's PageTile:
/// displays compact horizontal row when short and smoothly crossfades (200ms) into an airy card
/// with icon badge, large watermark glyph, and summary pills when tall.
/// </summary>
public sealed class PageTile : Button
{
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(string), typeof(PageTile), new PropertyMetadata(null));

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(PageTile), new PropertyMetadata(null));

    public static readonly DependencyProperty SummaryProperty =
        DependencyProperty.Register(nameof(Summary), typeof(string), typeof(PageTile),
            new PropertyMetadata(null, (el, _) => ((PageTile)el).UpdateItems()));

    private static readonly DependencyPropertyKey ItemsPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(Items), typeof(string[]), typeof(PageTile), new PropertyMetadata(Array.Empty<string>()));

    public static readonly DependencyProperty ItemsProperty = ItemsPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey IsTallPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(IsTall), typeof(bool), typeof(PageTile), new PropertyMetadata(false));

    public static readonly DependencyProperty IsTallProperty = IsTallPropertyKey.DependencyProperty;

    private const double TallThreshold = 134;
    private const double Hysteresis = 6;

    public string? Icon
    {
        get => (string?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Header
    {
        get => (string?)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string? Summary
    {
        get => (string?)GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    public string[] Items => (string[])GetValue(ItemsProperty);
    public bool IsTall => (bool)GetValue(IsTallProperty);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var h = sizeInfo.NewSize.Height;
        if (IsTall ? h < TallThreshold - Hysteresis : h >= TallThreshold + Hysteresis)
        {
            SetValue(IsTallPropertyKey, !IsTall);
        }
    }

    private void UpdateItems()
    {
        var split = Summary?.Split(new[] { " · ", "、", ", " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    ?? Array.Empty<string>();
        SetValue(ItemsPropertyKey, split);
    }
}

public sealed class RampConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double d || parameter is not string p) return 0.0;
        var bounds = p.Split(',');
        if (bounds.Length != 2) return 0.0;
        var from = double.Parse(bounds[0], CultureInfo.InvariantCulture);
        var to = double.Parse(bounds[1], CultureInfo.InvariantCulture);
        return Math.Clamp((d - from) / (to - from), 0.0, 1.0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
