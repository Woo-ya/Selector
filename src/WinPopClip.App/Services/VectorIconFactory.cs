using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Xml.Linq;

namespace WinPopClip.App.Services;

public sealed record IconPresetOption(string Value, string Label);

/// <summary>
/// Renders crisp, macOS SF-Symbol-inspired monochrome vector icons (symbol:*),
/// PopClip monogram badges (square/circle), bundled or user-selected PNG/ICO/JPG/SVG image icons
/// (with automatic Dark/Light monochrome template adaptation), and Unicode/emoji fallbacks consistently
/// across the floating toolbar, plugin list, and icon picker.
/// </summary>
public static class VectorIconFactory
{
    private sealed record CachedRasterIcon(DateTime LastWriteUtc, BitmapSource Bitmap, bool IsMonochromeTemplate);
    private sealed record SvgShapeSpec(
        Geometry Geometry,
        string? FillAttr,
        string? StrokeAttr,
        double StrokeWidth,
        double Opacity,
        FillRule FillRule);
    private sealed record CachedSvgIcon(DateTime LastWriteUtc, double Width, double Height, IReadOnlyList<SvgShapeSpec> Shapes);

    private static readonly ConcurrentDictionary<string, CachedRasterIcon> RasterCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, CachedSvgIcon> SvgCache = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Copies a user-selected custom image/SVG into the persistent custom icons folder so moving or deleting
    /// the original source file never breaks the floating toolbar icon.
    /// </summary>
    public static string SaveCustomIconFile(string sourceFilePath, string pluginId, string iconsDirectory)
    {
        Directory.CreateDirectory(iconsDirectory);
        var ext = System.IO.Path.GetExtension(sourceFilePath);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".png";

        var safeId = Regex.Replace(pluginId ?? "icon", @"[^a-zA-Z0-9_\-\.]", "_");
        if (safeId.Length > 48) safeId = safeId[..48];
        var stamp = DateTime.UtcNow.Ticks.ToString("x")[^6..];
        var destPath = System.IO.Path.Combine(iconsDirectory, $"{safeId}_{stamp}{ext.ToLowerInvariant()}");
        File.Copy(sourceFilePath, destPath, overwrite: true);
        return destPath;
    }

    public static bool TryExtractImageFileSpec(string? iconSpec, out string filePath, out bool forcePreserveColor)
    {
        filePath = string.Empty;
        forcePreserveColor = false;
        if (string.IsNullOrWhiteSpace(iconSpec)) return false;

        var trimmed = iconSpec.Trim();
        if (trimmed.StartsWith("file-color:", StringComparison.OrdinalIgnoreCase))
        {
            filePath = trimmed["file-color:".Length..].Trim().Trim('"');
            forcePreserveColor = true;
            return !string.IsNullOrWhiteSpace(filePath);
        }

        if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            filePath = trimmed["file:".Length..].Trim().Trim('"');
            return !string.IsNullOrWhiteSpace(filePath);
        }

        var unquoted = trimmed.Trim('"');
        if (System.IO.Path.IsPathRooted(unquoted) && File.Exists(unquoted))
        {
            var ext = System.IO.Path.GetExtension(unquoted);
            if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".svg", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                filePath = unquoted;
                return true;
            }
        }

        return false;
    }

    public static FrameworkElement CreateIcon(
        string? iconSpec,
        Brush foreground,
        Brush inverseForeground,
        double size = 15.0)
    {
        var spec = string.IsNullOrWhiteSpace(iconSpec) ? "symbol:sparkle" : iconSpec.Trim();

        // 0. Local image or SVG file icon ("file:<path>", "file-color:<path>", or absolute image path)
        if (TryExtractImageFileSpec(spec, out var filePath, out var forcePreserveColor) && File.Exists(filePath))
        {
            var ext = System.IO.Path.GetExtension(filePath);
            if (ext.Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var svgEl = TryCreateSvgIcon(filePath, foreground, forcePreserveColor, size);
                if (svgEl is not null) return svgEl;
            }
            else
            {
                var imgEl = TryCreateRasterIcon(filePath, foreground, forcePreserveColor, size);
                if (imgEl is not null) return imgEl;
            }
        }

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
        var fallbackText = spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                           spec.StartsWith("file-color:", StringComparison.OrdinalIgnoreCase)
            ? "?"
            : spec;

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
                        Text = fallbackText,
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

    private static FrameworkElement? TryCreateRasterIcon(string filePath, Brush foreground, bool forcePreserveColor, double size)
    {
        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(filePath);
            if (!RasterCache.TryGetValue(filePath, out var cached) || cached.LastWriteUtc != lastWrite)
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 64;
                bmp.StreamSource = stream;
                bmp.EndInit();
                bmp.Freeze();

                var isMono = DetectMonochromeTemplateBitmap(bmp);
                cached = new CachedRasterIcon(lastWrite, bmp, isMono);
                RasterCache[filePath] = cached;
            }

            if (cached.IsMonochromeTemplate && !forcePreserveColor)
            {
                // Hardware-accelerated monochrome template tinting (adapts PopClip black PNGs like g.png / Baidu.png
                // to crisp white in Dark Mode and charcoal in Light Mode!)
                var maskBrush = new ImageBrush(cached.Bitmap)
                {
                    Stretch = Stretch.Uniform
                };
                RenderOptions.SetBitmapScalingMode(maskBrush, BitmapScalingMode.HighQuality);
                maskBrush.Freeze();

                return new Rectangle
                {
                    Width = size,
                    Height = size,
                    Fill = foreground,
                    OpacityMask = maskBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    SnapsToDevicePixels = true
                };
            }

            var img = new Image
            {
                Source = cached.Bitmap,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }
        catch
        {
            return null;
        }
    }

    private static bool DetectMonochromeTemplateBitmap(BitmapSource source)
    {
        try
        {
            var converted = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var w = converted.PixelWidth;
            var h = converted.PixelHeight;
            if (w <= 0 || h <= 0) return false;

            var stride = w * 4;
            var pixels = new byte[stride * h];
            converted.CopyPixels(pixels, stride, 0);

            var stepX = Math.Max(1, w / 32);
            var stepY = Math.Max(1, h / 32);
            var opaqueCount = 0;
            var transparentCount = 0;
            var coloredCount = 0;

            for (var y = 0; y < h; y += stepY)
            {
                var rowOffset = y * stride;
                for (var x = 0; x < w; x += stepX)
                {
                    var idx = rowOffset + x * 4;
                    var b = pixels[idx];
                    var g = pixels[idx + 1];
                    var r = pixels[idx + 2];
                    var a = pixels[idx + 3];

                    if (a < 24)
                    {
                        transparentCount++;
                        continue;
                    }

                    opaqueCount++;
                    var maxC = Math.Max(r, Math.Max(g, b));
                    var minC = Math.Min(r, Math.Min(g, b));
                    if (maxC - minC > 22)
                    {
                        coloredCount++;
                    }
                }
            }

            if (opaqueCount == 0 || transparentCount == 0) return false;
            return (double)coloredCount / opaqueCount < 0.08;
        }
        catch
        {
            return false;
        }
    }

    private static FrameworkElement? TryCreateSvgIcon(string filePath, Brush foreground, bool forcePreserveColor, double size)
    {
        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(filePath);
            if (!SvgCache.TryGetValue(filePath, out var cached) || cached.LastWriteUtc != lastWrite)
            {
                cached = ParseSvgFile(filePath, lastWrite);
                if (cached is null) return null;
                SvgCache[filePath] = cached;
            }

            if (cached.Shapes.Count == 0) return null;

            var canvas = new Canvas
            {
                Width = cached.Width,
                Height = cached.Height
            };

            foreach (var shape in cached.Shapes)
            {
                var fillBrush = ResolveSvgBrush(shape.FillAttr, defaultBrush: foreground, foreground, forcePreserveColor);
                var strokeBrush = ResolveSvgBrush(shape.StrokeAttr, defaultBrush: null, foreground, forcePreserveColor);
                if (fillBrush is null && strokeBrush is null) continue;

                var path = new System.Windows.Shapes.Path
                {
                    Data = shape.Geometry,
                    Fill = fillBrush,
                    Stroke = strokeBrush,
                    StrokeThickness = strokeBrush is not null ? shape.StrokeWidth : 0,
                    Opacity = shape.Opacity
                };
                if (shape.Geometry is PathGeometry pg)
                {
                    pg.FillRule = shape.FillRule;
                }
                canvas.Children.Add(path);
            }

            return new Viewbox
            {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = canvas
            };
        }
        catch
        {
            return null;
        }
    }

    private static CachedSvgIcon? ParseSvgFile(string filePath, DateTime lastWrite)
    {
        var doc = XDocument.Load(filePath);
        var root = doc.Root;
        if (root is null) return null;

        double vbX = 0, vbY = 0, vbW = 24, vbH = 24;
        var viewBox = root.Attribute("viewBox")?.Value;
        if (!string.IsNullOrWhiteSpace(viewBox))
        {
            var nums = Regex.Split(viewBox.Trim(), @"[\s,]+")
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0)
                .ToArray();
            if (nums.Length == 4 && nums[2] > 0 && nums[3] > 0)
            {
                vbX = nums[0];
                vbY = nums[1];
                vbW = nums[2];
                vbH = nums[3];
            }
        }
        else
        {
            var w = ParseSvgNumber(root.Attribute("width")?.Value, 24);
            var h = ParseSvgNumber(root.Attribute("height")?.Value, 24);
            if (w > 0 && h > 0) { vbW = w; vbH = h; }
        }

        var shapes = new List<SvgShapeSpec>();
        CollectSvgShapes(root, shapes, vbX, vbY, inheritedFill: "#000000", inheritedStroke: "none", inheritedStrokeWidth: 1.0, inheritedOpacity: 1.0, inheritedFillRule: FillRule.Nonzero);
        return new CachedSvgIcon(lastWrite, vbW, vbH, shapes);
    }

    private static void CollectSvgShapes(
        XElement element,
        List<SvgShapeSpec> shapes,
        double offsetX,
        double offsetY,
        string? inheritedFill,
        string? inheritedStroke,
        double inheritedStrokeWidth,
        double inheritedOpacity,
        FillRule inheritedFillRule)
    {
        var tag = element.Name.LocalName.ToLowerInvariant();
        if (tag is "defs" or "title" or "desc" or "metadata" or "style") return;

        var fill = GetSvgStyleOrAttr(element, "fill") ?? inheritedFill;
        var stroke = GetSvgStyleOrAttr(element, "stroke") ?? inheritedStroke;
        var strokeWidth = ParseSvgNumber(GetSvgStyleOrAttr(element, "stroke-width"), inheritedStrokeWidth);
        var opacity = inheritedOpacity
                      * ParseSvgNumber(GetSvgStyleOrAttr(element, "opacity"), 1.0)
                      * ParseSvgNumber(GetSvgStyleOrAttr(element, "fill-opacity"), 1.0);
        var fillRuleStr = GetSvgStyleOrAttr(element, "fill-rule");
        var fillRule = string.Equals(fillRuleStr, "evenodd", StringComparison.OrdinalIgnoreCase)
            ? FillRule.EvenOdd
            : string.Equals(fillRuleStr, "nonzero", StringComparison.OrdinalIgnoreCase)
                ? FillRule.Nonzero
                : inheritedFillRule;

        Geometry? geom = null;
        switch (tag)
        {
            case "path":
            {
                var d = element.Attribute("d")?.Value;
                if (!string.IsNullOrWhiteSpace(d))
                {
                    try
                    {
                        var parsed = Geometry.Parse(d);
                        var pg = PathGeometry.CreateFromGeometry(parsed);
                        pg.FillRule = fillRule;
                        geom = pg;
                    }
                    catch { }
                }
                break;
            }
            case "rect":
            {
                var x = ParseSvgNumber(element.Attribute("x")?.Value, 0);
                var y = ParseSvgNumber(element.Attribute("y")?.Value, 0);
                var w = ParseSvgNumber(element.Attribute("width")?.Value, 0);
                var h = ParseSvgNumber(element.Attribute("height")?.Value, 0);
                var rx = ParseSvgNumber(element.Attribute("rx")?.Value, 0);
                var ry = ParseSvgNumber(element.Attribute("ry")?.Value, rx);
                if (w > 0 && h > 0)
                {
                    geom = new RectangleGeometry(new Rect(x, y, w, h), rx, ry);
                }
                break;
            }
            case "circle":
            {
                var cx = ParseSvgNumber(element.Attribute("cx")?.Value, 0);
                var cy = ParseSvgNumber(element.Attribute("cy")?.Value, 0);
                var r = ParseSvgNumber(element.Attribute("r")?.Value, 0);
                if (r > 0) geom = new EllipseGeometry(new Point(cx, cy), r, r);
                break;
            }
            case "ellipse":
            {
                var cx = ParseSvgNumber(element.Attribute("cx")?.Value, 0);
                var cy = ParseSvgNumber(element.Attribute("cy")?.Value, 0);
                var rx = ParseSvgNumber(element.Attribute("rx")?.Value, 0);
                var ry = ParseSvgNumber(element.Attribute("ry")?.Value, 0);
                if (rx > 0 && ry > 0) geom = new EllipseGeometry(new Point(cx, cy), rx, ry);
                break;
            }
            case "line":
            {
                var x1 = ParseSvgNumber(element.Attribute("x1")?.Value, 0);
                var y1 = ParseSvgNumber(element.Attribute("y1")?.Value, 0);
                var x2 = ParseSvgNumber(element.Attribute("x2")?.Value, 0);
                var y2 = ParseSvgNumber(element.Attribute("y2")?.Value, 0);
                geom = new LineGeometry(new Point(x1, y1), new Point(x2, y2));
                break;
            }
            case "polygon":
            case "polyline":
            {
                var ptsRaw = element.Attribute("points")?.Value;
                if (!string.IsNullOrWhiteSpace(ptsRaw))
                {
                    var nums = Regex.Split(ptsRaw.Trim(), @"[\s,]+")
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0)
                        .ToArray();
                    if (nums.Length >= 4)
                    {
                        var fig = new PathFigure { StartPoint = new Point(nums[0], nums[1]), IsClosed = tag == "polygon" };
                        for (var i = 2; i + 1 < nums.Length; i += 2)
                        {
                            fig.Segments.Add(new LineSegment(new Point(nums[i], nums[i + 1]), true));
                        }
                        geom = new PathGeometry(new[] { fig }) { FillRule = fillRule };
                    }
                }
                break;
            }
        }

        if (geom is not null)
        {
            if (Math.Abs(offsetX) > 0.001 || Math.Abs(offsetY) > 0.001)
            {
                geom.Transform = new TranslateTransform(-offsetX, -offsetY);
            }
            geom.Freeze();
            shapes.Add(new SvgShapeSpec(geom, fill, stroke, Math.Max(0.5, strokeWidth), Math.Clamp(opacity, 0.05, 1.0), fillRule));
        }

        foreach (var child in element.Elements())
        {
            CollectSvgShapes(child, shapes, offsetX, offsetY, fill, stroke, strokeWidth, opacity, fillRule);
        }
    }

    private static string? GetSvgStyleOrAttr(XElement el, string name)
    {
        var direct = el.Attribute(name)?.Value;
        if (!string.IsNullOrWhiteSpace(direct)) return direct.Trim();

        var style = el.Attribute("style")?.Value;
        if (string.IsNullOrWhiteSpace(style)) return null;
        foreach (var part in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf(':');
            if (idx <= 0) continue;
            if (part[..idx].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return part[(idx + 1)..].Trim();
            }
        }
        return null;
    }

    private static double ParseSvgNumber(string? raw, double fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var cleaned = Regex.Replace(raw.Trim(), @"(px|pt|em|rem|%)$", string.Empty, RegexOptions.IgnoreCase);
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) ? val : fallback;
    }

    private static Brush? ResolveSvgBrush(string? attr, Brush? defaultBrush, Brush foreground, bool forcePreserveColor)
    {
        if (string.IsNullOrWhiteSpace(attr)) return defaultBrush;
        var trimmed = attr.Trim();
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (trimmed.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            return foreground;
        }

        try
        {
            if (ColorConverter.ConvertFromString(trimmed) is Color c)
            {
                var maxC = Math.Max(c.R, Math.Max(c.G, c.B));
                var minC = Math.Min(c.R, Math.Min(c.G, c.B));
                var isGrayscale = (maxC - minC) <= 18;
                if (isGrayscale && !forcePreserveColor)
                {
                    return foreground;
                }
                var brush = new SolidColorBrush(c);
                brush.Freeze();
                return brush;
            }
        }
        catch { }

        return defaultBrush;
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
