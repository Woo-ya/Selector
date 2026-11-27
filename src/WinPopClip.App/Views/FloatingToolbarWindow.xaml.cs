using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinPopClip.App.Native;
using WinPopClip.App.Services;
using WinPopClip.Core.Models;
using WinPopClip.Core.Plugins;

namespace WinPopClip.App.Views;

public readonly record struct ToolbarSizeMetrics(
    double Scale,
    double IconSize,
    double FontSize,
    double TextOnlyFontSize,
    double IconTextGap,
    double ButtonMinWidth,
    double ButtonMinHeight,
    Thickness ButtonPadding,
    Thickness ContainerPadding,
    double SeparatorHeight);

public partial class FloatingToolbarWindow : Window
{
    private static readonly Lazy<ImageBrush> DarkAcrylicNoiseBrush = new(() => CreateNoiseBrush(isDark: true));
    private static readonly Lazy<ImageBrush> LightAcrylicNoiseBrush = new(() => CreateNoiseBrush(isDark: false));

    private sealed record HitTestRect(int Left, int Top, int Right, int Bottom);

    private IntPtr _hwnd = IntPtr.Zero;
    private volatile HitTestRect? _cachedHitRect;
    private readonly DispatcherTimer _toastTimer;
    private string _currentToastText = string.Empty;

    private SelectionContext? _lastContext;
    private IReadOnlyList<MatchedPlugin>? _lastActions;
    private AppSettings? _lastSettings;
    private ToolbarTheme _currentEffectiveTheme = ToolbarTheme.Dark;
    private bool _lastPlacedBelow;
    private bool _isClosingAnimated;
    private int _animGeneration;

    public event EventHandler<MatchedPlugin>? ActionInvoked;
    public event EventHandler<string>? ToastCopyRequested;

    public FloatingToolbarWindow()
    {
        InitializeComponent();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            HideToolbar();
        };
        SizeChanged += (_, _) => RefreshCachedHitRectFromHwnd();
        LocationChanged += (_, _) => RefreshCachedHitRectFromHwnd();
    }

    private void RefreshCachedHitRectFromHwnd()
    {
        if (!IsVisible || _isClosingAnimated || _hwnd == IntPtr.Zero) return;
        if (Win32Interop.GetWindowRect(_hwnd, out var rect) && rect.Width > 0 && rect.Height > 0)
        {
            _cachedHitRect = new HitTestRect(rect.Left - 2, rect.Top - 2, rect.Right + 2, rect.Bottom + 2);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = Win32Interop.GetWindowLongPtr(_hwnd, Win32Interop.GWL_EXSTYLE).ToInt64();
        exStyle |= Win32Interop.WS_EX_NOACTIVATE | Win32Interop.WS_EX_TOOLWINDOW | Win32Interop.WS_EX_TOPMOST;
        Win32Interop.SetWindowLongPtr(_hwnd, Win32Interop.GWL_EXSTYLE, new IntPtr(exStyle));

        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Interop.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(Win32Interop.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Lock-free, cross-thread-safe hit test read directly by the dedicated low-level mouse hook thread.
    /// Never accesses WPF DependencyProperties or Win32 HWND APIs from the hook thread.
    /// </summary>
    public bool IsScreenPointInside(ScreenPoint pt)
    {
        var rect = _cachedHitRect;
        if (rect is null)
        {
            return false;
        }

        return pt.X >= rect.Left && pt.X <= rect.Right && pt.Y >= rect.Top && pt.Y <= rect.Bottom;
    }

    private void UpdateCachedHitRect(double leftPx, double topPx, double widthPx, double heightPx)
    {
        if (_hwnd != IntPtr.Zero && Win32Interop.GetWindowRect(_hwnd, out var rect) && rect.Width > 0 && rect.Height > 0)
        {
            _cachedHitRect = new HitTestRect(rect.Left - 2, rect.Top - 2, rect.Right + 2, rect.Bottom + 2);
            return;
        }

        var l = (int)Math.Floor(leftPx) - 2;
        var t = (int)Math.Floor(topPx) - 2;
        var r = (int)Math.Ceiling(leftPx + widthPx) + 2;
        var b = (int)Math.Ceiling(topPx + heightPx) + 2;
        _cachedHitRect = new HitTestRect(l, t, r, b);
    }

    protected override void OnClosed(EventArgs e)
    {
        _cachedHitRect = null;
        base.OnClosed(e);
    }

    public static ToolbarSizeMetrics ComputeSizeMetrics(AppSettings settings, bool showLabels)
    {
        var scale = ThemeResolver.ClampToolbarScale(settings.ToolbarScale);
        return new ToolbarSizeMetrics(
            Scale: scale,
            IconSize: Math.Round((showLabels ? 15.0 : 16.0) * scale, 1),
            FontSize: Math.Round(13.0 * scale, 1),
            TextOnlyFontSize: Math.Round(13.5 * scale, 1),
            IconTextGap: Math.Round(5.5 * scale, 1),
            ButtonMinWidth: showLabels ? 0 : Math.Round(32.0 * scale, 1),
            ButtonMinHeight: Math.Round(28.0 * scale, 1),
            ButtonPadding: new Thickness(
                Math.Round((showLabels ? 9.5 : 8.0) * scale, 1),
                Math.Round(5.5 * scale, 1),
                Math.Round((showLabels ? 9.5 : 8.0) * scale, 1),
                Math.Round(5.5 * scale, 1)),
            ContainerPadding: new Thickness(
                Math.Round(6.0 * scale, 1),
                Math.Round(5.0 * scale, 1),
                Math.Round(6.0 * scale, 1),
                Math.Round(5.0 * scale, 1)),
            SeparatorHeight: Math.Round(16.0 * scale, 1));
    }

    public void ShowActions(
        SelectionContext context,
        IReadOnlyList<MatchedPlugin> actions,
        AppSettings settings,
        ToolbarTheme effectiveTheme)
    {
        _animGeneration++;
        _isClosingAnimated = false;
        _toastTimer.Stop();
        _lastContext = context;
        _lastActions = actions;
        _lastSettings = settings;
        _currentEffectiveTheme = effectiveTheme;

        RenderActionsContent(actions, settings, effectiveTheme);
        PrepareAndPosition(context.SelectionBounds, context.AnchorPoint, showPointer: true, animate: true);
    }

    /// <summary>
    /// Re-renders the currently visible floating toolbar immediately when Theme, Material, CornerRadius, Size or DisplayMode changes.
    /// </summary>
    public void ApplyLiveAppearance(ToolbarTheme effectiveTheme, AppSettings settings)
    {
        _currentEffectiveTheme = effectiveTheme;
        _lastSettings = settings;

        if (!IsVisible || _isClosingAnimated) return;

        ApplySurfaceStyling(effectiveTheme, settings.ToolbarMaterial, settings.ToolbarCornerRadius);

        if (ActionsContainer.Visibility == Visibility.Visible && _lastActions is not null)
        {
            RenderActionsContent(_lastActions, settings, effectiveTheme);
            if (_lastContext is not null)
            {
                PrepareAndPosition(_lastContext.SelectionBounds, _lastContext.AnchorPoint, showPointer: true, animate: false);
            }
        }
    }

    public void ShowToastMessage(
        string message,
        ScreenPoint? anchor,
        ToolbarTheme effectiveTheme,
        AppSettings settings,
        bool showCopyButton = true,
        double durationSeconds = 3.5)
    {
        _animGeneration++;
        _isClosingAnimated = false;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(durationSeconds, 1.0, 8.0));
        _currentEffectiveTheme = effectiveTheme;
        _lastSettings = settings;
        ApplySurfaceStyling(effectiveTheme, settings.ToolbarMaterial, settings.ToolbarCornerRadius);

        _currentToastText = message;
        ToastTextBlock.Text = message.Length > 260 ? message[..257] + "..." : message;
        ToastCopyButton.Visibility = showCopyButton ? Visibility.Visible : Visibility.Collapsed;
        ActionsContainer.Visibility = Visibility.Collapsed;
        ToastBorder.Visibility = Visibility.Visible;

        var point = anchor ?? GetCurrentCursorScreenPoint();
        PrepareAndPosition(null, point, showPointer: false, animate: true);
        _toastTimer.Start();
    }

    public void ShowLoadingIndicator(string message, ScreenPoint anchor, ToolbarTheme effectiveTheme, AppSettings settings)
    {
        _animGeneration++;
        _isClosingAnimated = false;
        _toastTimer.Stop();
        _currentEffectiveTheme = effectiveTheme;
        _lastSettings = settings;
        ApplySurfaceStyling(effectiveTheme, settings.ToolbarMaterial, settings.ToolbarCornerRadius);

        _currentToastText = string.Empty;
        ToastTextBlock.Text = message;
        ToastCopyButton.Visibility = Visibility.Collapsed;
        ActionsContainer.Visibility = Visibility.Collapsed;
        ToastBorder.Visibility = Visibility.Visible;

        PrepareAndPosition(null, anchor, showPointer: false, animate: true);
    }

    /// <summary>
    /// Smooth exit animation scaled coherently with the user's popup speed setting.
    /// </summary>
    public void HideToolbar(bool immediate = false)
    {
        _toastTimer.Stop();
        _cachedHitRect = null;
        if (!IsVisible) return;

        var openMs = ResolveConfiguredPopupDurationMs();
        if (immediate || !SelectorMotion.Enabled || openMs <= 0)
        {
            _animGeneration++;
            _isClosingAnimated = false;
            Hide();
            return;
        }

        if (_isClosingAnimated) return;
        _isClosingAnimated = true;
        var gen = ++_animGeneration;
        var closeMs = Math.Clamp((int)Math.Round(openMs * 0.55), 45, 160);
        var dur = TimeSpan.FromMilliseconds(closeMs);
        var ease = SelectorMotion.CubicInEase;

        var fade = new DoubleAnimation(Opacity, 0, dur) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (gen != _animGeneration) return;
            _isClosingAnimated = false;
            Hide();
        };

        BeginAnimation(OpacityProperty, fade);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_lastPlacedBelow ? -5 : 5, dur) { EasingFunction = ease });
        PopScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, dur) { EasingFunction = ease });
        PopScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, dur) { EasingFunction = ease });
    }

    /// <summary>
    /// Shared surface styling helper used by BOTH the live FloatingToolbarWindow and the Settings live preview stage,
    /// guaranteeing 100% WYSIWYG fidelity for Acrylic, Solid, and LiquidGlass materials and custom CornerRadius.
    /// </summary>
    public static void ApplyMaterialVisual(
        Border surfaceBorder,
        Border? acrylicNoiseLayer,
        Border? liquidGlassSheenLayer,
        Border? liquidGlassTopHighlight,
        Border? liquidGlassInnerRim,
        Polygon? topPointer,
        Polygon? bottomPointer,
        DropShadowEffect? shadow,
        ToolbarTheme effectiveTheme,
        ToolbarMaterial material,
        double rawCornerRadius)
    {
        var isDark = effectiveTheme == ToolbarTheme.Dark;
        var radius = ThemeResolver.ClampCornerRadius(rawCornerRadius);
        var innerRadius = Math.Max(2, radius - 1);

        surfaceBorder.CornerRadius = new CornerRadius(radius);
        if (acrylicNoiseLayer is not null) acrylicNoiseLayer.CornerRadius = new CornerRadius(innerRadius);
        if (liquidGlassSheenLayer is not null) liquidGlassSheenLayer.CornerRadius = new CornerRadius(innerRadius);
        if (liquidGlassInnerRim is not null) liquidGlassInnerRim.CornerRadius = new CornerRadius(Math.Max(2, innerRadius - 0.5));
        if (liquidGlassTopHighlight is not null)
        {
            var topR = Math.Max(2, innerRadius - 1);
            liquidGlassTopHighlight.CornerRadius = new CornerRadius(topR, topR, 4, 4);
        }

        Brush pointerFill;

        switch (material)
        {
            case ToolbarMaterial.Solid:
            {
                // Cool-gray light (#F6F6F6) / neutral charcoal dark (#2B2B2B)
                var bg = isDark
                    ? new SolidColorBrush(Color.FromRgb(43, 43, 43))
                    : new SolidColorBrush(Color.FromRgb(246, 246, 246));
                var border = isDark
                    ? new SolidColorBrush(Color.FromArgb(46, 255, 255, 255))
                    : new SolidColorBrush(Color.FromArgb(28, 0, 0, 0));
                surfaceBorder.Background = bg;
                surfaceBorder.BorderBrush = border;
                surfaceBorder.BorderThickness = new Thickness(1);
                pointerFill = bg;

                if (acrylicNoiseLayer is not null) acrylicNoiseLayer.Visibility = Visibility.Collapsed;
                if (liquidGlassSheenLayer is not null) liquidGlassSheenLayer.Visibility = Visibility.Collapsed;
                if (shadow is not null)
                {
                    shadow.BlurRadius = 20;
                    shadow.ShadowDepth = 5;
                    shadow.Opacity = isDark ? 0.36 : 0.16;
                    shadow.Color = Colors.Black;
                }
                break;
            }

            case ToolbarMaterial.LiquidGlass:
            {
                var glassBg = isDark
                    ? new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new(Color.FromArgb(178, 52, 56, 74), 0.0),
                            new(Color.FromArgb(164, 26, 28, 38), 0.52),
                            new(Color.FromArgb(182, 36, 40, 56), 1.0)
                        },
                        new Point(0.2, 0), new Point(0.8, 1))
                    : new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new(Color.FromArgb(204, 250, 250, 252), 0.0),
                            new(Color.FromArgb(178, 246, 246, 246), 0.55),
                            new(Color.FromArgb(198, 246, 246, 248), 1.0)
                        },
                        new Point(0.2, 0), new Point(0.8, 1));

                var prismaticBorder = isDark
                    ? new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new(Color.FromArgb(155, 255, 255, 255), 0.0),
                            new(Color.FromArgb(95, 165, 180, 252), 0.48),
                            new(Color.FromArgb(115, 255, 255, 255), 1.0)
                        },
                        new Point(0, 0), new Point(1, 1))
                    : new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new(Color.FromArgb(245, 255, 255, 255), 0.0),
                            new(Color.FromArgb(120, 129, 140, 248), 0.5),
                            new(Color.FromArgb(215, 255, 255, 255), 1.0)
                        },
                        new Point(0, 0), new Point(1, 1));

                surfaceBorder.Background = glassBg;
                surfaceBorder.BorderBrush = prismaticBorder;
                surfaceBorder.BorderThickness = new Thickness(1.35);
                pointerFill = isDark
                    ? new SolidColorBrush(Color.FromArgb(176, 36, 40, 56))
                    : new SolidColorBrush(Color.FromArgb(194, 246, 246, 246));

                if (acrylicNoiseLayer is not null) acrylicNoiseLayer.Visibility = Visibility.Collapsed;
                if (liquidGlassSheenLayer is not null) liquidGlassSheenLayer.Visibility = Visibility.Visible;
                if (liquidGlassTopHighlight is not null)
                {
                    liquidGlassTopHighlight.Background = isDark
                        ? new LinearGradientBrush(
                            Color.FromArgb(62, 255, 255, 255),
                            Color.FromArgb(0, 255, 255, 255),
                            new Point(0.5, 0), new Point(0.5, 1))
                        : new LinearGradientBrush(
                            Color.FromArgb(185, 255, 255, 255),
                            Color.FromArgb(0, 255, 255, 255),
                            new Point(0.5, 0), new Point(0.5, 1));
                }
                if (liquidGlassInnerRim is not null)
                {
                    liquidGlassInnerRim.BorderBrush = isDark
                        ? new SolidColorBrush(Color.FromArgb(36, 199, 210, 254))
                        : new SolidColorBrush(Color.FromArgb(155, 255, 255, 255));
                }
                if (shadow is not null)
                {
                    shadow.BlurRadius = 26;
                    shadow.ShadowDepth = 7;
                    shadow.Opacity = isDark ? 0.42 : 0.20;
                    shadow.Color = isDark ? Color.FromRgb(12, 14, 30) : Color.FromRgb(30, 36, 62);
                }
                break;
            }

            default: // ToolbarMaterial.Acrylic (aligned with DropShelf ShelfAcrylicTintBrush)
            {
                var acrylicBg = isDark
                    ? new LinearGradientBrush(
                        Color.FromArgb(212, 44, 44, 46),
                        Color.FromArgb(222, 34, 34, 36),
                        new Point(0.5, 0), new Point(0.5, 1))
                    : new LinearGradientBrush(
                        Color.FromArgb(228, 248, 248, 248),
                        Color.FromArgb(224, 246, 246, 246),
                        new Point(0.5, 0), new Point(0.5, 1));

                var border = isDark
                    ? new SolidColorBrush(Color.FromArgb(62, 255, 255, 255))
                    : new SolidColorBrush(Color.FromArgb(165, 255, 255, 255));

                surfaceBorder.Background = acrylicBg;
                surfaceBorder.BorderBrush = border;
                surfaceBorder.BorderThickness = new Thickness(1.1);
                pointerFill = isDark
                    ? new SolidColorBrush(Color.FromArgb(218, 36, 36, 38))
                    : new SolidColorBrush(Color.FromArgb(226, 246, 246, 246));

                if (acrylicNoiseLayer is not null)
                {
                    acrylicNoiseLayer.Background = isDark ? DarkAcrylicNoiseBrush.Value : LightAcrylicNoiseBrush.Value;
                    acrylicNoiseLayer.Visibility = Visibility.Visible;
                }
                if (liquidGlassSheenLayer is not null) liquidGlassSheenLayer.Visibility = Visibility.Collapsed;
                if (shadow is not null)
                {
                    shadow.BlurRadius = 22;
                    shadow.ShadowDepth = 6;
                    shadow.Opacity = isDark ? 0.36 : 0.18;
                    shadow.Color = Colors.Black;
                }
                break;
            }
        }

        if (topPointer is not null) topPointer.Fill = pointerFill;
        if (bottomPointer is not null) bottomPointer.Fill = pointerFill;
    }

    private void ApplySurfaceStyling(ToolbarTheme effectiveTheme, ToolbarMaterial material, double cornerRadius)
    {
        ApplyMaterialVisual(
            PillBorder,
            AcrylicNoiseLayer,
            LiquidGlassSheenLayer,
            LiquidGlassTopHighlight,
            LiquidGlassInnerRim,
            TopPointer,
            BottomPointer,
            PillShadow,
            effectiveTheme,
            material,
            cornerRadius);

        ApplyMaterialVisual(
            ToastBorder,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            effectiveTheme,
            material,
            Math.Min(cornerRadius, 16));

        var isDark = effectiveTheme == ToolbarTheme.Dark;
        ToastTextBlock.Foreground = isDark
            ? new SolidColorBrush(Color.FromRgb(250, 250, 250))
            : new SolidColorBrush(Color.FromArgb(228, 0, 0, 0));
    }

    private void RenderActionsContent(
        IReadOnlyList<MatchedPlugin> actions,
        AppSettings settings,
        ToolbarTheme effectiveTheme)
    {
        ApplySurfaceStyling(effectiveTheme, settings.ToolbarMaterial, settings.ToolbarCornerRadius);

        ToastBorder.Visibility = Visibility.Collapsed;
        ActionsContainer.Visibility = Visibility.Visible;
        ActionsHostGrid.Children.Clear();

        var isDark = effectiveTheme == ToolbarTheme.Dark;
        var hasAnyTextItem = actions.Any(a =>
        {
            var mode = a.Plugin.DisplayMode ?? settings.DisplayMode;
            return mode != ToolbarDisplayMode.IconOnly || string.IsNullOrWhiteSpace(a.Plugin.Icon);
        });
        var baseMetrics = ComputeSizeMetrics(settings, hasAnyTextItem);
        var btnRadius = Math.Clamp(ThemeResolver.ClampCornerRadius(settings.ToolbarCornerRadius) - 4, 4, 18);

        // Apply proportional overall scale cleanly across container, buttons, separators, and pointers.
        // When text buttons appear on the pill, dynamically tighten horizontal padding & separator margins
        // so text labels and neighboring icons stay compact and visually cohesive.
        var containerHorizPad = hasAnyTextItem
            ? Math.Round(4.5 * baseMetrics.Scale, 1)
            : baseMetrics.ContainerPadding.Left;
        ContentStack.Margin = new Thickness(
            containerHorizPad,
            baseMetrics.ContainerPadding.Top,
            containerHorizPad,
            baseMetrics.ContainerPadding.Bottom);
        PillBorder.Width = double.NaN;
        PillBorder.MinHeight = 0;
        ActionsScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        ActionsScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        ActionsScrollViewer.MaxHeight = double.PositiveInfinity;

        var ptrW = Math.Round(12.0 * baseMetrics.Scale, 1);
        var ptrH = Math.Round(6.0 * baseMetrics.Scale, 1);
        TopPointer.Points = new PointCollection { new(0, ptrH), new(ptrW / 2.0, 0), new(ptrW, ptrH) };
        BottomPointer.Points = new PointCollection { new(0, 0), new(ptrW, 0), new(ptrW / 2.0, ptrH) };

        var itemsPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var fgBrush = isDark ? new SolidColorBrush(Color.FromRgb(250, 250, 250)) : new SolidColorBrush(Color.FromArgb(242, 24, 24, 27));
        var invFgBrush = isDark ? new SolidColorBrush(Color.FromRgb(24, 24, 24)) : new SolidColorBrush(Color.FromRgb(255, 255, 255));
        var hoverBg = isDark ? new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(16, 0, 0, 0));
        var sepBrush = isDark ? new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(24, 0, 0, 0));

        for (var i = 0; i < actions.Count; i++)
        {
            if (i > 0)
            {
                itemsPanel.Children.Add(CreateSeparator(sepBrush, baseMetrics.SeparatorHeight, baseMetrics.Scale, compact: hasAnyTextItem));
            }
            var effectiveMode = actions[i].Plugin.DisplayMode ?? settings.DisplayMode;
            itemsPanel.Children.Add(CreateActionButton(actions[i], effectiveMode, settings, fgBrush, invFgBrush, hoverBg, btnRadius, compactForText: hasAnyTextItem));
        }

        ActionsHostGrid.Children.Add(itemsPanel);
    }

    private static Border CreateSeparator(Brush sepBrush, double height, double scale, bool compact) => new()
    {
        Width = 1,
        Height = height,
        Margin = new Thickness(
            Math.Round((compact ? 0.5 : 1.5) * scale, 1),
            0,
            Math.Round((compact ? 0.5 : 1.5) * scale, 1),
            0),
        Background = sepBrush,
        VerticalAlignment = VerticalAlignment.Center
    };

    private Button CreateActionButton(
        MatchedPlugin matched,
        ToolbarDisplayMode displayMode,
        AppSettings settings,
        Brush fgBrush,
        Brush invFgBrush,
        Brush hoverBg,
        double btnRadius,
        bool compactForText)
    {
        var showLabel = displayMode != ToolbarDisplayMode.IconOnly;
        var showIcon = displayMode != ToolbarDisplayMode.TextOnly;
        var metrics = ComputeSizeMetrics(settings, showLabel);

        var contentPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var hasIcon = !string.IsNullOrWhiteSpace(matched.Plugin.Icon);
        if (showIcon && hasIcon)
        {
            var iconVisual = VectorIconFactory.CreateIcon(matched.Plugin.Icon, fgBrush, invFgBrush, size: metrics.IconSize);
            if (showLabel)
            {
                var iconGap = compactForText ? Math.Round(4.0 * metrics.Scale, 1) : metrics.IconTextGap;
                iconVisual.Margin = new Thickness(0, 0, iconGap, 0);
            }
            contentPanel.Children.Add(iconVisual);
        }

        if (showLabel || !hasIcon)
        {
            var isTextOnly = !showIcon || !hasIcon;
            var labelBlock = new TextBlock
            {
                Text = matched.Plugin.Name,
                FontSize = isTextOnly ? metrics.TextOnlyFontSize : metrics.FontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = fgBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0.5)
            };
            TextOptions.SetTextFormattingMode(labelBlock, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(labelBlock, TextRenderingMode.Grayscale);
            contentPanel.Children.Add(labelBlock);
        }

        var horizPad = displayMode switch
        {
            ToolbarDisplayMode.TextOnly => Math.Round((compactForText ? 6.5 : 9.0) * metrics.Scale, 1),
            ToolbarDisplayMode.IconOnly => Math.Round((compactForText ? 5.5 : 7.5) * metrics.Scale, 1),
            _ => Math.Round((compactForText ? 6.5 : 8.5) * metrics.Scale, 1)
        };

        var minWidth = displayMode == ToolbarDisplayMode.IconOnly
            ? Math.Round((compactForText ? 26.5 : 31.0) * metrics.Scale, 1)
            : 0;

        var btn = new Button
        {
            Content = contentPanel,
            ToolTip = string.IsNullOrWhiteSpace(matched.Plugin.Description)
                ? matched.Plugin.Name
                : $"{matched.Plugin.Name} — {matched.Plugin.Description}",
            Padding = new Thickness(horizPad, metrics.ButtonPadding.Top, horizPad, metrics.ButtonPadding.Bottom),
            MinWidth = minWidth,
            MinHeight = metrics.ButtonMinHeight,
            Margin = compactForText ? new Thickness(0, 1, 0, 1) : new Thickness(1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = BuildSelectablePillTemplate(Brushes.Transparent, hoverBg, btnRadius)
        };

        btn.Click += (_, _) => ActionInvoked?.Invoke(this, matched);
        return btn;
    }

    private static ControlTemplate BuildSelectablePillTemplate(Brush normalBg, Brush hoverBg, double cornerRadius)
    {
        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border), "Bd");
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
        borderFactory.SetValue(Border.BackgroundProperty, normalBg);
        borderFactory.SetValue(UIElement.RenderTransformOriginProperty, new Point(0.5, 0.5));
        borderFactory.SetValue(UIElement.RenderTransformProperty, new ScaleTransform(1, 1));
        borderFactory.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });

        var presenterFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        presenterFactory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenterFactory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(presenterFactory);

        template.VisualTree = borderFactory;

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "Bd"));
        template.Triggers.Add(hoverTrigger);

        var pressTrigger = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressTrigger.Setters.Add(new Setter(UIElement.RenderTransformProperty, new ScaleTransform(0.94, 0.94), "Bd"));
        template.Triggers.Add(pressTrigger);

        return template;
    }

    private void PrepareAndPosition(ScreenRect? selectionBounds, ScreenPoint anchorPoint, bool showPointer, bool animate)
    {
        BeginAnimation(OpacityProperty, null);
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }
        UpdateLayout();

        var dpiScaleX = 1.0;
        var dpiScaleY = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
            dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
        }

        var winWidthPx = ActualWidth * dpiScaleX;
        var winHeightPx = ActualHeight * dpiScaleY;

        var targetX = selectionBounds is { IsEmpty: false } b ? b.CenterX : anchorPoint.X;
        var topRefY = selectionBounds is { IsEmpty: false } b2 ? b2.Top : anchorPoint.Y;
        var bottomRefY = selectionBounds is { IsEmpty: false } b3 ? b3.Bottom : anchorPoint.Y + 18;

        var workArea = Win32Interop.GetWorkAreaForPoint((int)targetX, (int)topRefY);

        var gapPx = 6 * dpiScaleY;
        var desiredLeftPx = targetX - winWidthPx / 2.0;
        var desiredTopPx = topRefY - winHeightPx - gapPx;
        var placedBelow = false;

        if (desiredTopPx < workArea.Top + 4)
        {
            desiredTopPx = bottomRefY + gapPx;
            placedBelow = true;
        }

        desiredLeftPx = Math.Clamp(desiredLeftPx, workArea.Left + 4, Math.Max(workArea.Left + 4, workArea.Right - winWidthPx - 4));
        desiredTopPx = Math.Clamp(desiredTopPx, workArea.Top + 4, Math.Max(workArea.Top + 4, workArea.Bottom - winHeightPx - 4));
        _lastPlacedBelow = placedBelow;

        if (showPointer)
        {
            TopPointer.Visibility = placedBelow ? Visibility.Visible : Visibility.Collapsed;
            BottomPointer.Visibility = placedBelow ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            TopPointer.Visibility = Visibility.Collapsed;
            BottomPointer.Visibility = Visibility.Collapsed;
        }

        Left = desiredLeftPx / dpiScaleX;
        Top = desiredTopPx / dpiScaleY;

        if (_hwnd != IntPtr.Zero)
        {
            Win32Interop.SetWindowPos(
                _hwnd,
                Win32Interop.HWND_TOPMOST,
                0, 0, 0, 0,
                Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW);
        }

        UpdateCachedHitRect(desiredLeftPx, desiredTopPx, winWidthPx, winHeightPx);

        var openMs = ResolveConfiguredPopupDurationMs();
        if (animate && SelectorMotion.Enabled && openMs > 0)
        {
            PlayEntranceAnimation(placedBelow, openMs);
        }
        else
        {
            BeginAnimation(OpacityProperty, null);
            SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
            PopScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PopScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            Opacity = 1;
            PopScaleTransform.ScaleX = 1;
            PopScaleTransform.ScaleY = 1;
            SlideTransform.Y = 0;
        }
    }

    private int ResolveConfiguredPopupDurationMs()
    {
        if (_lastSettings is null)
        {
            return ThemeResolver.FastPopupDurationMs;
        }
        return ThemeResolver.ResolvePopupDurationMs(_lastSettings.ToolbarPopupSpeed, _lastSettings.ToolbarPopupDurationMs);
    }

    /// <summary>
    /// Responsive pop-in animation driven by the user's configured popup speed (0 ~ 360ms).
    /// Fast speeds use a crisp decelerating ease with tighter travel; slower speeds add gentle spring overshoot.
    /// </summary>
    private void PlayEntranceAnimation(bool placedBelow, int durationMs)
    {
        var dur = TimeSpan.FromMilliseconds(durationMs);
        var fadeDur = TimeSpan.FromMilliseconds(Math.Max(25, Math.Round(durationMs * 0.65)));
        var isSnappy = durationMs <= 140;
        var popEase = isSnappy ? SelectorMotion.CubicOutEase : SelectorMotion.ToolbarPopEase;
        var slideEase = SelectorMotion.StandardEase;
        var startScale = isSnappy ? 0.93 : 0.89;
        var travelPx = isSnappy ? 7.0 : 11.0;

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, fadeDur) { EasingFunction = SelectorMotion.CubicOutEase });
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(placedBelow ? -travelPx : travelPx, 0, dur) { EasingFunction = slideEase });
        PopScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(startScale, 1.0, dur) { EasingFunction = popEase });
        PopScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(startScale, 1.0, dur) { EasingFunction = popEase });
    }

    private void OnToastCopyClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentToastText))
        {
            ToastCopyRequested?.Invoke(this, _currentToastText);
            HideToolbar(immediate: true);
        }
    }

    private static ScreenPoint GetCurrentCursorScreenPoint()
    {
        return Win32Interop.GetCursorPos(out var pt)
            ? new ScreenPoint(pt.X, pt.Y)
            : new ScreenPoint(400, 300);
    }

    private static ImageBrush CreateNoiseBrush(bool isDark)
    {
        const int size = 64;
        var wb = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[size * size * 4];
        var rng = new Random(4207);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var v = (byte)rng.Next(isDark ? 190 : 30, isDark ? 255 : 140);
            pixels[i + 0] = v;
            pixels[i + 1] = v;
            pixels[i + 2] = v;
            pixels[i + 3] = (byte)rng.Next(0, isDark ? 12 : 10);
        }

        wb.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        wb.Freeze();

        var brush = new ImageBrush(wb)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, size, size),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
        brush.Freeze();
        return brush;
    }
}
