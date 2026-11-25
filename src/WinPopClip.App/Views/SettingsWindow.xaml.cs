using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinPopClip.App.Services;
using WinPopClip.App.Views.Controls;
using WinPopClip.Core.Models;
using WinPopClip.Core.Plugins;
using WinPopClip.Core.Storage;

namespace WinPopClip.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly PluginRegistry _pluginRegistry;
    private readonly ThemeService _themeService;
    private readonly DiagnosticsLog _diagnostics;
    private readonly string _userPluginsDir;
    private readonly Dictionary<string, TextBox> _optionEditors = new(StringComparer.OrdinalIgnoreCase);

    private bool _suppressEvents;
    private string? _activePage; // null = Overview, or "General", "Appearance", "Plugins", "Playground"
    private int _navGeneration;

    // Plugin list drag-and-drop reorder state
    private Point _dragStartPoint;
    private double _dragGrabOffsetY;
    private PluginListItem? _dragSourceItem;
    private ListBoxItem? _dragSourceContainer;
    private int _dragSourceIndex = -1;
    private int _dragTargetSlot = -1;
    private bool _isDraggingPlugin;
    private PluginDragAdorner? _dragAdorner;
    private AdornerLayer? _dragAdornerLayer;

    private static readonly (string Key, string Title, string Glyph)[] SubPages =
    {
        ("General", "常规与触发", "\xE713"),
        ("Appearance", "外观、材质与尺寸", "\xE790"),
        ("Plugins", "插件与 PopClip 扩展", "\xE945"),
        ("Playground", "划词测试与诊断", "\xE943")
    };

    private sealed class PluginListItem
    {
        public required PluginManifest Manifest { get; init; }
        public required FrameworkElement IconVisual { get; init; }
        public bool CanMoveUp { get; init; }
        public bool CanMoveDown { get; init; }
        public string Id => Manifest.Id;
        public string Name => Manifest.Name;
        public string Icon => Manifest.Icon;
        public string Description => Manifest.Description;
        public bool Enabled { get; set; }
        public string KindLabel => Manifest.Kind.ToString();
        public string KindShortLabel => Manifest.Kind switch
        {
            PluginKind.Builtin => "内置",
            PluginKind.Url => "URL",
            PluginKind.TextTransform => "文本",
            PluginKind.JavaScript => "JS",
            PluginKind.Shell => "脚本",
            PluginKind.Http => "HTTP",
            _ => Manifest.Kind.ToString()
        };
        public string SourceLabel => Manifest.Source switch
        {
            PluginSourceType.BuiltIn => "官方",
            PluginSourceType.ExternalFile => "外部",
            PluginSourceType.UserCustom => "自定义",
            _ => "插件"
        };
        public string RowToolTip => string.IsNullOrWhiteSpace(Manifest.Description)
            ? $"{Manifest.Name}（按住拖动可调整顺序）"
            : $"{Manifest.Name} — {Manifest.Description}\n按住拖动可快速调整顺序";
    }

    public SettingsWindow(
        SettingsService settingsService,
        PluginRegistry pluginRegistry,
        ThemeService themeService,
        DiagnosticsLog diagnostics,
        string userPluginsDir)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _pluginRegistry = pluginRegistry;
        _themeService = themeService;
        _diagnostics = diagnostics;
        _userPluginsDir = userPluginsDir;

        TitleLogoHost.Content = VectorIconFactory.CreateBrandLogo(18);
        _themeService.ApplyWindowTheme(this);

        PlaygroundBox.Text =
            "欢迎使用 Selector！试着划选下面不同类型的内容（或直接切换到 Edge / 记事本 / VS Code 划词测试）：\n\n" +
            "1. 普通文本：hello selector world （试试单色矢量图标与亚克力/实体/液态玻璃材质，点击浮窗右侧图标可直接切换主题）。\n" +
            "2. 链接识别：https://github.com/wangmingkun148/DropShelf\n" +
            "3. JSON 识别：{\"app\":\"Selector\",\"material\":\"LiquidGlass\",\"size\":\"Custom\"}\n" +
            "4. 选中下方整段 PopClip Snippet 可直接弹出「一键安装」按钮：\n" +
            "#popclip\n" +
            "name: 豆瓣搜索\n" +
            "icon: circle 豆\n" +
            "url: https://www.douban.com/search?q=***";

        _pluginRegistry.PluginsChanged += OnPluginsChanged;
        _settingsService.SettingsChanged += OnSettingsServiceChanged;
        _themeService.ThemeChanged += OnThemeServiceChanged;
        _themeService.ToolbarAppearanceChanged += OnToolbarAppearanceChanged;
        _diagnostics.EntryAdded += OnDiagnosticsEntryAdded;

        DiagnosticsBox.Text = string.Join(Environment.NewLine, _diagnostics.GetSnapshot());

        StateChanged += (_, _) => UpdateCaptionMaximizeGlyph();
        Closed += (_, _) =>
        {
            _diagnostics.EntryAdded -= OnDiagnosticsEntryAdded;
            _pluginRegistry.PluginsChanged -= OnPluginsChanged;
            _settingsService.SettingsChanged -= OnSettingsServiceChanged;
            _themeService.ThemeChanged -= OnThemeServiceChanged;
            _themeService.ToolbarAppearanceChanged -= OnToolbarAppearanceChanged;
        };

        LoadAllSettings();
        PopulateInspectorIconPresets();
        RefreshPluginsList();
        RefreshOverviewSummaries();
        RenderLivePreview();
    }

    // ───────────── Frameless WindowChrome Caption Handlers ─────────────

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestoreClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateCaptionMaximizeGlyph();
    }

    private void OnCloseCaptionClick(object sender, RoutedEventArgs e) => Close();

    private void UpdateCaptionMaximizeGlyph()
    {
        MaxRestoreButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
    }

    public void OpenPage(string? pageKey)
    {
        if (string.IsNullOrWhiteSpace(pageKey))
        {
            NavigateBackToOverview();
            return;
        }
        NavigateToPage(pageKey);
    }

    private void OnContentLayerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Responsive DropShelf PageTile height so tall windows crossfade cleanly into spacious cards
        var available = e.NewSize.Height - (GettingStartedSection.Visibility == Visibility.Visible ? 310 : 175);
        var tileHeight = Math.Clamp(available / 2.0, 96, 178);
        TileGeneral.Height = tileHeight;
        TileAppearance.Height = tileHeight;
        TilePlugins.Height = tileHeight;
        TilePlayground.Height = tileHeight;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_isDraggingPlugin)
            {
                EndPluginDrag(commit: false);
                e.Handled = true;
                return;
            }
            if (_activePage is not null)
            {
                NavigateBackToOverview();
                e.Handled = true;
            }
        }
    }

    // ───────────── Spatial Overview <-> Settings Sheet Navigation (DropShelf Motion) ─────────────

    private void OnPageTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string pageKey)
        {
            NavigateToPage(pageKey);
        }
    }

    private void OnBackToOverviewClick(object sender, RoutedEventArgs e) => NavigateBackToOverview();

    private void OnQuickTestToolbarClick(object sender, RoutedEventArgs e) => NavigateToPage("Playground");

    private void OnDismissGettingStartedClick(object sender, RoutedEventArgs e)
    {
        _settingsService.Settings.ShowGettingStarted = false;
        _settingsService.Save();
        GettingStartedSection.Visibility = Visibility.Collapsed;
    }

    private void NavigateToPage(string pageKey)
    {
        var wasOnOverview = _activePage is null;
        if (_activePage == pageKey) return;

        _navGeneration++;
        var oldIndex = _activePage is null ? -1 : Array.FindIndex(SubPages, p => p.Key == _activePage);
        var newIndex = Array.FindIndex(SubPages, p => p.Key == pageKey);
        _activePage = pageKey;

        ShowOnlySubpage(pageKey);
        RebuildStackEdgeTabs(pageKey);
        SheetHost.IsHitTestVisible = true;

        if (wasOnOverview)
        {
            SheetHost.Visibility = Visibility.Visible;
            if (SelectorMotion.Enabled)
            {
                var dur = SelectorMotion.PageTransitionDuration;
                var ease = SelectorMotion.CubicOutEase;

                OverviewHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
                OverviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.96, dur) { EasingFunction = ease });
                OverviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.96, dur) { EasingFunction = ease });

                SheetHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
                SheetTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(28, 0, dur) { EasingFunction = SelectorMotion.StandardEase });
                SheetScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, 1, dur) { EasingFunction = SelectorMotion.StandardEase });
                SheetScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, 1, dur) { EasingFunction = SelectorMotion.StandardEase });
            }
            else
            {
                OverviewHost.Opacity = 0;
                SheetHost.Opacity = 1;
            }
            OverviewHost.IsHitTestVisible = false;
        }
        else if (SelectorMotion.Enabled)
        {
            // Card-swap glide when jumping between subpages via bottom peeking edges
            var fromX = newIndex > oldIndex ? 36.0 : -36.0;
            SheetPagesContainer.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = SelectorMotion.StandardEase });
            SheetSwapTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = SelectorMotion.StandardEase });
        }
    }

    private void NavigateBackToOverview()
    {
        if (_activePage is null) return;
        var gen = ++_navGeneration;
        _activePage = null;
        RefreshOverviewSummaries();
        OverviewHost.IsHitTestVisible = true;
        SheetHost.IsHitTestVisible = false;

        if (SelectorMotion.Enabled)
        {
            var dur = SelectorMotion.PageBackDuration;
            var ease = SelectorMotion.CubicOutEase;

            var hideSheet = new DoubleAnimation(SheetHost.Opacity, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease };
            hideSheet.Completed += (_, _) =>
            {
                if (gen == _navGeneration && _activePage is null)
                {
                    SheetHost.Visibility = Visibility.Collapsed;
                }
            };
            SheetHost.BeginAnimation(OpacityProperty, hideSheet);
            SheetTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 22, dur) { EasingFunction = ease });
            SheetScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.96, dur) { EasingFunction = ease });
            SheetScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.96, dur) { EasingFunction = ease });

            OverviewHost.BeginAnimation(OpacityProperty, new DoubleAnimation(OverviewHost.Opacity, 1, dur) { EasingFunction = ease });
            OverviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = ease });
            OverviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = ease });
        }
        else
        {
            SheetHost.Visibility = Visibility.Collapsed;
            OverviewHost.Opacity = 1;
        }
    }

    private void ShowOnlySubpage(string pageKey)
    {
        PageGeneral.Visibility = pageKey == "General" ? Visibility.Visible : Visibility.Collapsed;
        PageAppearance.Visibility = pageKey == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        PagePlugins.Visibility = pageKey == "Plugins" ? Visibility.Visible : Visibility.Collapsed;
        PagePlayground.Visibility = pageKey == "Playground" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RebuildStackEdgeTabs(string currentPage)
    {
        StackEdgeTabsPanel.Children.Clear();
        var style = TryFindResource("StackEdgeTabStyle") as Style;
        var iconFont = TryFindResource("IconFont") as FontFamily;

        foreach (var (key, title, glyph) in SubPages)
        {
            if (key == currentPage) continue;

            var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = iconFont,
                FontSize = 12,
                Margin = new Thickness(0, 1, 7, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });

            var btn = new Button { Content = content, Style = style, Tag = key };
            btn.Click += (_, _) => NavigateToPage(key);
            StackEdgeTabsPanel.Children.Add(btn);
        }
    }

    // ───────────── Event Sync & Summaries ─────────────

    private void OnPluginsChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        RefreshPluginsList();
        RefreshOverviewSummaries();
    });

    private void OnSettingsServiceChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _suppressEvents = true;
            var enabled = _settingsService.Settings.Enabled;
            OverviewMasterToggle.IsChecked = enabled;
            GeneralMasterToggle.IsChecked = enabled;
            _suppressEvents = false;
            RefreshOverviewSummaries();
        });
    }

    private void OnThemeServiceChanged(object? sender, ThemeChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            SyncAppearanceControls();
            PopulateInspectorIconPresets();
            RefreshPluginsList();
            RefreshOverviewSummaries();
            RenderLivePreview();
        });
    }

    private void OnToolbarAppearanceChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            SyncAppearanceControls();
            RefreshOverviewSummaries();
            RenderLivePreview();
        });
    }

    private void OnDiagnosticsEntryAdded(object? sender, string line)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (DiagnosticsBox.Text.Length > 0) DiagnosticsBox.AppendText(Environment.NewLine);
            DiagnosticsBox.AppendText(line);
            DiagnosticsBox.ScrollToEnd();
        });
    }

    private void RefreshOverviewSummaries()
    {
        var s = _settingsService.Settings;
        StatusText.Text = s.Enabled ? "正在监听划词" : "划词监听已暂停";
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, s.Enabled ? "SuccessBrush" : "CautionBrush");
        OverviewThemePill.Text = ThemeResolver.GetDisplayName(_themeService.CurrentPreference);

        var triggerLabel = s.TriggerMode switch
        {
            TriggerMode.RequireCtrlKey => "Ctrl + 划词",
            TriggerMode.RequireAltKey => "Alt + 划词",
            _ => "鼠标拖拽划词"
        };
        TileGeneral.Summary = $"{(s.Enabled ? "已启用" : "已暂停")} · {triggerLabel} · {(s.EnableClipboardFallback ? "剪贴板回退" : "仅 UIA")}";

        var scalePercent = Math.Round(ThemeResolver.ClampToolbarScale(s.ToolbarScale) * 100);
        TileAppearance.Summary = $"{ThemeResolver.GetDisplayName(_themeService.CurrentPreference)} · {ThemeResolver.GetMaterialDisplayName(s.ToolbarMaterial)} · 尺寸 {scalePercent:0}% · 圆角 {s.ToolbarCornerRadius:0}px";

        var allPlugins = _pluginRegistry.GetAllPlugins();
        var enabledCount = allPlugins.Count(p => p.Enabled);
        TilePlugins.Summary = $"已启用 {enabledCount} / {allPlugins.Count} 个 · 兼容 PopClip";
        TilePlayground.Summary = "实时划词测试 · 捕获通道与日志诊断";
    }

    private void LoadAllSettings()
    {
        _suppressEvents = true;
        var s = _settingsService.Settings;
        GettingStartedSection.Visibility = s.ShowGettingStarted ? Visibility.Visible : Visibility.Collapsed;
        OverviewMasterToggle.IsChecked = s.Enabled;
        GeneralMasterToggle.IsChecked = s.Enabled;
        SelectListBoxByTag(TriggerModeSegmented, s.TriggerMode.ToString());
        DoubleClickCheck.IsChecked = s.EnableDoubleClickSelection;
        ClipboardFallbackCheck.IsChecked = s.EnableClipboardFallback;
        FullscreenDisableCheck.IsChecked = s.DisableInFullscreen;
        MaxActionsBox.Text = s.MaxVisibleActions.ToString();
        DragThresholdBox.Text = s.DragThresholdPixels.ToString();
        ExcludedAppsBox.Text = string.Join(", ", s.ExcludedApps);
        ClipboardBlockedAppsBox.Text = string.Join(", ", s.ClipboardFallbackBlockedApps);
        _suppressEvents = false;

        SyncAppearanceControls();
    }

    private void SyncAppearanceControls()
    {
        _suppressEvents = true;
        var s = _settingsService.Settings;
        SelectListBoxByTag(ThemeTileList, _themeService.CurrentPreference.ToString());
        SelectListBoxByTag(MaterialSegmented, s.ToolbarMaterial.ToString());
        SelectListBoxByTag(DisplayModeSegmented, s.DisplayMode.ToString());

        var scalePct = Math.Round(ThemeResolver.ClampToolbarScale(s.ToolbarScale) * 100);
        ToolbarScaleSlider.Value = scalePct;
        ToolbarScaleValueText.Text = $"{scalePct:0}%";

        var radius = ThemeResolver.ClampCornerRadius(s.ToolbarCornerRadius);
        CornerRadiusSlider.Value = radius;
        CornerRadiusValueText.Text = $"{radius:0} px";

        var effectiveName = _themeService.IsDarkEffective ? "深色模式" : "浅色模式";
        EffectiveThemeHintText.Text = _themeService.CurrentPreference == ToolbarTheme.System
            ? $"当前跟随 Windows 系统外观（实际生效：{effectiveName}），与划词浮窗、系统托盘右键菜单实时同步。"
            : $"当前已固定为{effectiveName}，切换后立即生效并与划词浮窗、系统托盘实时同步。";
        _suppressEvents = false;
    }

    // ───────────── Live WYSIWYG Preview Stage ─────────────

    private void RenderLivePreview()
    {
        var isDark = _themeService.IsDarkEffective;
        var s = _settingsService.Settings;
        var radius = ThemeResolver.ClampCornerRadius(s.ToolbarCornerRadius);
        var showLabel = s.DisplayMode == ToolbarDisplayMode.IconAndText;
        var metrics = FloatingToolbarWindow.ComputeSizeMetrics(s, showLabel);

        PreviewStageBackdrop.Background = isDark
            ? new LinearGradientBrush(Color.FromRgb(26, 28, 34), Color.FromRgb(36, 30, 48), new Point(0, 0), new Point(1, 1))
            : new LinearGradientBrush(Color.FromRgb(236, 232, 224), Color.FromRgb(235, 232, 248), new Point(0, 0), new Point(1, 1));

        FloatingToolbarWindow.ApplyMaterialVisual(
            PreviewPillBorder,
            PreviewAcrylicNoiseLayer,
            PreviewLiquidGlassSheenLayer,
            PreviewLiquidGlassTopHighlight,
            PreviewLiquidGlassInnerRim,
            null,
            PreviewBottomPointer,
            PreviewPillShadow,
            _themeService.EffectiveTheme,
            s.ToolbarMaterial,
            radius);

        PreviewContentHost.Margin = metrics.ContainerPadding;
        PreviewPillBorder.Width = double.NaN;
        PreviewPillBorder.MinHeight = 0;

        var ptrW = Math.Round(12.0 * metrics.Scale, 1);
        var ptrH = Math.Round(6.0 * metrics.Scale, 1);
        PreviewBottomPointer.Points = new PointCollection { new(0, 0), new(ptrW, 0), new(ptrW / 2.0, ptrH) };

        PreviewContentHost.Children.Clear();
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var fg = isDark ? new SolidColorBrush(Color.FromRgb(248, 248, 248)) : new SolidColorBrush(Color.FromArgb(228, 0, 0, 0));
        var invFg = isDark ? new SolidColorBrush(Color.FromRgb(24, 24, 24)) : Brushes.White;
        var sep = isDark ? new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(22, 0, 0, 0));

        var samples = new (string Icon, string Label)[]
        {
            ("symbol:copy", "复制"),
            ("symbol:search", "搜索"),
            ("symbol:translate", "翻译"),
            ("square AA", "转大写"),
            (_themeService.CurrentPreference switch { ToolbarTheme.Light => "symbol:sun", ToolbarTheme.Dark => "symbol:moon", _ => "symbol:contrast" }, "")
        };

        for (var i = 0; i < samples.Length; i++)
        {
            if (i > 0)
            {
                panel.Children.Add(new Border
                {
                    Width = 1,
                    Height = metrics.SeparatorHeight,
                    Margin = new Thickness(Math.Round(2.5 * metrics.Scale, 1), 0, Math.Round(2.5 * metrics.Scale, 1), 0),
                    Background = sep,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var itemPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(metrics.ButtonPadding.Left * 0.72, metrics.ButtonPadding.Top * 0.52, metrics.ButtonPadding.Right * 0.72, metrics.ButtonPadding.Bottom * 0.52)
            };
            var iconEl = VectorIconFactory.CreateIcon(samples[i].Icon, fg, invFg, size: metrics.IconSize);
            if (showLabel && !string.IsNullOrEmpty(samples[i].Label))
            {
                iconEl.Margin = new Thickness(0, 0, metrics.IconTextGap, 0);
            }
            itemPanel.Children.Add(iconEl);

            if (showLabel && !string.IsNullOrEmpty(samples[i].Label))
            {
                itemPanel.Children.Add(new TextBlock
                {
                    Text = samples[i].Label,
                    FontSize = metrics.FontSize,
                    FontWeight = FontWeights.Medium,
                    Foreground = fg,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            panel.Children.Add(itemPanel);
        }

        PreviewContentHost.Children.Add(panel);
    }

    // ───────────── Appearance & Behavior Control Handlers ─────────────

    private void OnMasterEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var enabled = (sender as CheckBox)?.IsChecked == true;
        _suppressEvents = true;
        OverviewMasterToggle.IsChecked = enabled;
        GeneralMasterToggle.IsChecked = enabled;
        _suppressEvents = false;

        _settingsService.Settings.Enabled = enabled;
        _settingsService.Save();
        RefreshOverviewSummaries();
    }

    private void OnTriggerSegmentedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (TriggerModeSegmented.SelectedItem is ListBoxItem item &&
            Enum.TryParse<TriggerMode>(item.Tag?.ToString(), out var mode))
        {
            _settingsService.Settings.TriggerMode = mode;
            _settingsService.Save();
            RefreshOverviewSummaries();
        }
    }

    private void OnQuickBoolSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var s = _settingsService.Settings;
        s.EnableDoubleClickSelection = DoubleClickCheck.IsChecked == true;
        s.EnableClipboardFallback = ClipboardFallbackCheck.IsChecked == true;
        s.DisableInFullscreen = FullscreenDisableCheck.IsChecked == true;
        _settingsService.Save();
        RefreshOverviewSummaries();
    }

    private void OnSaveGeneralFieldsClick(object sender, RoutedEventArgs e)
    {
        var s = _settingsService.Settings;
        if (int.TryParse(MaxActionsBox.Text.Trim(), out var maxActions))
            s.MaxVisibleActions = Math.Clamp(maxActions, 1, 24);
        if (int.TryParse(DragThresholdBox.Text.Trim(), out var threshold))
            s.DragThresholdPixels = Math.Clamp(threshold, 3, 60);

        s.ExcludedApps = ExcludedAppsBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        s.ClipboardFallbackBlockedApps = ClipboardBlockedAppsBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        _settingsService.Save();
        _themeService.NotifyToolbarAppearanceChanged();
        RefreshOverviewSummaries();
        MessageBox.Show(this, "设置已保存并立即生效。", "Selector", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnThemeTileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (ThemeTileList.SelectedItem is ListBoxItem item &&
            Enum.TryParse<ToolbarTheme>(item.Tag?.ToString(), out var theme))
        {
            _themeService.SetTheme(theme);
        }
    }

    private void OnMaterialSegmentedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (MaterialSegmented.SelectedItem is ListBoxItem item &&
            Enum.TryParse<ToolbarMaterial>(item.Tag?.ToString(), out var material))
        {
            _themeService.SetToolbarMaterial(material);
        }
    }

    private void OnToolbarScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || ToolbarScaleValueText is null) return;
        var scale = ThemeResolver.ClampToolbarScale(e.NewValue / 100.0);
        ToolbarScaleValueText.Text = $"{Math.Round(scale * 100):0}%";
        _themeService.SetToolbarScale(scale);
    }

    private void OnToolbarScaleValueMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToolbarScaleSlider.Value = 100;
            e.Handled = true;
        }
    }

    private void OnCornerRadiusChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || CornerRadiusValueText is null) return;
        var val = ThemeResolver.ClampCornerRadius(e.NewValue);
        CornerRadiusValueText.Text = $"{val:0} px";
        _themeService.SetToolbarCornerRadius(val);
    }

    private void OnDisplayModeSegmentedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (DisplayModeSegmented.SelectedItem is ListBoxItem item &&
            Enum.TryParse<ToolbarDisplayMode>(item.Tag?.ToString(), out var mode))
        {
            _settingsService.Settings.DisplayMode = mode;
            _settingsService.Save();
            _themeService.NotifyToolbarAppearanceChanged();
        }
    }

    // ───────────── Plugin Management Handlers ─────────────

    private void PopulateInspectorIconPresets()
    {
        InspectorIconPresets.Children.Clear();
        var fg = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;
        var invFg = TryFindResource("SurfaceBrush") as Brush ?? Brushes.White;

        foreach (var preset in VectorIconFactory.Presets)
        {
            var btn = new Button
            {
                Content = VectorIconFactory.CreateIcon(preset.Value, fg, invFg, size: 15),
                ToolTip = $"{preset.Label} ({preset.Value})",
                Width = 32,
                Height = 30,
                MinHeight = 0,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 5, 5),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand
            };
            btn.Click += (_, _) => PluginIconBox.Text = preset.Value;
            InspectorIconPresets.Children.Add(btn);
        }
    }

    private void RefreshPluginsList()
    {
        var selectedId = (PluginsListBox.SelectedItem as PluginListItem)?.Id;
        var fg = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;
        var invFg = TryFindResource("SurfaceBrush") as Brush ?? Brushes.White;

        var all = _pluginRegistry.GetAllPlugins();
        var count = all.Count;
        var items = all
            .Select((m, idx) => new PluginListItem
            {
                Manifest = m,
                Enabled = m.Enabled,
                CanMoveUp = idx > 0,
                CanMoveDown = idx < count - 1,
                IconVisual = VectorIconFactory.CreateIcon(m.Icon, fg, invFg, size: 16)
            })
            .ToList();

        PluginsListBox.ItemsSource = items;
        if (selectedId is not null)
        {
            PluginsListBox.SelectedItem = items.FirstOrDefault(i => i.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
        }
        else if (items.Count > 0)
        {
            PluginsListBox.SelectedIndex = 0;
        }
    }

    private void OnPluginToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is PluginListItem item)
        {
            _pluginRegistry.SetEnabled(item.Id, cb.IsChecked == true);
        }
    }

    private void OnPluginSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _optionEditors.Clear();
        OptionsContainer.Children.Clear();
        SaveOptionsButton.Visibility = Visibility.Collapsed;

        if (PluginsListBox.SelectedItem is not PluginListItem item)
        {
            DetailIconHost.Content = null;
            DetailTitleText.Text = "请选择左侧插件";
            DetailMetaText.Text = string.Empty;
            DetailConditionText.Text = string.Empty;
            PluginIconBox.Text = string.Empty;
            SaveIconButton.IsEnabled = false;
            return;
        }

        var m = item.Manifest;
        var fg = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;
        var invFg = TryFindResource("SurfaceBrush") as Brush ?? Brushes.White;

        DetailIconHost.Content = VectorIconFactory.CreateIcon(m.Icon, fg, invFg, size: 16);
        DetailTitleText.Text = m.Name;
        DetailMetaText.Text = $"ID: {m.Id}\n类型: {m.Kind} · 来源: {item.SourceLabel} · 执行后: {m.After}" +
                              (string.IsNullOrWhiteSpace(m.SourceFilePath) ? string.Empty : $"\n文件: {m.SourceFilePath}");
        DetailConditionText.Text =
            $"需求条件: {(m.Requirements.Count > 0 ? string.Join(", ", m.Requirements) : "无")}\n" +
            $"正则过滤: {(string.IsNullOrWhiteSpace(m.Regex) ? "无" : m.Regex)}\n" +
            $"限定进程: {(m.AllowedApps.Count > 0 ? string.Join(", ", m.AllowedApps) : "全部应用")}";

        PluginIconBox.Text = m.Icon;
        SaveIconButton.IsEnabled = true;

        if (m.Options.Count == 0)
        {
            var emptyHint = new TextBlock { Text = "该插件暂无可配置选项。", FontSize = 12 };
            emptyHint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            OptionsContainer.Children.Add(emptyHint);
            return;
        }

        var resolved = _pluginRegistry.GetResolvedOptions(m);
        foreach (var opt in m.Options)
        {
            OptionsContainer.Children.Add(new TextBlock { Text = opt.Label, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 3) });
            if (!string.IsNullOrWhiteSpace(opt.Description))
            {
                var descBlock = new TextBlock { Text = opt.Description, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) };
                descBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                OptionsContainer.Children.Add(descBlock);
            }

            var tb = new TextBox
            {
                Text = resolved.TryGetValue(opt.Key, out var val) ? val : opt.DefaultValue,
                Margin = new Thickness(0, 0, 0, 10)
            };
            _optionEditors[opt.Key] = tb;
            OptionsContainer.Children.Add(tb);
        }

        SaveOptionsButton.Visibility = Visibility.Visible;
    }

    private void OnSaveIconClick(object sender, RoutedEventArgs e)
    {
        if (PluginsListBox.SelectedItem is not PluginListItem item) return;
        _pluginRegistry.SetCustomIcon(item.Id, PluginIconBox.Text);
        _diagnostics.Write($"已更新插件「{item.Name}」图标为: {PluginIconBox.Text.Trim()}");
    }

    private void OnSaveOptionsClick(object sender, RoutedEventArgs e)
    {
        if (PluginsListBox.SelectedItem is not PluginListItem item) return;
        var values = _optionEditors.ToDictionary(kv => kv.Key, kv => kv.Value.Text.Trim());
        _pluginRegistry.SaveOptionValues(item.Id, values);
        MessageBox.Show(this, "插件选项已保存。", "Selector", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnAddPluginClick(object sender, RoutedEventArgs e)
    {
        var dialog = new PluginEditorDialog { Owner = this };
        _themeService.ApplyWindowTheme(dialog);
        if (dialog.ShowDialog() == true && dialog.ResultManifest is not null)
        {
            _pluginRegistry.AddOrUpdateCustomPlugin(dialog.ResultManifest);
        }
    }

    private void OnImportPopClipClick(object sender, RoutedEventArgs e)
    {
        var dialog = new PopClipImportDialog { Owner = this };
        _themeService.ApplyWindowTheme(dialog);
        if (dialog.ShowDialog() == true && dialog.ParsedManifest is not null)
        {
            var added = _pluginRegistry.AddOrUpdateCustomPlugin(dialog.ParsedManifest);
            _diagnostics.Write($"已导入 PopClip 插件: {added.Name} ({added.Kind})");
            MessageBox.Show(this, $"已成功导入并启用 PopClip 插件「{added.Name}」！", "Selector", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnEditPluginClick(object sender, RoutedEventArgs e)
    {
        if (PluginsListBox.SelectedItem is not PluginListItem item) return;
        if (item.Manifest.Source != PluginSourceType.UserCustom)
        {
            MessageBox.Show(this, "内置或外部文件插件可在右侧直接自定义图标与配置项；外部文件也可打开插件目录编辑后点击重载。", "Selector", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new PluginEditorDialog(item.Manifest) { Owner = this };
        _themeService.ApplyWindowTheme(dialog);
        if (dialog.ShowDialog() == true && dialog.ResultManifest is not null)
        {
            _pluginRegistry.AddOrUpdateCustomPlugin(dialog.ResultManifest);
        }
    }

    private void OnDeletePluginClick(object sender, RoutedEventArgs e)
    {
        if (PluginsListBox.SelectedItem is not PluginListItem item) return;
        if (item.Manifest.Source != PluginSourceType.UserCustom)
        {
            MessageBox.Show(this, "内置或外部文件插件不可在此删除，你可以取消勾选将其停用。", "Selector", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, $"确认删除自定义插件「{item.Name}」吗？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _pluginRegistry.RemoveCustomPlugin(item.Id);
        }
    }

    private void OnRowMoveUpClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.DataContext is PluginListItem item)
        {
            _pluginRegistry.MovePlugin(item.Id, -1);
        }
    }

    private void OnRowMoveDownClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.DataContext is PluginListItem item)
        {
            _pluginRegistry.MovePlugin(item.Id, +1);
        }
    }

    // ───────────── Tactile Lifted Drag-and-Drop Plugin Reordering ─────────────

    private void OnPluginsListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveListChild(e.OriginalSource as DependencyObject))
        {
            ResetDragCandidate();
            return;
        }

        var container = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not PluginListItem item)
        {
            ResetDragCandidate();
            return;
        }

        var idx = PluginsListBox.ItemContainerGenerator.IndexFromContainer(container);
        if (idx < 0)
        {
            ResetDragCandidate();
            return;
        }

        _dragStartPoint = e.GetPosition(PluginsListBox);
        _dragGrabOffsetY = e.GetPosition(container).Y;
        _dragSourceContainer = container;
        _dragSourceItem = item;
        _dragSourceIndex = idx;
        _dragTargetSlot = idx;
    }

    private void OnPluginsListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSourceItem is null || _dragSourceContainer is null)
        {
            if (_isDraggingPlugin) EndPluginDrag(commit: false);
            return;
        }

        var pos = e.GetPosition(PluginsListBox);
        if (!_isDraggingPlugin)
        {
            if (Math.Abs(pos.Y - _dragStartPoint.Y) < 5 && Math.Abs(pos.X - _dragStartPoint.X) < 5)
            {
                return;
            }
            BeginPluginDrag(pos);
        }

        UpdatePluginDrag(pos);
        e.Handled = true;
    }

    private void OnPluginsListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingPlugin)
        {
            EndPluginDrag(commit: true);
            e.Handled = true;
        }
        else
        {
            ResetDragCandidate();
        }
    }

    private void OnPluginsListLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isDraggingPlugin)
        {
            EndPluginDrag(commit: false);
        }
    }

    private void BeginPluginDrag(Point pos)
    {
        if (_dragSourceContainer is null) return;

        _isDraggingPlugin = true;
        _dragAdornerLayer = AdornerLayer.GetAdornerLayer(PluginsListBox);
        if (_dragAdornerLayer is not null)
        {
            var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.CornflowerBlue;
            var surface = TryFindResource("FlyoutBrush") as Brush ?? Brushes.White;
            _dragAdorner = new PluginDragAdorner(PluginsListBox, _dragSourceContainer, accent, surface);
            _dragAdornerLayer.Add(_dragAdorner);
        }

        _dragSourceContainer.Opacity = 0.25;
        PluginsListBox.Cursor = Cursors.SizeNS;
        PluginsListBox.CaptureMouse();
        UpdatePluginDrag(pos);
    }

    private void UpdatePluginDrag(Point pos)
    {
        if (_dragSourceContainer is null) return;

        // Smooth edge auto-scroll when dragging near top or bottom of the list
        var scrollViewer = FindVisualDescendant<ScrollViewer>(PluginsListBox);
        if (scrollViewer is not null)
        {
            const double edgeZone = 30.0;
            if (pos.Y < edgeZone && scrollViewer.VerticalOffset > 0)
            {
                scrollViewer.ScrollToVerticalOffset(Math.Max(0, scrollViewer.VerticalOffset - 12));
            }
            else if (pos.Y > PluginsListBox.ActualHeight - edgeZone && scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight)
            {
                scrollViewer.ScrollToVerticalOffset(Math.Min(scrollViewer.ScrollableHeight, scrollViewer.VerticalOffset + 12));
            }
        }

        var count = PluginsListBox.Items.Count;
        var targetSlot = count;
        var insertY = PluginsListBox.ActualHeight - 2;

        for (var i = 0; i < count; i++)
        {
            if (PluginsListBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem itemContainer ||
                itemContainer.ActualHeight <= 0)
            {
                continue;
            }

            var top = itemContainer.TranslatePoint(new Point(0, 0), PluginsListBox).Y;
            var mid = top + itemContainer.ActualHeight * 0.5;
            if (pos.Y < mid)
            {
                targetSlot = i;
                insertY = Math.Clamp(top, 2, PluginsListBox.ActualHeight - 2);
                break;
            }

            targetSlot = i + 1;
            insertY = Math.Clamp(top + itemContainer.ActualHeight, 2, PluginsListBox.ActualHeight - 2);
        }

        _dragTargetSlot = targetSlot;

        var ghostHeight = Math.Max(36, _dragSourceContainer.ActualHeight);
        var ghostTop = Math.Clamp(pos.Y - _dragGrabOffsetY, -4, Math.Max(0, PluginsListBox.ActualHeight - ghostHeight + 4));
        _dragAdorner?.UpdatePosition(ghostTop, insertY);
    }

    private void EndPluginDrag(bool commit)
    {
        var draggedId = _dragSourceItem?.Id;
        var sourceIdx = _dragSourceIndex;
        var targetSlot = _dragTargetSlot;

        if (_dragAdornerLayer is not null && _dragAdorner is not null)
        {
            _dragAdornerLayer.Remove(_dragAdorner);
        }
        _dragAdorner = null;
        _dragAdornerLayer = null;

        if (_dragSourceContainer is not null)
        {
            _dragSourceContainer.Opacity = 1.0;
        }

        _isDraggingPlugin = false;
        PluginsListBox.Cursor = null;
        if (PluginsListBox.IsMouseCaptured)
        {
            PluginsListBox.ReleaseMouseCapture();
        }
        ResetDragCandidate();

        if (commit && draggedId is not null && sourceIdx >= 0 && targetSlot >= 0)
        {
            var finalIndex = targetSlot > sourceIdx ? targetSlot - 1 : targetSlot;
            if (finalIndex != sourceIdx)
            {
                _pluginRegistry.MovePluginToIndex(draggedId, finalIndex);
                if (PluginsListBox.ItemsSource is IEnumerable<PluginListItem> items)
                {
                    PluginsListBox.SelectedItem = items.FirstOrDefault(i => i.Id.Equals(draggedId, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
    }

    private void ResetDragCandidate()
    {
        _dragSourceItem = null;
        _dragSourceContainer = null;
        _dragSourceIndex = -1;
        _dragTargetSlot = -1;
    }

    private static DependencyObject? GetParentObject(DependencyObject dep) =>
        dep is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(dep)
            : LogicalTreeHelper.GetParent(dep);

    private static bool IsInteractiveListChild(DependencyObject? dep)
    {
        while (dep is not null && dep is not ListBoxItem)
        {
            if (dep is ButtonBase or ScrollBar or Thumb)
            {
                return true;
            }
            dep = GetParentObject(dep);
        }
        return false;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = GetParentObject(current);
        }
        return null;
    }

    private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var found = FindVisualDescendant<T>(child);
            if (found is not null) return found;
        }
        return null;
    }

    private void OnReloadPluginsClick(object sender, RoutedEventArgs e)
    {
        _pluginRegistry.Reload();
        _diagnostics.Write($"已重新扫描 plugins/ 目录，当前共 {_pluginRegistry.GetAllPlugins().Count} 个插件");
        MessageBox.Show(this, "已重新扫描并加载全部插件。", "Selector", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnOpenPluginsFolderClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_userPluginsDir);
        Process.Start(new ProcessStartInfo { FileName = _userPluginsDir, UseShellExecute = true });
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e)
    {
        _diagnostics.Clear();
        DiagnosticsBox.Clear();
    }

    private static void SelectListBoxByTag(ListBox listBox, string tag)
    {
        foreach (ListBoxItem item in listBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                listBox.SelectedItem = item;
                return;
            }
        }
    }
}
