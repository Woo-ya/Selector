using System.Diagnostics;
using System.Runtime.InteropServices;
using WinPopClip.Core.Models;

namespace WinPopClip.App.Native;

internal sealed record NativeUiaResult(
    string? SelectedText,
    ScreenRect? Bounds,
    bool IsEditable,
    bool IsPasswordField,
    string? DiagnosticNote = null);

/// <summary>
/// High-performance, persistent-MTA Windows COM UI Automation 3 (CUIAutomation8) selection & caret reader.
/// Key reliability design:
/// 1. Single persistent MTA worker thread & single reused CUIAutomation8 COM instance (zero leaked threads/COM proxies).
/// 2. Lazy short-circuit probing: checks GetFocusedElement() FIRST and returns immediately if selection found,
///    avoiding unnecessary cross-process ElementFromPoint calls on Electron/Chromium (WorkBuddy, Antigravity, Edge).
/// 3. Never discards already-fetched candidates on slow refocus; automatically resets COM instance on error.
/// </summary>
internal static class NativeUia3Reader
{
    private const int UIA_ValuePatternId = 10002;
    private const int UIA_TextPatternId = 10014;
    private const int UIA_TextPattern2Id = 10024;
    private const int UIA_TextChildPatternId = 10029;

    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_HasKeyboardFocusPropertyId = 30008;
    private const int UIA_IsEnabledPropertyId = 30010;
    private const int UIA_ClassNamePropertyId = 30012;
    private const int UIA_IsPasswordPropertyId = 30019;
    private const int UIA_EditControlTypeId = 50004;
    private const int UIA_DocumentControlTypeId = 50030;
    private const int UIA_IsReadOnlyAttributeId = 40015;

    private static readonly Guid ClsidCUIAutomation8 = new("E22AD333-B25F-460C-83D0-0581107395C9");
    private static readonly Guid ClsidCUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");

    private sealed class ProbeRequest
    {
        public required ScreenPoint ReleasePoint { get; init; }
        public required IntPtr ForegroundHwnd { get; init; }
        public ScreenPoint? DragStartPoint { get; init; }
        public TaskCompletionSource<NativeUiaResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly object WorkerLock = new();
    private static readonly AutoResetEvent WorkAvailable = new(false);
    private static ProbeRequest? _pendingRequest;
    private static Thread? _workerThread;

    public static async Task<NativeUiaResult> TryReadSelectionAsync(
        ScreenPoint releasePoint,
        IntPtr foregroundHwnd,
        ScreenPoint? dragStartPoint,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        EnsureWorkerStarted();

        var req = new ProbeRequest
        {
            ReleasePoint = releasePoint,
            ForegroundHwnd = foregroundHwnd,
            DragStartPoint = dragStartPoint
        };

        ProbeRequest? superseded;
        lock (WorkerLock)
        {
            superseded = _pendingRequest;
            _pendingRequest = req;
        }
        superseded?.Completion.TrySetResult(new NativeUiaResult(null, null, false, false, "superseded"));
        WorkAvailable.Set();

        try
        {
            var completed = await Task.WhenAny(req.Completion.Task, Task.Delay(timeoutMs, cancellationToken)).ConfigureAwait(false);
            if (completed == req.Completion.Task)
            {
                return await req.Completion.Task.ConfigureAwait(false);
            }
            return new NativeUiaResult(null, null, false, false, "timeout");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new NativeUiaResult(null, null, false, false, ex.Message);
        }
    }

    private static void EnsureWorkerStarted()
    {
        lock (WorkerLock)
        {
            if (_workerThread is { IsAlive: true }) return;
            _workerThread = new Thread(WorkerLoop)
            {
                Name = "Selector-Uia3Worker",
                IsBackground = true
            };
            _workerThread.SetApartmentState(ApartmentState.MTA);
            _workerThread.Start();
        }
    }

    private static void WorkerLoop()
    {
        IUIAutomation? uia = CreateAutomation();

        while (true)
        {
            WorkAvailable.WaitOne();

            while (true)
            {
                ProbeRequest? req;
                lock (WorkerLock)
                {
                    req = _pendingRequest;
                    _pendingRequest = null;
                }
                if (req is null) break;

                try
                {
                    uia ??= CreateAutomation();
                    var res = uia is null
                        ? new NativeUiaResult(null, null, false, false, "COM CUIAutomation 初始化失败")
                        : TryReadSelectionCore(uia, req.ReleasePoint, req.ForegroundHwnd, req.DragStartPoint);
                    req.Completion.TrySetResult(res);
                }
                catch (COMException comEx)
                {
                    // Recreate COM automation instance if target RPC/proxy went stale
                    ReleaseCom(uia);
                    uia = null;
                    req.Completion.TrySetResult(new NativeUiaResult(null, null, false, false, $"COM 0x{comEx.HResult:X8}"));
                }
                catch (Exception ex)
                {
                    req.Completion.TrySetResult(new NativeUiaResult(null, null, false, false, ex.Message));
                }
            }
        }
    }

    private static NativeUiaResult TryReadSelectionCore(
        IUIAutomation uia,
        ScreenPoint releasePoint,
        IntPtr foregroundHwnd,
        ScreenPoint? dragStartPoint)
    {
        var sw = Stopwatch.StartNew();
        const int AncestorBudgetMs = 520;

        IUIAutomationTreeWalker? walker = null;
        try { walker = uia.ControlViewWalker; } catch { }
        try { walker ??= uia.RawViewWalker; } catch { }

        var probed = new List<IUIAutomationElement>(4);
        var bestEditable = false;
        ScreenRect? bestCaretBounds = null;
        string? firstElSummary = null;

        bool AlreadyProbed(IUIAutomationElement candidate)
        {
            foreach (var existing in probed)
            {
                try
                {
                    if (uia.CompareElements(existing, candidate) != 0) return true;
                }
                catch { }
            }
            return false;
        }

        NativeUiaResult? ProbeElement(IUIAutomationElement start, out bool isPassword)
        {
            isPassword = false;
            if (IsPassword(start))
            {
                isPassword = true;
                return null;
            }
            if (AlreadyProbed(start)) return null;
            probed.Add(start);

            firstElSummary ??= DescribeElementBrief(start);
            if (IsElementEditable(start)) bestEditable = true;

            // Fast path A: Direct TextPattern / TextPattern2
            var direct = TryExtractFromElement(start, bestEditable);
            if (direct is not null)
            {
                if (!string.IsNullOrWhiteSpace(direct.SelectedText)) return direct;
                if (direct.IsEditable) bestEditable = true;
                bestCaretBounds ??= direct.Bounds;
            }

            // Fast path B: UIA3 TextChildPattern 1-hop jump to Document
            var viaChild = TryExtractViaTextChild(start, bestEditable);
            if (viaChild is not null)
            {
                if (!string.IsNullOrWhiteSpace(viaChild.SelectedText)) return viaChild;
                if (viaChild.IsEditable) bestEditable = true;
                bestCaretBounds ??= viaChild.Bounds;
            }

            // Fast path C: Ancestor chain walk (up to 16 hops)
            if (walker is not null)
            {
                var cur = start;
                for (var depth = 0; depth < 16 && sw.ElapsedMilliseconds < AncestorBudgetMs; depth++)
                {
                    IUIAutomationElement? parent = null;
                    try { parent = walker.GetParentElement(cur); } catch { break; }
                    if (parent is null) break;

                    if (IsElementEditable(parent)) bestEditable = true;

                    var res = TryExtractFromElement(parent, bestEditable)
                              ?? TryExtractViaTextChild(parent, bestEditable);
                    if (res is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(res.SelectedText)) return res;
                        if (res.IsEditable) bestEditable = true;
                        bestCaretBounds ??= res.Bounds;
                    }

                    cur = parent;
                }
            }

            return null;
        }

        // Step 1 (Lazy short-circuit): Focused element FIRST — returns immediately in ~5-20ms on editors/WorkBuddy composer!
        try
        {
            var focused = uia.GetFocusedElement();
            if (focused is not null)
            {
                var hit = ProbeElement(focused, out var isPwd);
                if (isPwd) return new NativeUiaResult(null, null, false, true);
                if (hit is not null) return hit;
            }
        }
        catch { }

        // Step 2: Element directly under release point (covers read-only web/Electron chat bubbles)
        if (sw.ElapsedMilliseconds < AncestorBudgetMs)
        {
            try
            {
                var fromPt = uia.ElementFromPoint(new Win32Interop.POINT
                {
                    X = (int)Math.Round(releasePoint.X),
                    Y = (int)Math.Round(releasePoint.Y)
                });
                if (fromPt is not null)
                {
                    var hit = ProbeElement(fromPt, out var isPwd);
                    if (isPwd) return new NativeUiaResult(null, null, false, true);
                    if (hit is not null) return hit;
                }
            }
            catch { }
        }

        // Step 3: Drag midpoint only if user actually dragged >= 8px and previous steps found nothing
        if (dragStartPoint is { } startPt &&
            (Math.Abs(releasePoint.X - startPt.X) >= 8 || Math.Abs(releasePoint.Y - startPt.Y) >= 8) &&
            sw.ElapsedMilliseconds < 320)
        {
            try
            {
                var midPt = uia.ElementFromPoint(new Win32Interop.POINT
                {
                    X = (int)Math.Round((startPt.X + releasePoint.X) / 2.0),
                    Y = (int)Math.Round((startPt.Y + releasePoint.Y) / 2.0)
                });
                if (midPt is not null)
                {
                    var hit = ProbeElement(midPt, out var isPwd);
                    if (isPwd) return new NativeUiaResult(null, null, false, true);
                    if (hit is not null) return hit;
                }
            }
            catch { }
        }

        // Step 4: Only try ElementFromHandle if no element was reachable at all
        if (probed.Count == 0 && foregroundHwnd != IntPtr.Zero && sw.ElapsedMilliseconds < 320)
        {
            try
            {
                var fromHwnd = uia.ElementFromHandle(foregroundHwnd);
                if (fromHwnd is not null)
                {
                    var hit = ProbeElement(fromHwnd, out var isPwd);
                    if (isPwd) return new NativeUiaResult(null, null, false, true);
                    if (hit is not null) return hit;
                }
            }
            catch { }
        }

        var note = $"cand={probed.Count} {firstElSummary ?? "none"} edit={bestEditable} {sw.ElapsedMilliseconds}ms";
        return new NativeUiaResult(null, bestCaretBounds, bestEditable, false, note);
    }

    private static void ReleaseCom(object? obj)
    {
        if (obj is not null && Marshal.IsComObject(obj))
        {
            try { Marshal.ReleaseComObject(obj); } catch { }
        }
    }

    private static string DescribeElementBrief(IUIAutomationElement el)
    {
        try
        {
            var ct = el.GetCurrentPropertyValue(UIA_ControlTypePropertyId) is int c ? c : 0;
            var cls = el.GetCurrentPropertyValue(UIA_ClassNamePropertyId) as string ?? string.Empty;
            if (cls.Length > 28) cls = cls[..28];
            return $"ct={ct} cls='{cls}'";
        }
        catch
        {
            return "ct=?";
        }
    }

    private static IUIAutomation? CreateAutomation()
    {
        try
        {
            var type8 = Type.GetTypeFromCLSID(ClsidCUIAutomation8);
            if (type8 is not null && Activator.CreateInstance(type8) is IUIAutomation uia8)
            {
                return uia8;
            }
        }
        catch { }

        try
        {
            var type = Type.GetTypeFromCLSID(ClsidCUIAutomation);
            if (type is not null && Activator.CreateInstance(type) is IUIAutomation uia)
            {
                return uia;
            }
        }
        catch { }

        return null;
    }

    private static NativeUiaResult? TryExtractViaTextChild(IUIAutomationElement element, bool fallbackEditable)
    {
        try
        {
            var obj = element.GetCurrentPattern(UIA_TextChildPatternId);
            if (obj is IUIAutomationTextChildPattern childPattern)
            {
                var container = childPattern.TextContainer;
                if (container is not null)
                {
                    return TryExtractFromElement(container, fallbackEditable || IsElementEditable(container));
                }
            }
        }
        catch { }
        return null;
    }

    private static NativeUiaResult? TryExtractFromElement(IUIAutomationElement element, bool fallbackEditable)
    {
        IUIAutomationTextPattern? textPattern = null;
        try
        {
            textPattern = element.GetCurrentPattern(UIA_TextPatternId) as IUIAutomationTextPattern;
        }
        catch { }

        if (textPattern is null)
        {
            try
            {
                textPattern = element.GetCurrentPattern(UIA_TextPattern2Id) as IUIAutomationTextPattern;
            }
            catch { }
        }

        if (textPattern is null) return null;

        try
        {
            var ranges = textPattern.GetSelection();
            if (ranges is null || ranges.Length <= 0) return null;

            var count = Math.Min(ranges.Length, 16);
            var parts = new List<string>(count);
            IUIAutomationTextRange? firstNonEmpty = null;
            IUIAutomationTextRange? firstRange = null;

            for (var i = 0; i < count; i++)
            {
                var r = ranges.GetElement(i);
                if (r is null) continue;
                firstRange ??= r;
                var txt = r.GetText(16384);
                if (!string.IsNullOrEmpty(txt))
                {
                    firstNonEmpty ??= r;
                    parts.Add(txt);
                }
            }

            var combined = string.Concat(parts);
            if (string.IsNullOrWhiteSpace(combined) || firstNonEmpty is null)
            {
                if (firstRange is not null)
                {
                    var caretEditable = DetermineRangeEditable(firstRange, element, fallbackEditable);
                    if (caretEditable)
                    {
                        return new NativeUiaResult(null, ExtractBounds(firstRange), IsEditable: true, IsPasswordField: false);
                    }
                }
                return null;
            }

            var isEditable = DetermineRangeEditable(firstNonEmpty, element, fallbackEditable);
            var bounds = ExtractBounds(firstNonEmpty);
            return new NativeUiaResult(combined, bounds, isEditable, false);
        }
        catch
        {
            return null;
        }
    }

    private static ScreenRect? ExtractBounds(IUIAutomationTextRange range)
    {
        try
        {
            var raw = range.GetBoundingRectangles();
            if (raw is { Length: >= 4 })
            {
                var minX = double.MaxValue;
                var minY = double.MaxValue;
                var maxR = double.MinValue;
                var maxB = double.MinValue;

                for (var i = 0; i + 3 < raw.Length; i += 4)
                {
                    var x = raw[i];
                    var y = raw[i + 1];
                    var w = raw[i + 2];
                    var h = raw[i + 3];
                    if (w <= 0 || h <= 0) continue;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x + w > maxR) maxR = x + w;
                    if (y + h > maxB) maxB = y + h;
                }

                if (maxR > minX && maxB > minY && minX < double.MaxValue)
                {
                    return new ScreenRect(minX, minY, maxR - minX, maxB - minY);
                }
            }
        }
        catch { }
        return null;
    }

    private static bool DetermineRangeEditable(IUIAutomationTextRange range, IUIAutomationElement owner, bool fallbackEditable)
    {
        try
        {
            var attr = range.GetAttributeValue(UIA_IsReadOnlyAttributeId);
            if (attr is bool isReadOnly)
            {
                return !isReadOnly;
            }
        }
        catch { }

        return IsElementEditable(owner) || fallbackEditable;
    }

    private static bool IsPassword(IUIAutomationElement element)
    {
        try
        {
            return element.GetCurrentPropertyValue(UIA_IsPasswordPropertyId) is bool b && b;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsElementEditable(IUIAutomationElement element)
    {
        try
        {
            if (element.GetCurrentPattern(UIA_ValuePatternId) is IUIAutomationValuePattern vp)
            {
                return vp.CurrentIsReadOnly == 0;
            }

            if (element.GetCurrentPropertyValue(UIA_ControlTypePropertyId) is int ct)
            {
                if (ct == UIA_EditControlTypeId)
                {
                    return element.GetCurrentPropertyValue(UIA_IsEnabledPropertyId) is not bool en || en;
                }

                if (ct == UIA_DocumentControlTypeId &&
                    element.GetCurrentPropertyValue(UIA_HasKeyboardFocusPropertyId) is bool focused && focused)
                {
                    return element.GetCurrentPropertyValue(UIA_IsEnabledPropertyId) is not bool en || en;
                }
            }
        }
        catch { }
        return false;
    }

    // ───────────── Minimal Immutable Windows UIA3 COM Interfaces ─────────────

    [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        int CompareElements(IUIAutomationElement el1, IUIAutomationElement el2);
        int CompareRuntimeIds(int[] runtimeId1, int[] runtimeId2);
        IUIAutomationElement GetRootElement();
        IUIAutomationElement ElementFromHandle(IntPtr hwnd);
        IUIAutomationElement ElementFromPoint(Win32Interop.POINT pt);
        IUIAutomationElement GetFocusedElement();
        IUIAutomationElement GetRootElementBuildCache(IntPtr cacheRequest);
        IUIAutomationElement ElementFromHandleBuildCache(IntPtr hwnd, IntPtr cacheRequest);
        IUIAutomationElement ElementFromPointBuildCache(Win32Interop.POINT pt, IntPtr cacheRequest);
        IUIAutomationElement GetFocusedElementBuildCache(IntPtr cacheRequest);
        IUIAutomationTreeWalker CreateTreeWalker(IntPtr condition);
        IUIAutomationTreeWalker ControlViewWalker { get; }
        IUIAutomationTreeWalker ContentViewWalker { get; }
        IUIAutomationTreeWalker RawViewWalker { get; }
    }

    [ComImport, Guid("4042C624-389C-4AFC-A630-9DF854A541FC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTreeWalker
    {
        IUIAutomationElement? GetParentElement(IUIAutomationElement element);
        IUIAutomationElement? GetFirstChildElement(IUIAutomationElement element);
        IUIAutomationElement? GetLastChildElement(IUIAutomationElement element);
        IUIAutomationElement? GetNextSiblingElement(IUIAutomationElement element);
        IUIAutomationElement? GetPreviousSiblingElement(IUIAutomationElement element);
    }

    [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        IntPtr GetRuntimeId();
        IUIAutomationElement? FindFirst(int scope, IntPtr condition);
        IntPtr FindAll(int scope, IntPtr condition);
        IUIAutomationElement? FindFirstBuildCache(int scope, IntPtr condition, IntPtr cacheRequest);
        IntPtr FindAllBuildCache(int scope, IntPtr condition, IntPtr cacheRequest);
        IUIAutomationElement BuildUpdatedCache(IntPtr cacheRequest);
        [return: MarshalAs(UnmanagedType.Struct)]
        object? GetCurrentPropertyValue(int propertyId);
        [return: MarshalAs(UnmanagedType.Struct)]
        object? GetCurrentPropertyValueEx(int propertyId, int ignoreDefaultValue);
        [return: MarshalAs(UnmanagedType.Struct)]
        object? GetCachedPropertyValue(int propertyId);
        [return: MarshalAs(UnmanagedType.Struct)]
        object? GetCachedPropertyValueEx(int propertyId, int ignoreDefaultValue);
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object? GetCurrentPatternAs(int patternId, ref Guid riid);
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object? GetCachedPatternAs(int patternId, ref Guid riid);
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object? GetCurrentPattern(int patternId);
    }

    [ComImport, Guid("32EBA289-3583-42C9-9C59-3B6D9A1E9B6A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextPattern
    {
        IUIAutomationTextRange RangeFromPoint(Win32Interop.POINT pt);
        IUIAutomationTextRange RangeFromChild(IUIAutomationElement child);
        IUIAutomationTextRangeArray? GetSelection();
        IUIAutomationTextRangeArray? GetVisibleRanges();
    }

    [ComImport, Guid("6552B038-AE05-40C8-ABFD-AA08352AAB86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextChildPattern
    {
        IUIAutomationElement? TextContainer { get; }
        IUIAutomationTextRange? TextRange { get; }
    }

    [ComImport, Guid("CE4AE76A-E717-4C98-81EA-47371D028EB6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRangeArray
    {
        int Length { get; }
        IUIAutomationTextRange? GetElement(int index);
    }

    [ComImport, Guid("A543CC6A-F4AE-494B-8239-C814481187A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRange
    {
        IUIAutomationTextRange Clone();
        int Compare(IUIAutomationTextRange range);
        int CompareEndpoints(int srcEndPoint, IUIAutomationTextRange range, int targetEndPoint);
        void ExpandToEnclosingUnit(int textUnit);
        IUIAutomationTextRange? FindAttribute(int attr, [MarshalAs(UnmanagedType.Struct)] object val, int backward);
        IUIAutomationTextRange? FindText([MarshalAs(UnmanagedType.BStr)] string text, int backward, int ignoreCase);
        [return: MarshalAs(UnmanagedType.Struct)]
        object? GetAttributeValue(int attr);
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_R8)]
        double[]? GetBoundingRectangles();
        IUIAutomationElement? GetEnclosingElement();
        [return: MarshalAs(UnmanagedType.BStr)]
        string? GetText(int maxLength);
    }

    [ComImport, Guid("A94CD8B1-0844-4CD6-9D2D-640537AB39E9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationValuePattern
    {
        void SetValue([MarshalAs(UnmanagedType.BStr)] string val);
        string? CurrentValue { [return: MarshalAs(UnmanagedType.BStr)] get; }
        int CurrentIsReadOnly { get; }
    }
}
