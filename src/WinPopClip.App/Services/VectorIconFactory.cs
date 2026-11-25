using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinPopClip.App.Services;

public sealed record IconPresetOption(string Value, string Label);

/// <summary>
/// Renders crisp, macOS SF-Symbol-inspired monochrome vector icons (symbol:*),
/// PopClip monogram badges (square/circle), and Unicode/emoji fallbacks consistently
/// across the floating toolbar, plugin list, and icon picker.
/// </summary>
public static class VectorIconFactory
{
    private static readonly Dictionary<string, string> SymbolGeometries = new(StringComparer.OrdinalIgnoreCase)
    {
        // Clean 16x16 coordinate space stroke paths (1.5px stroke, rounded caps/joins)
        ["copy"]      = "M 5.5,5.5 H 13.5 A 1.5,1.5 0 0 1 15,7 V 13.5 A 1.5,1.5 0 0 1 13.5,15 H 5.5 A 1.5,1.5 0 0 1 4,13.5 V 7 A 1.5,1.5 0 0 1 5.5,5.5 Z M 11,5.5 V 3.5 A 1.5,1.5 0 0 0 9.5,2 H 2.5 A 1.5,1.5 0 0 0 1,3.5 V 10.5 A 1.5,1.5 0 0 0 2.5,12 H 4",
        ["cut"]       = "M 4.5,4 A 2,2 0 1 1 4.5,8 A 2,2 0 1 1 4.5,4 Z M 4.5,11 A 2,2 0 1 1 4.5,15 A 2,2 0 1 1 4.5,11 Z M 6.2,7.2 L 14,13.5 M 6.2,11.8 L 14,5.5",
        ["paste"]     = "M 6,2 H 10 A 1,1 0 0 1 11,3 V 4.5 H 5 V 3 A 1,1 0 0 1 6,2 Z M 5,3.5 H 3.5 A 1.5,1.5 0 0 0 2,5 V 13.5 A 1.5,1.5 0 0 0 3.5,15 H 12.5 A 1.5,1.5 0 0 0 14,13.5 V 5 A 1.5,1.5 0 0 0 12.5,3.5 H 11",
        ["link"]      = "M 6.5,9.5 L 9.5,6.5 M 8,5 L 10,3 A 2.8,2.8 0 0 1 14,7 L 12,9 M 8,11 L 6,13 A 2.8,2.8 0 0 1 2,9 L 4,7",
        ["search"]    = "M 7,2 A 5,5 0 1 1 6.99,2 Z M 11,11 L 14.5,14.5",
        ["count"]     = "M 3,6 H 13 M 3,10 H 13 M 6,2.5 L 4.8,13.5 M 11.2,2.5 L 10,13.5",
        ["braces"]    = "M 5.5,2.5 C 4,2.5 3.5,3.2 3.5,4.5 V 6.5 C 3.5,7.4 2.8,8 2,8 C 2.8,8 3.5,8.6 3.5,9.5 V 11.5 C 3.5,12.8 4,13.5 5.5,13.5 M 10.5,2.5 C 12,2.5 12.5,3.2 12.5,4.5 V 6.5 C 12.5,7.4 13.2,8 14,8 C 13.2,8 12.5,8.6 12.5,9.5 V 11.5 C 12.5,12.8 12,13.5 10.5,13.5",
        ["translate"] = "M 2,4 H 9 M 5.5,2.5 V 4 M 7.8,4 C 7.2,6.8 5.4,9.2 2.5,10.5 M 3.8,6 C 4.8,7.6 6.2,9 8.2,10 M 9.5,13.5 L 12,6.5 L 14.5,13.5 M 10.3,11.3 H 13.7",
        ["globe"]     = "M 8,1.5 A 6.5,6.5 0 1 1 7.99,1.5 Z M 1.8,8 H 14.2 M 8,1.5 C 10.2,3.6 10.2,12.4 8,14.5 C 5.8,12.4 5.8,3.6 8,1.5 Z",
        ["code"]      = "M 5.5,4.5 L 2,8 L 5.5,11.5 M 10.5,4.5 L 14,8 L 10.5,11.5 M 9,3 L 7,13",
        ["swap"]      = "M 2.5,5.5 H 13.5 M 10.5,2.5 L 13.5,5.5 L 10.5,8.5 M 13.5,10.5 H 2.5 M 5.5,7.5 L 2.5,10.5 L 5.5,13.5",
        ["sparkle"]   = "M 8,1.5 L 9.5,6.5 L 14.5,8 L 9.5,9.5 L 8,14.5 L 6.5,9.5 L 1.5,8 L 6.5,6.5 Z",
        ["terminal"]  = "M 2,3 H 14 A 1,1 0 0 1 15,4 V 12 A 1,1 0 0 1 14,13 H 2 A 1,1 0 0 1 1,12 V 4 A 1,1 0 0 1 2,3 Z M 4.2,6.2 L 6.5,8 L 4.2,9.8 M 8,10 H 11.5",
        ["mail"]      = "M 2,3.5 H 14 A 1,1 0 0 1 15,4.5 V 11.5 A 1,1 0 0 1 14,12.5 H 2 A 1,1 0 0 1 1,11.5 V 4.5 A 1,1 0 0 1 2,3.5 Z M 2,4.5 L 8,8.8 L 14,4.5",
        ["book"]      = "M 2,3.5 C 4,2.8 6.5,3 8,4.2 C 9.5,3 12,2.8 14,3.5 V 12.5 C 12,11.8 9.5,12 8,13.2 C 6.5,12 4,11.8 2,12.5 Z M 8,4.2 V 13.2",
        ["star"]      = "M 8,1.8 L 9.9,5.7 L 14.2,6.3 L 11.1,9.3 L 11.8,13.6 L 8,11.6 L 4.2,13.6 L 4.9,9.3 L 1.8,6.3 L 6.1,5.7 Z",
        ["pin"]       = "M 5.5,2.5 H 10.5 L 9.5,7.5 L 12,9.5 H 4 L 6.5,7.5 Z M 8,9.5 V 14.5",
        ["plus"]      = "M 8,2.5 V 13.5 M 2.5,8 H 13.5",
        ["sun"]       = "M 8,4.8 A 3.2,3.2 0 1 1 7.99,4.8 Z M 8,1.4 V 2.8 M 8,13.2 V 14.6 M 1.4,8 H 2.8 M 13.2,8 H 14.6 M 3.3,3.3 L 4.3,4.3 M 11.7,11.7 L 12.7,12.7 M 12.7,3.3 L 11.7,4.3 M 4.3,11.7 L 3.3,12.7",
        ["moon"]      = "M 13.2,9.8 A 5.4,5.4 0 1 1 6.2,2.8 A 4.4,4.4 0 0 0 13.2,9.8 Z",
        ["contrast"]  = "M 8,1.8 A 6.2,6.2 0 1 1 7.99,1.8 Z M 8,1.8 V 14.2 M 8,4.8 A 3.2,3.2 0 0 1 8,11.2"
    };

    public static readonly IReadOnlyList<IconPresetOption> Presets = new List<IconPresetOption>
    {
        new("symbol:copy", "复制"),
        new("symbol:cut", "剪切"),
        new("symbol:paste", "粘贴"),
        new("symbol:search", "搜索"),
        new("symbol:link", "链接"),
        new("symbol:translate", "翻译"),
        new("symbol:globe", "网页"),
        new("symbol:count", "计数"),
        new("symbol:braces", "JSON"),
        new("symbol:code", "代码"),
        new("symbol:swap", "反转"),
        new("symbol:sparkle", "AI/智能"),
        new("symbol:terminal", "终端"),
        new("symbol:book", "文档"),
        new("symbol:mail", "邮件"),
        new("symbol:star", "收藏"),
        new("symbol:pin", "置顶"),
        new("square AA", "方框 AA"),
        new("square filled Aa", "实心 Aa"),
        new("square filled AI", "实心 AI"),
        new("circle W", "圆框 W"),
        new("circle filled S", "实心 S")
    };

    public static FrameworkElement CreateIcon(
        string? iconSpec,
        Brush foreground,
        Brush inverseForeground,
        double size = 15.0)
    {
        var spec = string.IsNullOrWhiteSpace(iconSpec) ? "symbol:sparkle" : iconSpec.Trim();

        // 1. Crisp macOS-style monochrome vector symbol ("symbol:copy", "symbol:search", etc.)
        if (spec.StartsWith("symbol:", StringComparison.OrdinalIgnoreCase))
        {
            var key = spec["symbol:".Length..].Trim();
            if (!SymbolGeometries.TryGetValue(key, out var pathData))
            {
                pathData = SymbolGeometries["sparkle"];
            }

            return new Viewbox
            {
                Width = size,
                Height = size,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new Canvas
                {
                    Width = 16,
                    Height = 16,
                    Children =
                    {
                        new System.Windows.Shapes.Path
                        {
                            Data = Geometry.Parse(pathData),
                            Stroke = foreground,
                            StrokeThickness = 1.45,
                            StrokeStartLineCap = PenLineCap.Round,
                            StrokeEndLineCap = PenLineCap.Round,
                            StrokeLineJoin = PenLineJoin.Round,
                            Fill = Brushes.Transparent
                        }
                    }
                }
            };
        }

        // 2. PopClip-style rounded square or circular monogram badge ("square filled Aa", "circle W")
        // Unified exact size x size bounding box matching symbol:* icons for consistent visual alignment
        var parts = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 &&
            (parts[0].Equals("square", StringComparison.OrdinalIgnoreCase) ||
             parts[0].Equals("circle", StringComparison.OrdinalIgnoreCase)))
        {
            var isCircle = parts[0].Equals("circle", StringComparison.OrdinalIgnoreCase);
            var isFilled = parts.Length >= 3 && parts[1].Equals("filled", StringComparison.OrdinalIgnoreCase);
            var labelText = isFilled ? string.Join(" ", parts.Skip(2)) : string.Join(" ", parts.Skip(1));
            if (labelText.Length > 3) labelText = labelText[..3];

            var stroke = Math.Max(1.15, size * 0.085);
            var pad = Math.Max(1.4, size * 0.11);
            var fontSize = labelText.Length >= 3
                ? size * 0.44
                : labelText.Length == 2
                    ? size * 0.52
                    : size * 0.62;

            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(isCircle ? size / 2.0 : Math.Max(3.0, size * 0.24)),
                Background = isFilled ? foreground : Brushes.Transparent,
                BorderBrush = foreground,
                BorderThickness = new Thickness(isFilled ? 0 : stroke),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                SnapsToDevicePixels = true,
                Child = new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    Margin = new Thickness(pad),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = labelText,
                        FontSize = fontSize,
                        FontWeight = FontWeights.Bold,
                        Foreground = isFilled ? inverseForeground : foreground,
                        TextAlignment = TextAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };
        }

        // 3. Fallback for user-entered Emoji or short text glyphs in exact unified size x size box
        return new Grid
        {
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = spec,
                        FontSize = size * 0.85,
                        Foreground = foreground,
                        TextAlignment = TextAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
    }

    /// <summary>
    /// Creates the Selector brand vector logo mark for headers and about cards.
    /// </summary>
    public static UIElement CreateBrandLogo(double size = 34)
    {
        var grad = new LinearGradientBrush(
            Color.FromRgb(79, 70, 229),
            Color.FromRgb(124, 58, 237),
            new Point(0, 0),
            new Point(1, 1));

        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(size * 0.27),
            Background = grad,
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            BorderThickness = new Thickness(1)
        });

        // Subtle glass sheen
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(size * 0.25, size * 0.25, size * 0.12, size * 0.12),
            Height = size * 0.45,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(1, 1, 1, 0),
            Background = new LinearGradientBrush(
                Color.FromArgb(65, 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255),
                new Point(0.5, 0),
                new Point(0.5, 1))
        });

        var canvas = new Canvas { Width = 32, Height = 32 };
        // Floating pill above selection
        var pill = new Rectangle
        {
            Width = 17,
            Height = 5.6,
            RadiusX = 2.8,
            RadiusY = 2.8,
            Fill = new SolidColorBrush(Color.FromArgb(245, 255, 255, 255))
        };
        Canvas.SetLeft(pill, 7.5);
        Canvas.SetTop(pill, 6.2);
        canvas.Children.Add(pill);

        // Selection highlight bar
        var highlight = new Rectangle
        {
            Width = 14.5,
            Height = 7.6,
            RadiusX = 2,
            RadiusY = 2,
            Fill = new SolidColorBrush(Color.FromArgb(95, 255, 255, 255))
        };
        Canvas.SetLeft(highlight, 6.5);
        Canvas.SetTop(highlight, 16);
        canvas.Children.Add(highlight);

        var line = new Rectangle
        {
            Width = 10.5,
            Height = 2.3,
            RadiusX = 1.15,
            RadiusY = 1.15,
            Fill = Brushes.White
        };
        Canvas.SetLeft(line, 8.5);
        Canvas.SetTop(line, 18.65);
        canvas.Children.Add(line);

        // I-beam cursor
        var beam = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 23.5,14.5 V 25 M 21.5,14.5 H 25.5 M 21.5,25 H 25.5"),
            Stroke = Brushes.White,
            StrokeThickness = 2.0,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        canvas.Children.Add(beam);

        grid.Children.Add(new Viewbox { Child = canvas });
        return grid;
    }
}
