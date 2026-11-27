using System.Runtime.InteropServices;
using WinPopClip.Core.Models;

namespace WinPopClip.App.Native;

public sealed record SelectionGestureEventArgs(
    ScreenPoint StartPoint,
    ScreenPoint ReleasePoint,
    bool IsDoubleClick,
    long EventSequence = 0,
    bool SawIBeamCursor = false,
    bool WindowMovedDuringDrag = false);

public sealed record DismissEventArgs(long EventSequence);

/// <summary>
/// Global low-level mouse & keyboard hook service running on a dedicated lightweight native Win32
/// message-pump thread (completely decoupled from WPF UI / COM / Clipboard work) so Windows mouse
/// input can NEVER stall or freeze even if the UI or clipboard is momentarily busy.
/// </summary>
public sealed class GlobalInputHookService : IDisposable
{
    private readonly Func<ScreenPoint, bool> _isPointInsideToolbar;
    private readonly Func<AppSettings> _getSettings;
    private readonly Action<string>? _log;

    private readonly object _lifecycleLock = new();
    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _mouseHook = IntPtr.Zero;
    private IntPtr _keyboardHook = IntPtr.Zero;
    private Win32Interop.LowLevelHookProc? _mouseProc;
    private Win32Interop.LowLevelHookProc? _keyboardProc;
    private volatile bool _running;

    private bool _isLeftButtonDown;
    private bool _dragSawIBeam;
    private int _dragIBeamSamples;
    private IntPtr _downForegroundHwnd;
    private Win32Interop.RECT _downForegroundRect;
    private ScreenPoint _leftDownPoint;
    private long _lastClickTick;
    private ScreenPoint _lastClickPoint;
    private long _eventSequence;

    /// <summary>Set to true while ClipboardGuard is sending Ctrl+C/V/X so hooks ignore synthetic events.</summary>
    public volatile bool SuppressKeyboardDismiss;

    public event EventHandler<SelectionGestureEventArgs>? SelectionGestureCompleted;
    public event EventHandler<DismissEventArgs>? DismissRequested;
    public event EventHandler? DragStarted;

    public GlobalInputHookService(
        Func<ScreenPoint, bool> isPointInsideToolbar,
        Func<AppSettings> getSettings,
        Action<string>? log = null)
    {
        _isPointInsideToolbar = isPointInsideToolbar;
        _getSettings = getSettings;
        _log = log;
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_running) return;
            _running = true;

            using var readyEvent = new ManualResetEventSlim(false);
            var thread = new Thread(() => RunHookMessageLoop(readyEvent))
            {
                Name = "Selector-LowLevelHookThread",
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            _hookThread = thread;
            thread.Start();
            readyEvent.Wait(TimeSpan.FromMilliseconds(1500));
        }
    }

    public void Stop()
    {
        Thread? threadToJoin;
        uint threadId;

        lock (_lifecycleLock)
        {
            if (!_running && _hookThread is null) return;
            _running = false;
            threadToJoin = _hookThread;
            threadId = _hookThreadId;
            _hookThread = null;
            _hookThreadId = 0;
        }

        if (threadId != 0)
        {
            Win32Interop.PostThreadMessageW(threadId, Win32Interop.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        if (threadToJoin is not null && threadToJoin.IsAlive && threadToJoin != Thread.CurrentThread)
        {
            threadToJoin.Join(TimeSpan.FromMilliseconds(800));
        }
    }

    private void RunHookMessageLoop(ManualResetEventSlim readyEvent)
    {
        _hookThreadId = Win32Interop.GetCurrentThreadId();
        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;

        try
        {
            var moduleHandle = Win32Interop.GetModuleHandle(null);

            _mouseHook = Win32Interop.SetWindowsHookEx(Win32Interop.WH_MOUSE_LL, _mouseProc, moduleHandle, 0);
            if (_mouseHook == IntPtr.Zero)
            {
                _log?.Invoke($"[错误] 安装全局鼠标钩子失败 (Win32Error={Marshal.GetLastWin32Error()})");
            }

            _keyboardHook = Win32Interop.SetWindowsHookEx(Win32Interop.WH_KEYBOARD_LL, _keyboardProc, moduleHandle, 0);
            if (_keyboardHook == IntPtr.Zero)
            {
                _log?.Invoke($"[错误] 安装全局键盘钩子失败 (Win32Error={Marshal.GetLastWin32Error()})");
            }

            if (_mouseHook != IntPtr.Zero && _keyboardHook != IntPtr.Zero)
            {
                _log?.Invoke("全局鼠标与键盘钩子已就绪（独立轻量消息线程）");
            }
        }
        finally
        {
            readyEvent.Set();
        }

        try
        {
            while (_running && Win32Interop.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Win32Interop.TranslateMessage(ref msg);
                Win32Interop.DispatchMessageW(ref msg);
            }
        }
        finally
        {
            if (_mouseHook != IntPtr.Zero)
            {
                Win32Interop.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
            if (_keyboardHook != IntPtr.Zero)
            {
                Win32Interop.UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Win32Interop.MSLLHOOKSTRUCT>(lParam);

            // Ignore synthetic mouse events injected by automation/tools
            if ((info.flags & Win32Interop.LLMHF_INJECTED) != 0)
            {
                return Win32Interop.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            var point = new ScreenPoint(info.pt.X, info.pt.Y);
            var msg = wParam.ToInt32();

            switch (msg)
            {
                case Win32Interop.WM_LBUTTONDOWN:
                    if (_isPointInsideToolbar(point))
                    {
                        _isLeftButtonDown = false;
                        break;
                    }

                    _isLeftButtonDown = true;
                    _leftDownPoint = point;
                    _dragSawIBeam = Win32Interop.IsCurrentCursorIBeam();
                    _dragIBeamSamples = 0;
                    _downForegroundHwnd = Win32Interop.GetForegroundWindow();
                    _downForegroundRect = default;
                    if (_downForegroundHwnd != IntPtr.Zero)
                    {
                        Win32Interop.GetWindowRect(_downForegroundHwnd, out _downForegroundRect);
                    }
                    RaiseDismissAsync();
                    RaiseDragStartedAsync();
                    break;

                case Win32Interop.WM_MOUSEMOVE:
                    if (_isLeftButtonDown && !_dragSawIBeam && _dragIBeamSamples < 6)
                    {
                        var dx = point.X - _leftDownPoint.X;
                        var dy = point.Y - _leftDownPoint.Y;
                        var distSq = dx * dx + dy * dy;

                        // Lightweight I-beam check (up to 6 distance-gated samples per drag, short-circuiting as soon
                        // as IDC_IBEAM is observed) so Doubao-style deferred capture reliably catches drags that start
                        // on a message bubble margin and end outside the text area while still rejecting window/scrollbar drags.
                        var minDist = 10 + _dragIBeamSamples * 24; // 10, 34, 58, 82, 106, 130 px
                        if (distSq >= minDist * minDist)
                        {
                            _dragIBeamSamples++;
                            if (Win32Interop.IsCurrentCursorIBeam())
                            {
                                _dragSawIBeam = true;
                            }
                        }
                    }
                    break;

                case Win32Interop.WM_LBUTTONUP:
                    if (!_isLeftButtonDown)
                    {
                        break;
                    }

                    _isLeftButtonDown = false;
                    if (_isPointInsideToolbar(point))
                    {
                        break;
                    }

                    HandleLeftButtonUp(point);
                    break;

                case Win32Interop.WM_RBUTTONDOWN:
                case Win32Interop.WM_MBUTTONDOWN:
                case Win32Interop.WM_MOUSEWHEEL:
                    if (!_isPointInsideToolbar(point))
                    {
                        RaiseDismissAsync();
                    }
                    break;
            }
        }

        return Win32Interop.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void HandleLeftButtonUp(ScreenPoint upPoint)
    {
        var settings = _getSettings();
        if (!settings.Enabled)
        {
            return;
        }

        // Check modifier key trigger mode
        if (settings.TriggerMode == TriggerMode.RequireCtrlKey && !Win32Interop.IsKeyDown(Win32Interop.VK_CONTROL))
        {
            return;
        }
        if (settings.TriggerMode == TriggerMode.RequireAltKey && !Win32Interop.IsKeyDown(Win32Interop.VK_MENU))
        {
            return;
        }

        var startPoint = _leftDownPoint;
        var dx = Math.Abs(upPoint.X - startPoint.X);
        var dy = Math.Abs(upPoint.Y - startPoint.Y);
        var distance = Math.Sqrt(dx * dx + dy * dy);

        var now = Environment.TickCount64;
        var doubleClickEnabled = settings.EnableDoubleClickSelection || settings.EnableDialogDoubleClickSummon;
        var isDoubleClick = doubleClickEnabled &&
                            (now - _lastClickTick) <= 500 &&
                            Math.Abs(upPoint.X - _lastClickPoint.X) <= 14 &&
                            Math.Abs(upPoint.Y - _lastClickPoint.Y) <= 14;

        _lastClickTick = now;
        _lastClickPoint = upPoint;

        var isDragSelection = distance >= Math.Max(4, settings.DragThresholdPixels);
        var isShiftClickSelection = !isDoubleClick && Win32Interop.IsKeyDown(Win32Interop.VK_SHIFT);
        if (isDragSelection || isDoubleClick || isShiftClickSelection)
        {
            var sawIBeam = _dragSawIBeam || Win32Interop.IsCurrentCursorIBeam();
            var windowMoved = false;
            if (isDragSelection && _downForegroundHwnd != IntPtr.Zero &&
                Win32Interop.GetWindowRect(_downForegroundHwnd, out var upRect))
            {
                windowMoved = upRect.Left != _downForegroundRect.Left ||
                              upRect.Top != _downForegroundRect.Top ||
                              upRect.Right != _downForegroundRect.Right ||
                              upRect.Bottom != _downForegroundRect.Bottom;
            }

            var seq = Interlocked.Increment(ref _eventSequence);
            var args = new SelectionGestureEventArgs(startPoint, upPoint, isDoubleClick, seq, sawIBeam, windowMoved);
            ThreadPool.UnsafeQueueUserWorkItem(state =>
            {
                try
                {
                    SelectionGestureCompleted?.Invoke(this, (SelectionGestureEventArgs)state!);
                }
                catch { }
            }, args);
        }
    }

    private void RaiseDismissAsync()
    {
        var handler = DismissRequested;
        if (handler is null) return;

        var seq = Interlocked.Increment(ref _eventSequence);
        ThreadPool.UnsafeQueueUserWorkItem(state =>
        {
            try
            {
                handler.Invoke(this, new DismissEventArgs((long)state!));
            }
            catch { }
        }, seq);
    }

    private void RaiseDragStartedAsync()
    {
        var handler = DragStarted;
        if (handler is null) return;

        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                handler.Invoke(this, EventArgs.Empty);
            }
            catch { }
        }, null);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !SuppressKeyboardDismiss)
        {
            var msg = wParam.ToInt32();
            if (msg == Win32Interop.WM_KEYDOWN || msg == Win32Interop.WM_SYSKEYDOWN)
            {
                var kbd = Marshal.PtrToStructure<Win32Interop.KBDLLHOOKSTRUCT>(lParam);

                // Ignore injected keys (such as our own Ctrl+C/V/X) so we don't self-cancel capture
                if ((kbd.flags & Win32Interop.LLKHF_INJECTED) != 0 ||
                    kbd.dwExtraInfo == Win32Interop.SelfInputExtraInfo)
                {
                    return Win32Interop.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
                }

                // Ignore pure modifier presses so user can hold Ctrl/Alt/Shift while selecting
                if (kbd.vkCode != Win32Interop.VK_CONTROL &&
                    kbd.vkCode != Win32Interop.VK_LCONTROL &&
                    kbd.vkCode != Win32Interop.VK_RCONTROL &&
                    kbd.vkCode != Win32Interop.VK_MENU &&
                    kbd.vkCode != Win32Interop.VK_LMENU &&
                    kbd.vkCode != Win32Interop.VK_RMENU &&
                    kbd.vkCode != Win32Interop.VK_SHIFT &&
                    kbd.vkCode != Win32Interop.VK_LSHIFT &&
                    kbd.vkCode != Win32Interop.VK_RSHIFT)
                {
                    RaiseDismissAsync();
                }
            }
        }

        return Win32Interop.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
    }
}
