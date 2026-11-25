using System.Windows;
using WinPopClip.App.Native;
using WinPopClip.App.Views;
using WinPopClip.Core.Models;
using WinPopClip.Core.Plugins;
using WinPopClip.Core.Storage;

namespace WinPopClip.App.Services;

/// <summary>
/// Orchestrates global input gestures -> selection capture -> plugin matching -> floating toolbar -> plugin execution,
/// and keeps floating toolbar theme, material, and corner radius live-synchronized with ThemeService.
/// </summary>
public sealed class ToolbarCoordinator : IDisposable
{
    private const string DynamicInstallPopClipId = "__dynamic.install-popclip";

    private readonly SettingsService _settingsService;
    private readonly PluginRegistry _pluginRegistry;
    private readonly PluginContextMatcher _matcher;
    private readonly PluginExecutionEngine _engine;
    private readonly SelectionCaptureService _captureService;
    private readonly GlobalInputHookService _hookService;
    private readonly FloatingToolbarWindow _toolbar;
    private readonly ThemeService _themeService;
    private readonly IPluginHostEnvironment _hostEnvironment;
    private readonly DiagnosticsLog _diagnostics;

    private CancellationTokenSource? _pendingCaptureCts;
    private SelectionContext? _currentContext;

    public ToolbarCoordinator(
        SettingsService settingsService,
        PluginRegistry pluginRegistry,
        PluginContextMatcher matcher,
        PluginExecutionEngine engine,
        SelectionCaptureService captureService,
        ClipboardGuard clipboardGuard,
        FloatingToolbarWindow toolbar,
        ThemeService themeService,
        DiagnosticsLog diagnostics)
    {
        _settingsService = settingsService;
        _pluginRegistry = pluginRegistry;
        _matcher = matcher;
        _engine = engine;
        _captureService = captureService;
        _toolbar = toolbar;
        _themeService = themeService;
        _diagnostics = diagnostics;

        _hostEnvironment = new AppHostEnvironment(clipboardGuard, (msg, anchor) =>
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                _toolbar.ShowToastMessage(msg, anchor, _themeService.EffectiveTheme, _settingsService.Settings);
            });
        });

        _toolbar.ActionInvoked += OnToolbarActionInvoked;
        _toolbar.ToastCopyRequested += async (_, text) =>
        {
            await _hostEnvironment.CopyTextAsync(text);
        };
        _toolbar.ThemeChangeRequested += (_, requestedTheme) =>
        {
            _themeService.SetTheme(requestedTheme);
        };

        _themeService.ToolbarAppearanceChanged += OnAppearanceChanged;

        _hookService = new GlobalInputHookService(
            pt => _toolbar.IsScreenPointInside(pt),
            () => _settingsService.Settings,
            msg => _diagnostics.Write(msg));

        clipboardGuard.SetSyntheticInputSuppression = suppress => _hookService.SuppressKeyboardDismiss = suppress;

        _hookService.SelectionGestureCompleted += OnSelectionGestureCompleted;
        _hookService.DismissRequested += OnDismissRequested;
    }

    public void Start() => _hookService.Start();

    public void Stop() => _hookService.Stop();

    private void OnAppearanceChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            _toolbar.ApplyLiveAppearance(_themeService.EffectiveTheme, _themeService.CurrentPreference, _settingsService.Settings);
        });
    }

    public void ShowToolbarForContext(SelectionContext rawContext)
    {
        var enriched = _matcher.EnrichContext(rawContext);
        var settings = _settingsService.Settings;
        var matched = _matcher.MatchPlugins(_pluginRegistry.GetAllPlugins(), enriched, settings.MaxVisibleActions).ToList();

        if (PopClipSnippetParser.LooksLikePopClipSnippet(enriched.Text) &&
            PopClipSnippetParser.TryParseSnippet(enriched.Text, out var parsed, out _) &&
            parsed is not null)
        {
            var installAction = new PluginManifest
            {
                Id = DynamicInstallPopClipId,
                Name = $"安装「{parsed.Name}」",
                Icon = "symbol:plus",
                Description = $"检测到 PopClip 插件片段，点击立即安装 ({parsed.Kind})",
                Kind = PluginKind.Builtin,
                After = AfterActionType.None,
                Enabled = true
            };
            matched.Insert(0, new MatchedPlugin(installAction));
        }

        if (matched.Count == 0)
        {
            _diagnostics.Write($"已捕获文本但未匹配到任何已启用插件 [{enriched.ProcessName}]");
            return;
        }

        _currentContext = enriched;
        _diagnostics.Write($"弹出浮动栏 [{enriched.ProcessName}]：匹配 {matched.Count} 个动作，来源={enriched.Source}");
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            _toolbar.ShowActions(enriched, matched, settings, _themeService.EffectiveTheme, _themeService.CurrentPreference);
        });
    }

    private void OnSelectionGestureCompleted(object? sender, SelectionGestureEventArgs e)
    {
        _pendingCaptureCts?.Cancel();
        _pendingCaptureCts?.Dispose();
        _pendingCaptureCts = new CancellationTokenSource();
        var token = _pendingCaptureCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var settings = _settingsService.Settings;
                var delay = Math.Clamp(settings.SelectionDelayMs, 20, 400);
                await Task.Delay(delay, token);

                var context = await _captureService.TryCaptureAsync(e.ReleasePoint, settings, token);
                if (context is null || token.IsCancellationRequested)
                {
                    return;
                }

                ShowToolbarForContext(context);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer gesture or user interaction
            }
            catch (Exception ex)
            {
                _diagnostics.Write($"捕获流程异常: {ex.Message}");
            }
        }, token);
    }

    private void OnDismissRequested(object? sender, EventArgs e)
    {
        _pendingCaptureCts?.Cancel();
        Application.Current?.Dispatcher.InvokeAsync(() => _toolbar.HideToolbar());
    }

    private async void OnToolbarActionInvoked(object? sender, MatchedPlugin matched)
    {
        var context = _currentContext;
        if (context is null)
        {
            return;
        }

        _toolbar.HideToolbar();

        if (matched.Plugin.Id == DynamicInstallPopClipId)
        {
            if (_pluginRegistry.TryImportPopClipSnippet(context.Text, out var imported, out var err) && imported is not null)
            {
                _diagnostics.Write($"已通过划词一键安装 PopClip 插件: {imported.Name} ({imported.Id})");
                _hostEnvironment.ShowToast($"已安装 PopClip 插件「{imported.Name}」", context.AnchorPoint);
            }
            else
            {
                _hostEnvironment.ShowToast($"安装失败: {err}", context.AnchorPoint);
            }
            return;
        }

        var options = _pluginRegistry.GetResolvedOptions(matched.Plugin);
        var request = new PluginExecutionRequest
        {
            Plugin = matched.Plugin,
            Context = context,
            Options = options,
            RegexMatchValue = matched.RegexMatchValue,
            RegexGroups = matched.RegexGroups ?? Array.Empty<string>()
        };

        var result = await _engine.ExecuteAsync(request, _hostEnvironment);
        _diagnostics.Write(result.Success
            ? $"执行插件「{matched.Plugin.Name}」成功"
            : $"执行插件「{matched.Plugin.Name}」失败: {result.ErrorMessage}");
    }

    public void Dispose()
    {
        _themeService.ToolbarAppearanceChanged -= OnAppearanceChanged;
        _pendingCaptureCts?.Cancel();
        _pendingCaptureCts?.Dispose();
        _hookService.Dispose();
    }
}
