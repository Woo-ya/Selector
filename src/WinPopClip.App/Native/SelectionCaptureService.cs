using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Controls;
using System.Windows.Input;
using WinPopClip.Core.Models;

namespace WinPopClip.App.Native;

/// <summary>
/// Multi-channel read-only text selection capture service:
/// 1. Direct WPF reader for Selector's own UI (instant, zero cross-thread overhead)
/// 2. Native COM UIA3 (CUIAutomation8 + TextChildPattern) with lazy browser accessibility retry
/// 3. Classic Win32 EM_GETSEL / WM_GETTEXT for legacy Edit/RichEdit controls
/// 4. Managed UIA2 backup (never runs slow Descendants tree search)
/// 5. Optional opt-in cancel-safe Clipboard fallback (disabled by default so selecting text never copies)
/// </summary>
public sealed class SelectionCaptureService
{
    private static readonly HashSet<string> LazyUiaApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "brave", "opera", "vivaldi", "arc", "firefox", "quark", "QQBrowser", "360chrome", "SogouExplorer",
        "Code", "Cursor", "Windsurf", "Trae", "Antigravity", "WorkBuddy", "electron", "msedgewebview2", "WebViewHost",
        "WeChat", "Weixin", "WeChatAppEx", "QQ", "Feishu", "Lark", "DingTalk", "Telegram", "Slack", "Discord", "Teams", "ms-teams",
        "Notion", "Obsidian", "Logseq", "Typora", "wps", "et", "wpp", "Postman", "Apifox", "Spotify"
    };

    /// <summary>
    /// Apps where UIA3 is disabled (WeChat 4.x Qt51514QWindowIcon) or still cold-booting on first drag (QQNT).
    /// Instead of sending Ctrl+C on selection (which pollutes clipboard & triggers Quicker toasts),
    /// Selector uses Doubao AI-style deferred capture: pop up toolbar immediately on I-beam text drag,
    /// and send Ctrl+C ONLY if/when the user actually clicks an action button!
    /// </summary>
    private static readonly HashSet<string> DeferredCaptureApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "WeChat", "Weixin", "WeChatAppEx", "QQ"
    };

    public static bool IsDeferredCaptureEligibleApp(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        if (DeferredCaptureApps.Contains(processName)) return true;
        return processName.StartsWith("WeChat", StringComparison.OrdinalIgnoreCase) ||
               processName.StartsWith("Weixin", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLikelyLazyUiaTarget(string processName, string className)
    {
        if (!string.IsNullOrWhiteSpace(processName) && LazyUiaApps.Contains(processName))
        {
            return true;
        }
        return ActiveWindowService.IsCustomChromeDesktopClass(className);
    }

    private readonly ActiveWindowService _activeWindowService;
    private readonly ClipboardGuard _clipboardGuard;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<IntPtr, long> _lastWakeTickByHwnd = new();
    private readonly ConcurrentDictionary<IntPtr, byte> _confirmedAwakeHwnds = new();

    public SelectionCaptureService(
        ActiveWindowService activeWindowService,
        ClipboardGuard clipboardGuard,
        Action<string>? log = null)
    {
        _activeWindowService = activeWindowService;
        _clipboardGuard = clipboardGuard;
        _log = log;
    }

    /// <summary>
    /// Called asynchronously on WM_LBUTTONDOWN so lazy Electron/Chromium/Qt apps (QQNT, WorkBuddy, Antigravity)
    /// begin activating their native UIA3/MSAA tree WHILE the user is dragging across text, making the very first drag work!
    /// </summary>
    public void PreWakeForegroundWindow()
    {
        try
        {
            var hwnd = Win32Interop.GetForegroundWindow();
            if (hwnd == IntPtr.Zero || _confirmedAwakeHwnds.ContainsKey(hwnd)) return;

            var cls = new StringBuilder(128);
            _ = Win32Interop.GetClassName(hwnd, cls, cls.Capacity);
            var className = cls.ToString();
            if (!ActiveWindowService.IsCustomChromeDesktopClass(className)) return;

            var now = Environment.TickCount64;
            if (_lastWakeTickByHwnd.TryGetValue(hwnd, out var lastTick) && (now - lastTick) < 2500)
            {
                return;
            }
            _lastWakeTickByHwnd[hwnd] = now;
            if (_lastWakeTickByHwnd.Count > 256) _lastWakeTickByHwnd.Clear();

            WakeWindowAccessibility(hwnd);
        }
        catch { }
    }

    public async Task<SelectionContext?> TryCaptureAsync(
        ScreenPoint releasePoint,
        AppSettings settings,
        CancellationToken cancellationToken = default,
        bool allowEmptyEditableSummon = false,
        ScreenPoint? dragStartPoint = null,
        bool isDragSelection = false,
        bool sawIBeamCursor = false,
        bool windowMovedDuringDrag = false)
    {
        var winInfo = _activeWindowService.GetForegroundWindowInfo();
        if (winInfo.Handle == IntPtr.Zero)
        {
            _log?.Invoke("跳过捕获：未检测到前台窗口");
            return null;
        }

        if (settings.DisableInFullscreen && winInfo.IsFullscreen)
        {
            _log?.Invoke($"跳过捕获 [{winInfo.ProcessName}]：检测到全屏独占窗口");
            return null;
        }

        if (settings.ExcludedApps.Any(a => a.Equals(winInfo.ProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            _log?.Invoke($"跳过捕获 [{winInfo.ProcessName}]：在排除应用列表中");
            return null;
        }

        var detectedEditable = false;
        ScreenRect? caretBounds = null;

        // Channel 0: Direct in-process WPF TextBox reader for Selector's own window (0.01ms, zero UIA risk)
        if (winInfo.ProcessId == Environment.ProcessId)
        {
            var ownResult = await TryCaptureOwnWpfSelectionAsync();
            if (ownResult is not null)
            {
                if (ownResult.IsPasswordField)
                {
                    _log?.Invoke($"跳过捕获 [{winInfo.ProcessName}]：检测到密码输入框");
                    return null;
                }
                if (!string.IsNullOrWhiteSpace(ownResult.SelectedText))
                {
                    _log?.Invoke($"捕获成功 [{winInfo.ProcessName}]：本地 WPF 选区 ({ownResult.SelectedText.Length} 字符, 可编辑={ownResult.IsEditable})");
                    return BuildContext(winInfo, ownResult, releasePoint, SelectionCaptureSource.UiAutomation);
                }
                if (ownResult.IsEditable && allowEmptyEditableSummon)
                {
                    return await BuildEmptyEditableSummonContextAsync(winInfo, ownResult.Bounds, releasePoint, cancellationToken);
                }
            }
            return null;
        }

        // Channel 1: Persistent-MTA Native COM UIA3 (CUIAutomation8 + TextChildPattern + lazy short-circuit)
        // Wake Chromium/Electron/Qt accessibility when not yet confirmed awake (re-wakes after 2.5s cooldown if still bare ct=50032!)
        var freshlyWoken = false;
        if (IsLikelyLazyUiaTarget(winInfo.ProcessName, winInfo.ClassName) &&
            !_confirmedAwakeHwnds.ContainsKey(winInfo.Handle))
        {
            var now = Environment.TickCount64;
            if (!_lastWakeTickByHwnd.TryGetValue(winInfo.Handle, out var lastWake) || (now - lastWake) >= 2500)
            {
                _lastWakeTickByHwnd[winInfo.Handle] = now;
                if (_lastWakeTickByHwnd.Count > 256) _lastWakeTickByHwnd.Clear();
                WakeWindowAccessibility(winInfo.Handle);
                freshlyWoken = true;
            }
        }

        var uiaResult = await NativeUia3Reader.TryReadSelectionAsync(
            releasePoint,
            winInfo.Handle,
            dragStartPoint,
            timeoutMs: 650,
            cancellationToken);

        if (uiaResult.IsPasswordField)
        {
            _log?.Invoke($"跳过捕获 [{winInfo.ProcessName}]：检测到密码输入框");
            return null;
        }
        if (uiaResult.IsEditable)
        {
            detectedEditable = true;
            caretBounds ??= uiaResult.Bounds;
        }

        // Progressive wake retry for newly-activated Chromium/Electron windows (e.g. QQNT first drag):
        // if still seeing bare Window (ct=50032), retry after 120ms and once more after 200ms
        var isBareWindow = uiaResult.DiagnosticNote is not null && uiaResult.DiagnosticNote.Contains("ct=50032", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(uiaResult.SelectedText) &&
            (freshlyWoken || (isBareWindow && IsLikelyLazyUiaTarget(winInfo.ProcessName, winInfo.ClassName) && !winInfo.ProcessName.StartsWith("Weixin", StringComparison.OrdinalIgnoreCase))) &&
            !string.Equals(uiaResult.DiagnosticNote, "timeout", StringComparison.Ordinal))
        {
            foreach (var waitMs in new[] { 120, 200 })
            {
                await Task.Delay(waitMs, cancellationToken);
                var retry = await NativeUia3Reader.TryReadSelectionAsync(
                    releasePoint,
                    winInfo.Handle,
                    dragStartPoint,
                    timeoutMs: 650,
                    cancellationToken);

                if (retry.IsPasswordField)
                {
                    _log?.Invoke($"跳过捕获 [{winInfo.ProcessName}]：检测到密码输入框");
                    return null;
                }
                if (retry.IsEditable)
                {
                    detectedEditable = true;
                    caretBounds ??= retry.Bounds;
                }
                if (!string.IsNullOrWhiteSpace(retry.SelectedText))
                {
                    uiaResult = retry;
                    break;
                }
                if (!string.IsNullOrWhiteSpace(retry.DiagnosticNote))
                {
                    uiaResult = uiaResult with { DiagnosticNote = retry.DiagnosticNote };
                }
                // Stop retrying early once the tree is awake (no longer bare ct=50032)
                if (retry.DiagnosticNote is null || !retry.DiagnosticNote.Contains("ct=50032", StringComparison.Ordinal))
                {
                    break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(uiaResult.SelectedText))
        {
            _confirmedAwakeHwnds[winInfo.Handle] = 1;
            if (_confirmedAwakeHwnds.Count > 256) _confirmedAwakeHwnds.Clear();
            _log?.Invoke($"捕获成功 [{winInfo.ProcessName}]：UIA3 通道 ({uiaResult.SelectedText.Length} 字符, 可编辑={uiaResult.IsEditable})");
            return BuildContext(winInfo, uiaResult, releasePoint, SelectionCaptureSource.UiAutomation);
        }

        // Channel 2: Classic Win32 Edit control EM_GETSEL + WM_GETTEXT + OS Caret check (instant, read-only)
        var editResult = TryCaptureWin32EditSelection(winInfo.Handle);
        if (editResult is not null)
        {
            if (editResult.IsEditable)
            {
                detectedEditable = true;
                caretBounds ??= editResult.Bounds;
            }
            if (!string.IsNullOrWhiteSpace(editResult.SelectedText))
            {
                _log?.Invoke($"捕获成功 [{winInfo.ProcessName}]：Win32 Edit 通道 ({editResult.SelectedText.Length} 字符)");
                return BuildContext(winInfo, editResult, releasePoint, SelectionCaptureSource.UiAutomation);
            }
        }

        // Fast-path PopClip-style Double-Click in empty editable dialog/input box BEFORE any deferred/clipboard probe!
        if (allowEmptyEditableSummon && detectedEditable)
        {
            return await BuildEmptyEditableSummonContextAsync(winInfo, caretBounds, releasePoint, cancellationToken);
        }

        // Channel 3: Doubao-style Deferred Capture (instant popup, zero Ctrl+C on drag, zero Quicker toast!).
        // For bare-UIA apps (WeChat 4.x Qt, cold-start QQ) where UIA3 & Win32 Edit yielded no text on a genuine
        // I-beam text drag, summon the floating toolbar immediately with IsDeferredCapture = true without wasting
        // time on full-monitor screenshots/OCR. Clicking Copy sends ONE direct Ctrl+C (single accurate Quicker toast);
        // clicking Search/Translate/WordCount/AI materializes the selected text on demand.
        if (isDragSelection &&
            !windowMovedDuringDrag &&
            sawIBeamCursor &&
            IsDeferredCaptureEligibleApp(winInfo.ProcessName))
        {
            _log?.Invoke($"捕获成功 [{winInfo.ProcessName}]：延迟取词通道 (Doubao 模式，零剪贴板触碰, 可编辑={detectedEditable})");
            return new SelectionContext
            {
                Text = string.Empty,
                ProcessName = winInfo.ProcessName,
                WindowTitle = winInfo.WindowTitle,
                WindowHandle = winInfo.Handle,
                IsEditable = detectedEditable,
                IsPasswordField = false,
                IsDeferredCapture = true,
                Source = SelectionCaptureSource.DeferredCapture,
                SelectionBounds = caretBounds ?? BuildDragSelectionBounds(dragStartPoint, releasePoint),
                AnchorPoint = releasePoint
            };
        }

        // Clipboard-copy fallback is permanently disabled on the capture path. Even if a legacy
        // settings file still has EnableClipboardFallback=true, selection must never write clipboard.
        var hint = winInfo.IsPotentiallyElevated ? "（提示：目标进程可能以管理员权限运行，受 UIPI 隔离限制）" : string.Empty;
        _log?.Invoke($"未捕获到文本 [{winInfo.ProcessName}/{winInfo.ClassName}]：只读通道无选区 ({uiaResult.DiagnosticNote}, ibeam={sawIBeamCursor}, moved={windowMovedDuringDrag}){hint}");
        return null;
    }

    /// <summary>
    /// Materializes selected text on-demand for a Doubao-style DeferredCapture context ONLY when the user
    /// clicks a non-Copy action (e.g. Search, Translate, Word Count, AI). Snapshots and restores the clipboard cleanly.
    /// </summary>
    public async Task<SelectionContext?> MaterializeDeferredContextAsync(
        SelectionContext deferredContext,
        CancellationToken cancellationToken = default)
    {
        var text = await _clipboardGuard.CaptureSelectionViaCopyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            _log?.Invoke($"按需取词为空 [{deferredContext.ProcessName}]：目标选区可能已取消");
            return null;
        }

        _log?.Invoke($"按需取词成功 [{deferredContext.ProcessName}]：{text.Length} 字符（已还原原剪贴板）");
        return new SelectionContext
        {
            Text = text,
            ProcessName = deferredContext.ProcessName,
            WindowTitle = deferredContext.WindowTitle,
            WindowHandle = deferredContext.WindowHandle,
            IsEditable = deferredContext.IsEditable,
            IsPasswordField = false,
            IsDeferredCapture = false,
            Source = SelectionCaptureSource.ClipboardFallback,
            SelectionBounds = deferredContext.SelectionBounds,
            AnchorPoint = deferredContext.AnchorPoint
        };
    }

    private static ScreenRect? BuildDragSelectionBounds(ScreenPoint? start, ScreenPoint end)
    {
        if (start is not { } s) return null;
        var minX = Math.Min(s.X, end.X);
        var maxX = Math.Max(s.X, end.X);
        var minY = Math.Min(s.Y, end.Y);
        var maxY = Math.Max(s.Y, end.Y);
        return new ScreenRect(minX, minY, Math.Max(12, maxX - minX), Math.Max(18, maxY - minY));
    }

    private async Task<SelectionContext> BuildEmptyEditableSummonContextAsync(
        ActiveWindowInfo winInfo,
        ScreenRect? caretBounds,
        ScreenPoint releasePoint,
        CancellationToken cancellationToken)
    {
        var clipText = await _clipboardGuard.GetTextAsync(cancellationToken);
        var hasClip = !string.IsNullOrWhiteSpace(clipText);
        _log?.Invoke($"对话框双击呼出 [{winInfo.ProcessName}]：空白可编辑输入框（剪贴板有文本={hasClip}）");
        return new SelectionContext
        {
            Text = string.Empty,
            ProcessName = winInfo.ProcessName,
            WindowTitle = winInfo.WindowTitle,
            WindowHandle = winInfo.Handle,
            IsEditable = true,
            IsPasswordField = false,
            IsEmptyInputSummon = true,
            HasClipboardText = hasClip,
            Source = SelectionCaptureSource.UiAutomation,
            SelectionBounds = caretBounds,
            AnchorPoint = releasePoint
        };
    }

    private static SelectionContext BuildContext(
        ActiveWindowInfo winInfo,
        NativeUiaResult result,
        ScreenPoint releasePoint,
        SelectionCaptureSource source) => new()
        {
            Text = result.SelectedText!,
            ProcessName = winInfo.ProcessName,
            WindowTitle = winInfo.WindowTitle,
            WindowHandle = winInfo.Handle,
            IsEditable = result.IsEditable,
            IsPasswordField = false,
            Source = source,
            SelectionBounds = result.Bounds,
            AnchorPoint = releasePoint
        };

    private static async Task<NativeUiaResult> RunWithTimeoutAsync(
        Func<NativeUiaResult> probe,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        try
        {
            var task = Task.Run(probe, cancellationToken);
            var completed = await Task.WhenAny(task, Task.Delay(timeoutMs, cancellationToken));
            return completed == task ? await task : new NativeUiaResult(null, null, false, false, "timeout");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new NativeUiaResult(null, null, false, false);
        }
    }

    private static NativeUiaResult RunOnMtaThread(Func<NativeUiaResult> action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA)
        {
            return action();
        }

        NativeUiaResult result = new(null, null, false, false);
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch { }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join(480);
        return result;
    }

    private static async Task<NativeUiaResult?> TryCaptureOwnWpfSelectionAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return null;

        return await dispatcher.InvokeAsync(() =>
        {
            if (Keyboard.FocusedElement is PasswordBox)
            {
                return new NativeUiaResult(null, null, false, IsPasswordField: true);
            }

            if (Keyboard.FocusedElement is TextBox tb)
            {
                ScreenRect? bounds = null;
                try
                {
                    var hasSel = !string.IsNullOrWhiteSpace(tb.SelectedText);
                    var startRect = tb.GetRectFromCharacterIndex(hasSel ? tb.SelectionStart : tb.CaretIndex, trailingEdge: false);
                    var endRect = tb.GetRectFromCharacterIndex(hasSel ? tb.SelectionStart + tb.SelectionLength : tb.CaretIndex, trailingEdge: true);
                    if (!startRect.IsEmpty && !endRect.IsEmpty)
                    {
                        var topLeft = tb.PointToScreen(new Point(Math.Min(startRect.Left, endRect.Left), Math.Min(startRect.Top, endRect.Top)));
                        var bottomRight = tb.PointToScreen(new Point(Math.Max(startRect.Right, endRect.Right), Math.Max(startRect.Bottom, endRect.Bottom)));
                        if (bottomRight.X >= topLeft.X && bottomRight.Y > topLeft.Y)
                        {
                            bounds = new ScreenRect(topLeft.X, topLeft.Y, Math.Max(2, bottomRight.X - topLeft.X), bottomRight.Y - topLeft.Y);
                        }
                    }
                }
                catch { }

                var text = string.IsNullOrWhiteSpace(tb.SelectedText) ? null : tb.SelectedText;
                return new NativeUiaResult(text, bounds, !tb.IsReadOnly, false);
            }

            return null;
        }).Task;
    }

    private static void WakeWindowAccessibility(IntPtr topHwnd)
    {
        if (topHwnd == IntPtr.Zero) return;
        try
        {
            static void SendUiaWake(IntPtr h)
            {
                // 1. UiaRootObjectId (-25) activates Chromium/Electron/Qt native UI Automation provider
                _ = Win32Interop.SendMessageTimeoutW(
                    h, Win32Interop.WM_GETOBJECT, IntPtr.Zero, new IntPtr(Win32Interop.UiaRootObjectId),
                    Win32Interop.SMTO_ABORTIFHUNG, 60, out _);

                // 2. AccessibleObjectFromWindow (oleacc) instantiates the MSAA/IAccessible root object,
                //    which reliably flips AXMode::kNativeAPIs on QQNT/Electron and QAccessible on Qt5!
                try
                {
                    var iid = Win32Interop.IID_IAccessible;
                    if (Win32Interop.AccessibleObjectFromWindow(h, Win32Interop.OBJID_CLIENT_UINT, ref iid, out var acc) == 0 &&
                        acc is not null && Marshal.IsComObject(acc))
                    {
                        Marshal.ReleaseComObject(acc);
                    }
                }
                catch { }
            }

            SendUiaWake(topHwnd);

            var childCount = 0;
            Win32Interop.EnumChildWindows(topHwnd, (childHwnd, _) =>
            {
                if (++childCount > 24) return false;
                var cls = new StringBuilder(64);
                _ = Win32Interop.GetClassName(childHwnd, cls, cls.Capacity);
                var name = cls.ToString();
                if (name.Contains("RenderWidgetHost", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Intermediate D3D", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Qt", StringComparison.OrdinalIgnoreCase))
                {
                    SendUiaWake(childHwnd);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    private static NativeUiaResult? TryCaptureWin32EditSelection(IntPtr foregroundHwnd)
    {
        try
        {
            var threadId = Win32Interop.GetWindowThreadProcessId(foregroundHwnd, out _);
            if (threadId == 0) return null;

            var gui = new Win32Interop.GUITHREADINFO { cbSize = Marshal.SizeOf<Win32Interop.GUITHREADINFO>() };
            if (!Win32Interop.GetGUIThreadInfo(threadId, ref gui))
            {
                return null;
            }

            ScreenRect? caretRect = null;
            var hasActiveOsCaret = gui.hwndCaret != IntPtr.Zero && gui.rcCaret.Height > 0;
            if (hasActiveOsCaret)
            {
                var pt = new Win32Interop.POINT { X = gui.rcCaret.Left, Y = gui.rcCaret.Top };
                if (Win32Interop.ClientToScreen(gui.hwndCaret, ref pt))
                {
                    caretRect = new ScreenRect(pt.X, pt.Y, Math.Max(2, gui.rcCaret.Width), gui.rcCaret.Height);
                }
            }

            if (gui.hwndFocus == IntPtr.Zero)
            {
                return hasActiveOsCaret ? new NativeUiaResult(null, caretRect, IsEditable: true, IsPasswordField: false) : null;
            }

            var focusHwnd = gui.hwndFocus;
            var cls = new StringBuilder(64);
            _ = Win32Interop.GetClassName(focusHwnd, cls, cls.Capacity);
            var className = cls.ToString();
            if (!className.Contains("Edit", StringComparison.OrdinalIgnoreCase))
            {
                return hasActiveOsCaret ? new NativeUiaResult(null, caretRect, IsEditable: true, IsPasswordField: false) : null;
            }

            int start = 0, end = 0;
            if (Win32Interop.SendMessageTimeoutW(
                    focusHwnd,
                    Win32Interop.EM_GETSEL,
                    ref start,
                    ref end,
                    Win32Interop.SMTO_ABORTIFHUNG,
                    120,
                    out _) == IntPtr.Zero)
            {
                return null;
            }
            if (end <= start)
            {
                // Empty caret inside a classic Win32 Edit/RichEdit control — still editable for double-click summon
                return new NativeUiaResult(null, null, IsEditable: true, IsPasswordField: false);
            }
            if (end - start > 16384) return new NativeUiaResult(null, null, IsEditable: true, IsPasswordField: false);

            if (Win32Interop.SendMessageTimeoutW(
                    focusHwnd,
                    Win32Interop.WM_GETTEXTLENGTH,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    Win32Interop.SMTO_ABORTIFHUNG,
                    120,
                    out var lenPtr) == IntPtr.Zero)
            {
                return new NativeUiaResult(null, null, IsEditable: true, IsPasswordField: false);
            }
            var len = lenPtr.ToInt32();
            if (len <= 0 || len > 500_000) return new NativeUiaResult(null, null, IsEditable: true, IsPasswordField: false);

            var sb = new StringBuilder(len + 2);
            if (Win32Interop.SendMessageTimeoutW(
                    focusHwnd,
                    Win32Interop.WM_GETTEXT,
                    new IntPtr(sb.Capacity),
                    sb,
                    Win32Interop.SMTO_ABORTIFHUNG,
                    150,
                    out _) == IntPtr.Zero)
            {
                return new NativeUiaResult(null, null, IsEditable: true, IsPasswordField: false);
            }
            var full = sb.ToString();
            if (start < 0 || end > full.Length || end <= start) return new NativeUiaResult(null, null, IsEditable: true, IsPasswordField: false);

            var selected = full[start..end];
            return string.IsNullOrWhiteSpace(selected)
                ? new NativeUiaResult(null, null, IsEditable: true, IsPasswordField: false)
                : new NativeUiaResult(selected, null, IsEditable: true, IsPasswordField: false);
        }
        catch
        {
            return null;
        }
    }

    private static NativeUiaResult TryCaptureViaManagedUia2(ScreenPoint releasePoint)
    {
        try
        {
            var candidates = new List<AutomationElement>(2);
            try
            {
                var focused = AutomationElement.FocusedElement;
                if (focused is not null)
                {
                    if (focused.Current.IsPassword) return new NativeUiaResult(null, null, false, true);
                    candidates.Add(focused);
                }
            }
            catch { }

            try
            {
                var fromPoint = AutomationElement.FromPoint(new Point(releasePoint.X, releasePoint.Y));
                if (fromPoint is not null)
                {
                    if (fromPoint.Current.IsPassword) return new NativeUiaResult(null, null, false, true);
                    candidates.Add(fromPoint);
                }
            }
            catch { }

            var bestEditable = false;
            foreach (var start in candidates)
            {
                if (IsElementEditableUia2(start)) bestEditable = true;

                var current = start;
                for (var depth = 0; depth < 16 && current is not null; depth++)
                {
                    try
                    {
                        if (current.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj) &&
                            patternObj is TextPattern tp)
                        {
                            var ranges = tp.GetSelection();
                            if (ranges is { Length: > 0 })
                            {
                                var text = string.Concat(ranges.Select(r => r.GetText(16384)));
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    var isEditable = DetermineRangeEditableUia2(ranges[0], current, bestEditable);
                                    var bounds = ExtractBoundsUia2(ranges[0]);
                                    return new NativeUiaResult(text, bounds, isEditable, false);
                                }
                            }
                        }

                        current = TreeWalker.ControlViewWalker.GetParent(current);
                    }
                    catch
                    {
                        break;
                    }
                }
            }

            return new NativeUiaResult(null, null, bestEditable, false);
        }
        catch
        {
            return new NativeUiaResult(null, null, false, false);
        }
    }

    private static ScreenRect? ExtractBoundsUia2(TextPatternRange range)
    {
        try
        {
            var rects = range.GetBoundingRectangles();
            if (rects is { Length: > 0 })
            {
                var minX = rects.Min(r => r.Left);
                var minY = rects.Min(r => r.Top);
                var maxR = rects.Max(r => r.Right);
                var maxB = rects.Max(r => r.Bottom);
                if (maxR > minX && maxB > minY)
                {
                    return new ScreenRect(minX, minY, maxR - minX, maxB - minY);
                }
            }
        }
        catch { }
        return null;
    }

    private static bool DetermineRangeEditableUia2(TextPatternRange range, AutomationElement owner, bool fallbackEditable)
    {
        try
        {
            if (range.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is bool isReadOnly)
            {
                return !isReadOnly;
            }
        }
        catch { }
        return IsElementEditableUia2(owner) || fallbackEditable;
    }

    private static bool IsElementEditableUia2(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valObj) && valObj is ValuePattern vp)
            {
                return !vp.Current.IsReadOnly;
            }
            if (element.Current.ControlType == ControlType.Edit)
            {
                return element.Current.IsEnabled;
            }
        }
        catch { }
        return false;
    }
}
