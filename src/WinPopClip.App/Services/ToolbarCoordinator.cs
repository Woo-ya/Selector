using System.Windows;
using WinPopClip.App.Native;
using WinPopClip.App.Views;
using WinPopClip.Core.Models;
using WinPopClip.Core.Plugins;
using WinPopClip.Core.Storage;

namespace WinPopClip.App.Services;

public enum ToolbarCaptureState
{
    Idle,
    Loading,
    Succeeded,
    Failed
}

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
    private long _captureSeq;
    private long _latestHookEventSeq;
    private long _lastSummonTick;
    private ScreenPoint _lastSummonPoint;
    private volatile bool _isShowingLoading;
    private int _isExecutingAction;

    public ToolbarCaptureState CurrentState { get; private set; } = ToolbarCaptureState.Idle;
    public event EventHandler<ToolbarCaptureState>? CaptureStateChanged;

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

        _hostEnvironment = new AppHostEnvironment(
            clipboardGuard,
            (msg, anchor) =>
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    _toolbar.ShowToastMessage(msg, anchor, _themeService.EffectiveTheme, _settingsService.Settings);
                });
            },
            ReappearAtAsync);

        _toolbar.ActionInvoked += OnToolbarActionInvoked;
        _toolbar.ToastCopyRequested += async (_, text) =>
        {
            await _hostEnvironment.CopyTextAsync(text);
        };

        _themeService.ToolbarAppearanceChanged += OnAppearanceChanged;

        _hookService = new GlobalInputHookService(
            pt => _toolbar.IsScreenPointInside(pt),
            () => _settingsService.Settings,
            msg => _diagnostics.Write(msg));

        clipboardGuard.SetSyntheticInputSuppression = suppress => _hookService.SuppressKeyboardDismiss = suppress;

        _hookService.SelectionGestureCompleted += OnSelectionGestureCompleted;
        _hookService.DismissRequested += OnDismissRequested;
        _hookService.DragStarted += (_, _) => _captureService.PreWakeForegroundWindow();
    }

    public void Start() => _hookService.Start();

    public void Stop() => _hookService.Stop();

    private void OnAppearanceChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            _toolbar.ApplyLiveAppearance(_themeService.EffectiveTheme, _settingsService.Settings);
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
            if (enriched.IsEmptyInputSummon)
            {
                var hint = enriched.HasClipboardText
                    ? "当前对话框暂无可用动作（请确认已启用「粘贴」插件）"
                    : "剪贴板为空，暂无可粘贴内容";
                _diagnostics.Write($"对话框双击呼出 [{enriched.ProcessName}]：{hint}");
                SetState(ToolbarCaptureState.Idle);
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    _isShowingLoading = false;
                    _toolbar.ShowToastMessage(hint, enriched.AnchorPoint, _themeService.EffectiveTheme, settings, showCopyButton: false, durationSeconds: 2.0);
                });
                return;
            }

            _diagnostics.Write($"已捕获文本但未匹配到任何已启用插件 [{enriched.ProcessName}]");
            SetState(ToolbarCaptureState.Idle);
            DismissLoadingIfActive();
            return;
        }

        _currentContext = enriched;
        _lastSummonTick = Environment.TickCount64;
        _lastSummonPoint = enriched.AnchorPoint;
        SetState(ToolbarCaptureState.Succeeded);
        _diagnostics.Write(enriched.IsEmptyInputSummon
            ? $"对话框双击呼出浮动栏 [{enriched.ProcessName}]：匹配 {matched.Count} 个动作"
            : $"弹出浮动栏 [{enriched.ProcessName}]：匹配 {matched.Count} 个动作，来源={enriched.Source}");
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            _isShowingLoading = false;
            _toolbar.ShowActions(enriched, matched, settings, _themeService.EffectiveTheme);
        });
    }

    private async Task ReappearAtAsync(ScreenPoint? anchor, CancellationToken cancellationToken)
    {
        var point = anchor ?? (Win32Interop.GetCursorPos(out var pt) ? new ScreenPoint(pt.X, pt.Y) : _lastSummonPoint);
        var settings = _settingsService.Settings;
        var context = await _captureService.TryCaptureAsync(point, settings, cancellationToken, allowEmptyEditableSummon: false);
        if (context is not null && !cancellationToken.IsCancellationRequested)
        {
            ShowToolbarForContext(context);
        }
    }

    private void OnSelectionGestureCompleted(object? sender, SelectionGestureEventArgs e)
    {
        if (e.EventSequence > 0)
        {
            AdvanceLatestEventSeq(e.EventSequence);
        }

        var now = Environment.TickCount64;
        // Debounce duplicate identical double-clicks at the same anchor within 320ms while toolbar is already open
        if (e.IsDoubleClick &&
            (now - _lastSummonTick) <= 320 &&
            Math.Abs(e.ReleasePoint.X - _lastSummonPoint.X) <= 8 &&
            Math.Abs(e.ReleasePoint.Y - _lastSummonPoint.Y) <= 8)
        {
            return;
        }

        var seq = Interlocked.Increment(ref _captureSeq);
        _pendingCaptureCts?.Cancel();
        _pendingCaptureCts?.Dispose();
        _pendingCaptureCts = new CancellationTokenSource();
        var token = _pendingCaptureCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var settings = _settingsService.Settings;
                var popupMs = ThemeResolver.ResolvePopupDurationMs(settings.ToolbarPopupSpeed, settings.ToolbarPopupDurationMs);
                var dragDelayCap = popupMs == 0 ? 24 : popupMs <= 120 ? 38 : settings.SelectionDelayMs;
                var delay = e.IsDoubleClick
                    ? Math.Clamp(Math.Min(settings.SelectionDelayMs, 35), 15, 35)
                    : Math.Clamp(Math.Min(settings.SelectionDelayMs, dragDelayCap), 18, 400);
                await Task.Delay(delay, token);

                var allowEmptyDialogSummon = e.IsDoubleClick && settings.EnableDialogDoubleClickSummon;
                SetState(ToolbarCaptureState.Loading);

                var context = await _captureService.TryCaptureAsync(
                    e.ReleasePoint,
                    settings,
                    token,
                    allowEmptyDialogSummon,
                    e.StartPoint,
                    isDragSelection: !e.IsDoubleClick,
                    sawIBeamCursor: e.SawIBeamCursor,
                    windowMovedDuringDrag: e.WindowMovedDuringDrag);

                if (token.IsCancellationRequested || seq != Interlocked.Read(ref _captureSeq))
                {
                    return;
                }

                if (context is null)
                {
                    SetState(ToolbarCaptureState.Idle);
                    return;
                }

                // If double-click word selection is disabled while dialog summon is enabled, only allow empty-input summon
                if (e.IsDoubleClick && !context.IsEmptyInputSummon && !settings.EnableDoubleClickSelection)
                {
                    SetState(ToolbarCaptureState.Idle);
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
                SetState(ToolbarCaptureState.Failed);
                _diagnostics.Write($"捕获流程异常: {ex.Message}");
                SetState(ToolbarCaptureState.Idle);
            }
        }, token);
    }

    private void OnDismissRequested(object? sender, DismissEventArgs e)
    {
        // Ignore out-of-order ThreadPool dismiss events that originated BEFORE a newer mouse-up selection gesture
        if (e.EventSequence > 0 && !AdvanceLatestEventSeq(e.EventSequence))
        {
            return;
        }

        Interlocked.Increment(ref _captureSeq);
        _pendingCaptureCts?.Cancel();
        _isShowingLoading = false;
        SetState(ToolbarCaptureState.Idle);
        Application.Current?.Dispatcher.InvokeAsync(() => _toolbar.HideToolbar());
    }

    private bool AdvanceLatestEventSeq(long candidate)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _latestHookEventSeq);
            if (candidate < current)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref _latestHookEventSeq, candidate, current) == current)
            {
                return true;
            }
        }
    }

    private void DismissLoadingIfActive()
    {
        if (!_isShowingLoading) return;
        _isShowingLoading = false;
        Application.Current?.Dispatcher.InvokeAsync(() => _toolbar.HideToolbar(immediate: true));
    }

    private void SetState(ToolbarCaptureState next)
    {
        if (CurrentState == next) return;
        CurrentState = next;
        CaptureStateChanged?.Invoke(this, next);
    }

    private static bool CanExecuteWithoutMaterializingText(PluginManifest plugin)
    {
        if (plugin.Kind == PluginKind.Builtin)
        {
            var cmd = (plugin.Action.BuiltinCommand ?? string.Empty).Trim().ToLowerInvariant();
            return cmd is "copy" or "cut" or "paste";
        }
        return plugin.Id.Equals("com.pilotmoon.popclip.extension.select-all", StringComparison.OrdinalIgnoreCase);
    }

    private async void OnToolbarActionInvoked(object? sender, MatchedPlugin matched)
    {
        if (Interlocked.Exchange(ref _isExecutingAction, 1) != 0)
        {
            return;
        }

        try
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

            // Doubao-style on-demand materialization:
            // Copy/Cut/Paste/SelectAll execute directly via a single keystroke (zero double-copy!),
            // while Search/Translate/WordCount/AI materialize the selected text on click.
            var executionContext = context;
            if (context.IsDeferredCapture &&
                string.IsNullOrWhiteSpace(context.Text) &&
                !CanExecuteWithoutMaterializingText(matched.Plugin))
            {
                var materialized = await _captureService.MaterializeDeferredContextAsync(context, CancellationToken.None);
                if (materialized is null)
                {
                    _hostEnvironment.ShowToast("未检测到选中文本，请重新划选后重试", context.AnchorPoint);
                    return;
                }
                executionContext = _matcher.EnrichContext(materialized);
            }

            var options = _pluginRegistry.GetResolvedOptions(matched.Plugin);
            var request = new PluginExecutionRequest
            {
                Plugin = matched.Plugin,
                Context = executionContext,
                Options = options,
                RegexMatchValue = matched.RegexMatchValue,
                RegexGroups = matched.RegexGroups ?? Array.Empty<string>()
            };

            var result = await _engine.ExecuteAsync(request, _hostEnvironment);
            _diagnostics.Write(result.Success
                ? $"执行插件「{matched.Plugin.Name}」成功"
                : $"执行插件「{matched.Plugin.Name}」失败: {result.ErrorMessage}");
        }
        catch (Exception ex)
        {
            _diagnostics.Write($"执行插件「{matched.Plugin.Name}」异常: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isExecutingAction, 0);
        }
    }

    public void Dispose()
    {
        _themeService.ToolbarAppearanceChanged -= OnAppearanceChanged;
        _pendingCaptureCts?.Cancel();
        _pendingCaptureCts?.Dispose();
        _hookService.Dispose();
    }
}
