using System.Windows.Automation;
using System.Windows.Automation.Text;
using WinPopClip.Core.Models;

namespace WinPopClip.App.Native;

/// <summary>
/// Two-tier text selection capture service:
/// Tier 1: UI Automation via FocusedElement AND FromPoint + ancestor tree walk (with password & read-only checks)
/// Tier 2: Non-polluting Clipboard Ctrl+C fallback for Chromium/Edge, Electron, legacy Win32, and custom controls.
/// </summary>
public sealed class SelectionCaptureService
{
    private readonly ActiveWindowService _activeWindowService;
    private readonly ClipboardGuard _clipboardGuard;
    private readonly Action<string>? _log;

    public SelectionCaptureService(
        ActiveWindowService activeWindowService,
        ClipboardGuard clipboardGuard,
        Action<string>? log = null)
    {
        _activeWindowService = activeWindowService;
        _clipboardGuard = clipboardGuard;
        _log = log;
    }

    public async Task<SelectionContext?> TryCaptureAsync(
        ScreenPoint releasePoint,
        AppSettings settings,
        CancellationToken cancellationToken = default)
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

        // Tier 1: UI Automation with timeout protection so unresponsive UIA providers never stall
        UiaProbeResult uiaResult;
        try
        {
            var uiaTask = Task.Run(() => TryCaptureViaUiAutomation(releasePoint, winInfo.Handle), cancellationToken);
            var completed = await Task.WhenAny(uiaTask, Task.Delay(320, cancellationToken));
            uiaResult = completed == uiaTask ? await uiaTask : new UiaProbeResult(null, null, false, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            uiaResult = new UiaProbeResult(null, null, false, false);
        }

        if (uiaResult.IsPasswordField)
        {
            _log?.Invoke($"跳过捕获 [{winInfo.ProcessName}]：检测到密码输入框");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(uiaResult.SelectedText))
        {
            _log?.Invoke($"捕获成功 [{winInfo.ProcessName}]：UIA 通道 ({uiaResult.SelectedText.Length} 字符, 可编辑={uiaResult.IsEditable})");
            return new SelectionContext
            {
                Text = uiaResult.SelectedText,
                ProcessName = winInfo.ProcessName,
                WindowTitle = winInfo.WindowTitle,
                WindowHandle = winInfo.Handle,
                IsEditable = uiaResult.IsEditable,
                IsPasswordField = false,
                Source = SelectionCaptureSource.UiAutomation,
                SelectionBounds = uiaResult.Bounds,
                AnchorPoint = releasePoint
            };
        }

        // Tier 2: Opt-in Clipboard fallback (never run inside our own UI or blocked terminal processes)
        if (!settings.EnableClipboardFallback)
        {
            _log?.Invoke($"未捕获到文本 [{winInfo.ProcessName}]：UIA 无选区（已关闭剪贴板回退，不触发复制）");
            return null;
        }

        if (winInfo.ProcessId == Environment.ProcessId)
        {
            _log?.Invoke($"未捕获到文本 [{winInfo.ProcessName}]：本应用窗口仅使用 UIA 通道，跳过剪贴板回退");
            return null;
        }

        if (settings.ClipboardFallbackBlockedApps.Any(a => a.Equals(winInfo.ProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            _log?.Invoke($"未捕获到文本 [{winInfo.ProcessName}]：UIA 无选区（终端类应用已禁用 Ctrl+C 回退）");
            return null;
        }

        var fallbackText = await _clipboardGuard.CaptureSelectionViaCopyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(fallbackText))
        {
            var hint = winInfo.IsPotentiallyElevated ? "（提示：目标进程可能以管理员权限运行，受 UIPI 隔离限制）" : string.Empty;
            _log?.Invoke($"未捕获到文本 [{winInfo.ProcessName}]：UIA 与剪贴板回退均未返回选区{hint}");
            return null;
        }

        _log?.Invoke($"捕获成功 [{winInfo.ProcessName}]：剪贴板回退通道 ({fallbackText.Length} 字符, 可编辑={uiaResult.IsEditable})");
        return new SelectionContext
        {
            Text = fallbackText,
            ProcessName = winInfo.ProcessName,
            WindowTitle = winInfo.WindowTitle,
            WindowHandle = winInfo.Handle,
            IsEditable = uiaResult.IsEditable,
            IsPasswordField = false,
            Source = SelectionCaptureSource.ClipboardFallback,
            SelectionBounds = null,
            AnchorPoint = releasePoint
        };
    }

    private sealed record UiaProbeResult(
        string? SelectedText,
        ScreenRect? Bounds,
        bool IsEditable,
        bool IsPasswordField);

    private static UiaProbeResult TryCaptureViaUiAutomation(ScreenPoint releasePoint, IntPtr foregroundHwnd)
    {
        try
        {
            var candidates = new List<AutomationElement>(3);

            try
            {
                var focused = AutomationElement.FocusedElement;
                if (focused is not null)
                {
                    if (focused.Current.IsPassword)
                    {
                        return new UiaProbeResult(null, null, false, true);
                    }
                    candidates.Add(focused);
                }
            }
            catch { }

            try
            {
                var fromPoint = AutomationElement.FromPoint(new System.Windows.Point(releasePoint.X, releasePoint.Y));
                if (fromPoint is not null)
                {
                    if (fromPoint.Current.IsPassword)
                    {
                        return new UiaProbeResult(null, null, false, true);
                    }
                    candidates.Add(fromPoint);
                }
            }
            catch { }

            var bestEditable = false;
            foreach (var startElement in candidates)
            {
                if (IsElementEditable(startElement))
                {
                    bestEditable = true;
                }

                var probe = TryExtractTextPatternUpTree(startElement, bestEditable);
                if (probe is not null)
                {
                    return probe;
                }
            }

            // Fallback within UIA only: check the foreground window's Document element (helps Chromium/Edge/Electron
            // when drag release lands slightly outside the leaf text node)
            if (foregroundHwnd != IntPtr.Zero)
            {
                try
                {
                    var root = AutomationElement.FromHandle(foregroundHwnd);
                    if (root is not null)
                    {
                        var doc = root.FindFirst(
                            TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                        if (doc is not null)
                        {
                            var probe = TryExtractTextPatternFromElement(doc, bestEditable);
                            if (probe is not null)
                            {
                                return probe;
                            }
                        }
                    }
                }
                catch { }
            }

            return new UiaProbeResult(null, null, bestEditable, false);
        }
        catch
        {
            return new UiaProbeResult(null, null, false, false);
        }
    }

    private static UiaProbeResult? TryExtractTextPatternUpTree(AutomationElement startElement, bool fallbackEditable)
    {
        var current = startElement;
        // Walk up to 32 levels so deeply nested DOM text nodes in Edge/Chrome/Electron reach Document.TextPattern
        for (var depth = 0; depth < 32 && current is not null; depth++)
        {
            try
            {
                var probe = TryExtractTextPatternFromElement(current, fallbackEditable);
                if (probe is not null)
                {
                    return probe;
                }

                current = TreeWalker.RawViewWalker.GetParent(current);
            }
            catch
            {
                break;
            }
        }

        return null;
    }

    private static UiaProbeResult? TryExtractTextPatternFromElement(AutomationElement element, bool fallbackEditable)
    {
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj) &&
            patternObj is TextPattern textPattern)
        {
            var ranges = textPattern.GetSelection();
            if (ranges is { Length: > 0 })
            {
                var parts = new List<string>(ranges.Length);
                foreach (var r in ranges)
                {
                    var text = r.GetText(16384);
                    if (!string.IsNullOrEmpty(text))
                    {
                        parts.Add(text);
                    }
                }

                var selectedText = string.Concat(parts);
                if (!string.IsNullOrWhiteSpace(selectedText))
                {
                    var isEditable = DetermineRangeEditable(ranges[0], element, fallbackEditable);
                    ScreenRect? bounds = ExtractBoundingRect(ranges[0]);
                    return new UiaProbeResult(selectedText, bounds, isEditable, false);
                }
            }
        }

        return null;
    }

    private static ScreenRect? ExtractBoundingRect(TextPatternRange range)
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
        catch
        {
            // Fall back to release point
        }
        return null;
    }

    private static bool DetermineRangeEditable(TextPatternRange range, AutomationElement ownerElement, bool fallbackEditable)
    {
        try
        {
            var readOnlyAttr = range.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
            if (readOnlyAttr is bool isReadOnly)
            {
                return !isReadOnly;
            }
        }
        catch { }

        return IsElementEditable(ownerElement) || fallbackEditable;
    }

    private static bool IsElementEditable(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valPatternObj) &&
                valPatternObj is ValuePattern valuePattern)
            {
                return !valuePattern.Current.IsReadOnly;
            }

            var controlType = element.Current.ControlType;
            if (controlType == ControlType.Edit)
            {
                return element.Current.IsEnabled;
            }
        }
        catch
        {
            // Best-effort
        }

        return false;
    }
}
