using System.IO;
using System.Windows;
using WinPopClip.App.Native;
using WinPopClip.App.Services;
using WinPopClip.App.Views;
using WinPopClip.Core.Models;
using WinPopClip.Core.Plugins;
using WinPopClip.Core.Plugins.Executors;
using WinPopClip.Core.Storage;

namespace WinPopClip.App;

public partial class App : Application
{
    private SettingsService? _settingsService;
    private ThemeService? _themeService;
    private PluginRegistry? _pluginRegistry;
    private ToolbarCoordinator? _coordinator;
    private TrayIconService? _trayIcon;
    private FloatingToolbarWindow? _toolbarWindow;
    private SettingsWindow? _settingsWindow;
    private DiagnosticsLog? _diagnostics;
    private string _userPluginsDir = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _diagnostics = new DiagnosticsLog();
        _settingsService = new SettingsService();
        _themeService = new ThemeService(_settingsService, _diagnostics);

        _userPluginsDir = AppPaths.GetUserPluginsDirectory();
        Directory.CreateDirectory(_userPluginsDir);

        var bundledPluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        var legacyPluginsDir = AppPaths.GetUserPluginsDirectory(AppPaths.LegacyDataDirectory);
        var providers = new IPluginProvider[]
        {
            new BuiltinPluginsCatalog(),
            new DirectoryJsonPluginProvider(bundledPluginsDir, legacyPluginsDir, _userPluginsDir)
        };

        _pluginRegistry = new PluginRegistry(_settingsService, providers);

        var executors = new IPluginExecutor[]
        {
            new BuiltinExecutor(),
            new UrlTemplateExecutor(),
            new TextTransformExecutor(),
            new ShellCommandExecutor(),
            new HttpWebhookExecutor(),
            new JavaScriptExecutor()
        };

        var engine = new PluginExecutionEngine(executors);
        var matcher = new PluginContextMatcher();
        var activeWindowService = new ActiveWindowService();
        var clipboardGuard = new ClipboardGuard();
        var captureService = new SelectionCaptureService(activeWindowService, clipboardGuard, msg => _diagnostics.Write(msg));

        _toolbarWindow = new FloatingToolbarWindow();
        _coordinator = new ToolbarCoordinator(
            _settingsService,
            _pluginRegistry,
            matcher,
            engine,
            captureService,
            clipboardGuard,
            _toolbarWindow,
            _themeService,
            _diagnostics);

        _trayIcon = new TrayIconService(_settingsService, _themeService);
        _trayIcon.OpenSettingsRequested += (_, _) => OpenSettingsWindow();
        _trayIcon.OpenAppearanceRequested += (_, _) => OpenSettingsWindow("Appearance");
        _trayIcon.ReloadPluginsRequested += (_, _) =>
        {
            _pluginRegistry.Reload();
            _diagnostics.Write("已通过托盘菜单重新加载插件");
            _trayIcon.ShowBalloon("Selector", "插件列表已重新加载");
        };
        _trayIcon.ExitRequested += (_, _) => Shutdown();

        _coordinator.Start();
        _diagnostics.Write(
            $"Selector 已启动 · 主题={ThemeResolver.GetDisplayName(_themeService.CurrentPreference)}" +
            $"（生效：{ThemeResolver.GetDisplayName(_themeService.EffectiveTheme)}）" +
            $" · 材质={ThemeResolver.GetMaterialDisplayName(_themeService.CurrentMaterial)}" +
            $" · 尺寸={_themeService.CurrentToolbarScale * 100:0}%" +
            $" · 圆角={_themeService.CurrentCornerRadius:0}px · 插件={_pluginRegistry.GetAllPlugins().Count}");

        OpenSettingsWindow();
    }

    private void OpenSettingsWindow(string? pageKey = null)
    {
        if (_settingsService is null || _pluginRegistry is null || _themeService is null || _diagnostics is null) return;

        if (_settingsWindow is not null && _settingsWindow.IsLoaded)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }
            if (!string.IsNullOrWhiteSpace(pageKey))
            {
                _settingsWindow.OpenPage(pageKey);
            }
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settingsService, _pluginRegistry, _themeService, _diagnostics, _userPluginsDir);
        _settingsWindow.Show();
        if (!string.IsNullOrWhiteSpace(pageKey))
        {
            _settingsWindow.OpenPage(pageKey);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _coordinator?.Dispose();
        _trayIcon?.Dispose();
        _themeService?.Dispose();
        _toolbarWindow?.Close();
        base.OnExit(e);
    }
}
