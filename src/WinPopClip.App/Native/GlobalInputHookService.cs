using System.Diagnostics;
using System.Runtime.InteropServices;
using WinPopClip.Core.Models;

namespace WinPopClip.App.Native;

public sealed record SelectionGestureEventArgs(ScreenPoint ReleasePoint, bool IsDoubleClick);

/// <summary>
/// Global low-level mouse & keyboard hook service detecting text selection completion
/// (drag release, double-click word select) and dismissal gestures while ignoring our own synthetic inputs.
/// </summary>
public sealed class GlobalInputHookService : IDisposable
{
    private readonly Func<ScreenPoint, bool> _isPointInsideToolbar;
    private readonly Func<AppSettings> _getSettings;
    private readonly Action<string>? _log;

    private IntPtr _mouseHook = IntPtr.Zero;
    private IntPtr _keyboardHook = IntPtr.Zero;
    private Win32Interop.LowLevelHookProc? _mouseProc;
    private Win32Interop.LowLevelHookProc? _keyboardProc;

    private bool _isLeftButtonDown;
    private ScreenPoint _leftDownPoint;
    private long _lastClickTick;
    private ScreenPoint _lastClickPoint;

    /// <summary>Set to true while ClipboardGuard is sending Ctrl+C/V/X so hooks ignore synthetic events.</summary>
    public volatile bool SuppressKeyboardDismiss;

    public event EventHandler<SelectionGestureEventArgs>? SelectionGestureCompleted;
    public event EventHandler? DismissRequested;

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
        if (_mouseHook != IntPtr.Zero) return;

        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;

        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var moduleHandle = Win32Interop.GetModuleHandle(curModule?.ModuleName);

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
            _log?.Invoke("全局鼠标与键盘钩子已就绪");
        }
    }

    public void Stop()
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
                    DismissRequested?.Invoke(this, EventArgs.Empty);
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
                        DismissRequested?.Invoke(this, EventArgs.Empty);
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

        var dx = Math.Abs(upPoint.X - _leftDownPoint.X);
        var dy = Math.Abs(upPoint.Y - _leftDownPoint.Y);
        var distance = Math.Sqrt(dx * dx + dy * dy);

        var now = Environment.TickCount64;
        var isDoubleClick = settings.EnableDoubleClickSelection &&
                            (now - _lastClickTick) <= 450 &&
                            Math.Abs(upPoint.X - _lastClickPoint.X) <= 8 &&
                            Math.Abs(upPoint.Y - _lastClickPoint.Y) <= 8;

        _lastClickTick = now;
        _lastClickPoint = upPoint;

        var isDragSelection = distance >= Math.Max(4, settings.DragThresholdPixels);
        if (isDragSelection || isDoubleClick)
        {
            SelectionGestureCompleted?.Invoke(this, new SelectionGestureEventArgs(upPoint, isDoubleClick));
        }
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !SuppressKeyboardDismiss)
        {
            var msg = wParam.ToInt32();
            if (msg == Win32Interop.WM_KEYDOWN || msg == Win32Interop.WM_SYSKEYDOWN)
            {
                var kbd = Marshal.PtrToStructure<Win32Interop.KBDLLHOOKSTRUCT>(lParam);

                // CRITICAL FIX: Ignore injected keys (such as our own Ctrl+C fallback) so we don't self-cancel capture!
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
                    DismissRequested?.Invoke(this, EventArgs.Empty);
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
