using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using WinPopClip.App.Native;
using WinPopClip.App.Services;
using WinPopClip.Core.Models;
using WinPopClip.Core.Plugins;

namespace WinPopClip.App.Views;

public readonly record struct ToolbarSizeMetrics(
    double Scale,
    double IconSize,
    double FontSize,
    double IconTextGap,
    double ButtonMinWidth,
    double ThemeToggleMinWidth,
    Thickness ButtonPadding,
    Thickness ContainerPadding,
    double SeparatorHeight);

public partial class FloatingToolbarWindow : Window
{
    private static readonly Lazy<ImageBrush> DarkAcrylicNoiseBrush = new(() => CreateNoiseBrush(isDark: true));
    private static readonly Lazy<ImageBrush> LightAcrylicNoiseBrush = new(() => CreateNoiseBrush(isDark: false));

    private IntPtr _hwnd = IntPtr.Zero;
    private readonly DispatcherTimer _toastTimer;
    private string _currentToastText = string.Empty;

    private SelectionContext? _lastContext;
    private IReadOnlyList<MatchedPlugin>? _lastActions;
    private AppSettings? _lastSettings;
    private ToolbarTheme _currentEffectiveTheme = ToolbarTheme.Dark;
    private ToolbarTheme _currentPreference = ToolbarTheme.System;
    private bool _lastPlacedBelow;
    private bool _isClosingAnimated;
    private int _animGeneration;

    public event EventHandler<MatchedPlugin>? ActionInvoked;
    public event EventHandler<string>? ToastCopyRequested;
    public event EventHandler<ToolbarTheme>? ThemeChangeRequested;

    public FloatingToolbarWindow()
    {
        InitializeComponent();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            HideToolbar();
        };
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

    public bool IsScreenPointInside(ScreenPoint pt)
    {
        if (!IsVisible || _isClosingAnimated || _hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!Win32Interop.GetWindowRect(_hwnd, out var rect))
        {
            return false;
        }

        return pt.X >= rect.Left && pt.X <= rect.Right && pt.Y >= rect.Top && pt.Y <= rect.Bottom;
    }

    public static ToolbarSizeMetrics ComputeSizeMetrics(AppSettings settings, bool showLabels)
    {
        var scale = ThemeResolver.ClampToolbarScale(settings.ToolbarScale);
        return new ToolbarSizeMetrics(
            Scale: scale,
            IconSize: Math.Round((showLabels ? 14.5 : 16.0) * scale, 1),
            FontSize: Math.Round(12.5 * scale, 1),
            IconTextGap: Math.Round(6.0 * scale, 1),
            ButtonMinWidth: showLabels ? 0 : Math.Round(32.0 * scale, 1),
            ThemeToggleMinWidth: Math.Round(30.0 * scale, 1),
            ButtonPadding: new Thickness(
                Math.Round((showLabels ? 9.0 : 8.0) * scale, 1),
                Math.Round(6.0 * scale, 1),
                Math.Round((showLabels ? 9.0 : 8.0) * scale, 1),
                Math.Round(6.0 * scale, 1)),
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
        ToolbarTheme effectiveTheme,
        ToolbarTheme preference)
    {
        _animGeneration++;
        _isClosingAnimated = false;
        _toastTimer.Stop();
        _lastContext = context;
        _lastActions = actions;
        _lastSettings = settings;
        _currentEffectiveTheme = effectiveTheme;
        _currentPreference = preference;

        ThemeSwitcherPanel.Visibility = Visibility.Collapsed;
        RenderActionsContent(actions, settings, effectiveTheme, preference);
        PrepareAndPosition(context.SelectionBounds, context.AnchorPoint, showPointer: true, animate: true);
    }

    /// <summary>
    /// Re-renders the currently visible floating toolbar immediately when Theme, Material, CornerRadius, Size or DisplayMode changes.
    /// </summary>
    public void ApplyLiveAppearance(ToolbarTheme effectiveTheme, ToolbarTheme preference, AppSettings settings)
    {
        _currentEffectiveTheme = effectiveTheme;
        _currentPreference = preference;
        _lastSettings = settings;

        if (!IsVisible || _isClosingAnimated) return;

        ApplySurfaceStyling(effectiveTheme, settings.ToolbarMaterial, settings.ToolbarCornerRadius);

        if (ActionsContainer.Visibility == Visibility.Visible && _lastActions is not null)
        {
            var wasSwitcherOpen = ThemeSwitcherPanel.Visibility == Visibility.Visible;
            RenderActionsContent(_lastActions, settings, effectiveTheme, preference);
            if (wasSwitcherOpen)
            {
                BuildThemeSwitcherStrip(effectiveTheme, preference);
                ThemeSwitcherPanel.Visibility = Visibility.Visible;
            }
            if (_lastContext is not null)
            {
                PrepareAndPosition(_lastContext.SelectionBounds, _lastContext.AnchorPoint, showPointer: true, animate: false);
            }
        }
    }

    public void ShowToastMessage(string message, ScreenPoint? anchor, ToolbarTheme effectiveTheme, AppSettings settings)
    {
        _animGeneration++;
        _isClosingAnimated = false;
        _toastTimer.Stop();
        _currentEffectiveTheme = effectiveTheme;
        _lastSettings = settings;
        ApplySurfaceStyling(effectiveTheme, settings.ToolbarMaterial, settings.ToolbarCornerRadius);

        _currentToastText = message;
        ToastTextBlock.Text = message.Length > 260 ? message[..257] + "..." : message;
        ActionsContainer.Visibility = Visibility.Collapsed;
        ThemeSwitcherPanel.Visibility = Visibility.Collapsed;
        ToastBorder.Visibility = Visibility.Visible;

        var point = anchor ?? GetCurrentCursorScreenPoint();
        PrepareAndPosition(null, point, showPointer: false, animate: true);
        _toastTimer.Start();
    }

    /// <summary>
    /// DropShelf-calibrated smooth exit animation (170ms CubicEaseIn, scale 0.94, gentle vertical sink).
    /// </summary>
    public void HideToolbar(bool immediate = false)
    {
        _toastTimer.Stop();
        if (!IsVisible) return;

        if (immediate || !SelectorMotion.Enabled)
        {
            _animGeneration++;
            _isClosingAnimated = false;
            ThemeSwitcherPanel.Visibility = Visibility.Collapsed;
            Hide();
            return;
        }

        if (_isClosingAnimated) return;
        _isClosingAnimated = true;
        var gen = ++_animGeneration;
        var dur = SelectorMotion.ToolbarCloseDuration;
        var ease = SelectorMotion.CubicInEase;

        var fade = new DoubleAnimation(Opacity, 0, dur) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (gen != _animGeneration) return;
            _isClosingAnimated = false;
            ThemeSwitcherPanel.Visibility = Visibility.Collapsed;
            Hide();
        };

        BeginAnimation(OpacityProperty, fade);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_lastPlacedBelow ? -6 : 6, dur) { EasingFunction = ease });
        PopScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, dur) { EasingFunction = ease });
        PopScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, dur) { EasingFunction = ease });
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
                // Aligned with DropShelf ShelfSolidBrush (#FBF9F3 warm paper light / #2B2B2B charcoal dark)
                var bg = isDark
                    ? new SolidColorBrush(Color.FromRgb(43, 43, 43))
                    : new SolidColorBrush(Color.FromRgb(251, 249, 243));
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
                            new(Color.FromArgb(196, 255, 254, 250), 0.0),
                            new(Color.FromArgb(166, 245, 242, 236), 0.55),
                            new(Color.FromArgb(190, 252, 250, 246), 1.0)
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
                    : new SolidColorBrush(Color.FromArgb(188, 249, 247, 243));

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
                        Color.FromArgb(220, 252, 250, 245),
                        Color.FromArgb(214, 245, 242, 235),
                        new Point(0.5, 0), new Point(0.5, 1));

                var border = isDark
                    ? new SolidColorBrush(Color.FromArgb(62, 255, 255, 255))
                    : new SolidColorBrush(Color.FromArgb(165, 255, 255, 255));

                surfaceBorder.Background = acrylicBg;
                surfaceBorder.BorderBrush = border;
                surfaceBorder.BorderThickness = new Thickness(1.1);
                pointerFill = isDark
                    ? new SolidColorBrush(Color.FromArgb(218, 36, 36, 38))
                    : new SolidColorBrush(Color.FromArgb(216, 247, 244, 238));

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
        ToolbarTheme effectiveTheme,
        ToolbarTheme preference)
    {
        ApplySurfaceStyling(effectiveTheme, settings.ToolbarMaterial, settings.ToolbarCornerRadius);

        ToastBorder.Visibility = Visibility.Collapsed;
        ActionsContainer.Visibility = Visibility.Visible;
        ActionsHostGrid.Children.Clear();

        var isDark = effectiveTheme == ToolbarTheme.Dark;
        var showLabel = settings.DisplayMode == ToolbarDisplayMode.IconAndText;
        var metrics = ComputeSizeMetrics(settings, showLabel);
        var btnRadius = Math.Clamp(ThemeResolver.ClampCornerRadius(settings.ToolbarCornerRadius) - 4, 4, 18);

        // Apply proportional overall scale cleanly across container, buttons, separators, and pointers
        ContentStack.Margin = metrics.ContainerPadding;
        PillBorder.Width = double.NaN;
        PillBorder.MinHeight = 0;
        ActionsScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        ActionsScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        ActionsScrollViewer.MaxHeight = double.PositiveInfinity;

        var ptrW = Math.Round(12.0 * metrics.Scale, 1);
        var ptrH = Math.Round(6.0 * metrics.Scale, 1);
        TopPointer.Points = new PointCollection { new(0, ptrH), new(ptrW / 2.0, 0), new(ptrW, ptrH) };
        BottomPointer.Points = new PointCollection { new(0, 0), new(ptrW, 0), new(ptrW / 2.0, ptrH) };

        var itemsPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var fgBrush = isDark ? new SolidColorBrush(Color.FromRgb(248, 248, 248)) : new SolidColorBrush(Color.FromArgb(228, 0, 0, 0));
        var invFgBrush = isDark ? new SolidColorBrush(Color.FromRgb(24, 24, 24)) : new SolidColorBrush(Color.FromRgb(255, 255, 255));
        var hoverBg = isDark ? new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(16, 0, 0, 0));
        var sepBrush = isDark ? new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(22, 0, 0, 0));

        for (var i = 0; i < actions.Count; i++)
        {
            if (i > 0)
            {
                itemsPanel.Children.Add(CreateSeparator(sepBrush, metrics.SeparatorHeight, metrics.Scale));
            }
            itemsPanel.Children.Add(CreateActionButton(actions[i], showLabel, fgBrush, invFgBrush, hoverBg, btnRadius, metrics));
        }

        itemsPanel.Children.Add(CreateSeparator(sepBrush, metrics.SeparatorHeight, metrics.Scale));
        itemsPanel.Children.Add(CreateThemeToggleButton(effectiveTheme, preference, fgBrush, invFgBrush, hoverBg, btnRadius, metrics));

        ActionsHostGrid.Children.Add(itemsPanel);
    }

    private static Border CreateSeparator(Brush sepBrush, double height, double scale) => new()
    {
        Width = 1,
        Height = height,
        Margin = new Thickness(Math.Round(2.0 * scale, 1), 0, Math.Round(2.0 * scale, 1), 0),
        Background = sepBrush,
        VerticalAlignment = VerticalAlignment.Center
    };

    private Button CreateThemeToggleButton(
        ToolbarTheme effectiveTheme,
        ToolbarTheme preference,
        Brush fgBrush,
        Brush invFgBrush,
        Brush hoverBg,
        double btnRadius,
        ToolbarSizeMetrics metrics)
    {
        var symbolKey = preference switch
        {
            ToolbarTheme.Light => "symbol:sun",
            ToolbarTheme.Dark => "symbol:moon",
            _ => "symbol:contrast"
        };
        var effText = effectiveTheme == ToolbarTheme.Dark ? "深色" : "浅色";
        var prefText = ThemeResolver.GetDisplayName(preference);

        var btn = new Button
        {
            Content = VectorIconFactory.CreateIcon(symbolKey, fgBrush, invFgBrush, size: metrics.IconSize),
            ToolTip = $"主题切换（当前：{prefText} · 实际{effText}）",
            Padding = new Thickness(Math.Round(7.0 * metrics.Scale, 1), metrics.ButtonPadding.Top, Math.Round(7.0 * metrics.Scale, 1), metrics.ButtonPadding.Bottom),
            MinWidth = metrics.ThemeToggleMinWidth,
            Margin = new Thickness(1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = BuildSelectablePillTemplate(Brushes.Transparent, hoverBg, btnRadius)
        };

        btn.Click += (_, _) =>
        {
            if (ThemeSwitcherPanel.Visibility == Visibility.Visible)
            {
                ThemeSwitcherPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                BuildThemeSwitcherStrip(_currentEffectiveTheme, _currentPreference);
                ThemeSwitcherPanel.Visibility = Visibility.Visible;
            }

            if (_lastContext is not null)
            {
                PrepareAndPosition(_lastContext.SelectionBounds, _lastContext.AnchorPoint, showPointer: true, animate: false);
            }
        };

        return btn;
    }

    private void BuildThemeSwitcherStrip(ToolbarTheme effectiveTheme, ToolbarTheme preference)
    {
        ThemeOptionsStack.Children.Clear();
        var isDark = effectiveTheme == ToolbarTheme.Dark;
        ThemeSwitcherPanel.Background = isDark
            ? new SolidColorBrush(Color.FromArgb(120, 14, 14, 16))
            : new SolidColorBrush(Color.FromArgb(145, 232, 229, 222));
        ThemeSwitcherPanel.BorderBrush = isDark
            ? new SolidColorBrush(Color.FromArgb(30, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(24, 0, 0, 0));

        var fgBrush = isDark ? new SolidColorBrush(Color.FromRgb(245, 245, 245)) : new SolidColorBrush(Color.FromArgb(228, 0, 0, 0));
        var invFgBrush = isDark ? new SolidColorBrush(Color.FromRgb(20, 20, 20)) : Brushes.White;
        var hoverBg = isDark ? new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(18, 0, 0, 0));
        var activeBg = new SolidColorBrush(isDark ? Color.FromRgb(129, 140, 248) : Color.FromRgb(79, 70, 229));
        var activeFg = isDark ? new SolidColorBrush(Color.FromRgb(15, 17, 23)) : Brushes.White;

        var items = new (ToolbarTheme Mode, string Symbol, string Label)[]
        {
            (ToolbarTheme.Light, "symbol:sun", "浅色"),
            (ToolbarTheme.Dark, "symbol:moon", "深色"),
            (ToolbarTheme.System, "symbol:contrast", "跟随系统")
        };

        foreach (var (mode, symbol, label) in items)
        {
            var isActive = preference == mode;
            var itemFg = isActive ? activeFg : fgBrush;
            var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var iconEl = VectorIconFactory.CreateIcon(symbol, itemFg, invFgBrush, size: 12.5);
            iconEl.Margin = new Thickness(0, 0, 5, 0);
            content.Children.Add(iconEl);
            content.Children.Add(new TextBlock
            {
                Text = isActive ? $"{label} ✓" : label,
                FontSize = 11.5,
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = itemFg,
                VerticalAlignment = VerticalAlignment.Center
            });

            var btn = new Button
            {
                Content = content,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(2, 0, 2, 0),
                Background = isActive ? activeBg : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Focusable = false,
                Cursor = System.Windows.Input.Cursors.Hand,
                Template = BuildSelectablePillTemplate(isActive ? activeBg : Brushes.Transparent, isActive ? activeBg : hoverBg, 5.5)
            };

            btn.Click += (_, _) =>
            {
                ThemeSwitcherPanel.Visibility = Visibility.Collapsed;
                ThemeChangeRequested?.Invoke(this, mode);
            };
            ThemeOptionsStack.Children.Add(btn);
        }
    }

    private Button CreateActionButton(
        MatchedPlugin matched,
        bool showLabel,
        Brush fgBrush,
        Brush invFgBrush,
        Brush hoverBg,
        double btnRadius,
        ToolbarSizeMetrics metrics)
    {
        var contentPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var hasIcon = !string.IsNullOrWhiteSpace(matched.Plugin.Icon);
        if (hasIcon)
        {
            var iconVisual = VectorIconFactory.CreateIcon(matched.Plugin.Icon, fgBrush, invFgBrush, size: metrics.IconSize);
            if (showLabel)
            {
                iconVisual.Margin = new Thickness(0, 0, metrics.IconTextGap, 0);
            }
            contentPanel.Children.Add(iconVisual);
        }

        if (showLabel || !hasIcon)
        {
            contentPanel.Children.Add(new TextBlock
            {
                Text = matched.Plugin.Name,
                FontSize = metrics.FontSize,
                FontWeight = FontWeights.Medium,
                Foreground = fgBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        var btn = new Button
        {
            Content = contentPanel,
            ToolTip = string.IsNullOrWhiteSpace(matched.Plugin.Description)
                ? matched.Plugin.Name
                : $"{matched.Plugin.Name} — {matched.Plugin.Description}",
            Padding = metrics.ButtonPadding,
            MinWidth = metrics.ButtonMinWidth,
            Margin = new Thickness(1),
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

        var screen = WinForms.Screen.FromPoint(new Drawing.Point((int)targetX, (int)topRefY));
        var workArea = screen.WorkingArea;

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

        if (animate && SelectorMotion.Enabled)
        {
            PlayEntranceAnimation(placedBelow);
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

    /// <summary>
    /// DropShelf-calibrated pop-in: 320ms BackEase(0.22) scale 0.88->1.0 + 180ms CubicEaseOut fade + ±12px slide.
    /// </summary>
    private void PlayEntranceAnimation(bool placedBelow)
    {
        var popEase = SelectorMotion.ToolbarPopEase;
        var slideEase = SelectorMotion.StandardEase;
        var dur = SelectorMotion.ToolbarOpenDuration;

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, SelectorMotion.ToolbarFadeInDuration) { EasingFunction = SelectorMotion.CubicOutEase });
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(placedBelow ? -12 : 12, 0, dur) { EasingFunction = slideEase });
        PopScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.88, 1.0, dur) { EasingFunction = popEase });
        PopScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.88, 1.0, dur) { EasingFunction = popEase });
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
