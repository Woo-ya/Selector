using System.Runtime.InteropServices;
using System.Text;

namespace WinPopClip.App.Native;

internal static class Win32Interop
{
    public const int WH_MOUSE_LL = 14;
    public const int WH_KEYBOARD_LL = 13;

    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_GETOBJECT = 0x003D;
    public const int OBJID_CLIENT = -4;
    public const uint OBJID_CLIENT_UINT = 0xFFFFFFFCu;
    public const int UiaRootObjectId = -25;
    public static readonly Guid IID_IAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    public const int WM_GETTEXT = 0x000D;
    public const int WM_GETTEXTLENGTH = 0x000E;
    public const int WM_QUIT = 0x0012;
    public const int EM_GETSEL = 0x00B0;
    public const int WM_APP = 0x8000;
    public const int MA_NOACTIVATE = 3;

    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 0x0002;

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint CURSOR_SHOWING = 0x00000001;
    public static readonly IntPtr IDC_IBEAM = new(32513);
    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x0010;
    public static readonly IntPtr IDI_APPLICATION = new(32512);

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_INFO = 0x00000010;
    public const uint NIIF_INFO = 0x00000001;

    // Hook injection flags
    public const uint LLKHF_INJECTED = 0x00000010;
    public const uint LLMHF_INJECTED = 0x00000001;

    // Magic tag attached to dwExtraInfo of our own synthetic inputs
    public static readonly IntPtr SelfInputExtraInfo = new(0x5750434C); // "WPCL"

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const long WS_CAPTION = 0x00C00000L;
    public const long WS_THICKFRAME = 0x00040000L;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMSBT_MAINWINDOW = 2; // Mica
    public const int DWMWCP_ROUND = 2;

    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU = 0x12; // Alt
    public const ushort VK_LSHIFT = 0xA0;
    public const ushort VK_RSHIFT = 0xA1;
    public const ushort VK_LCONTROL = 0xA2;
    public const ushort VK_RCONTROL = 0xA3;
    public const ushort VK_LMENU = 0xA4;
    public const ushort VK_RMENU = 0xA5;
    public const ushort VK_ESCAPE = 0x1B;
    public const ushort VK_C = 0x43;
    public const ushort VK_V = 0x56;
    public const ushort VK_X = 0x58;

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint MAPVK_VK_TO_VSC = 0;

    public enum QUERY_USER_NOTIFICATION_STATE
    {
        QUNS_NOT_PRESENT = 1,
        QUNS_BUSY = 2,
        QUNS_RUNNING_D3D_FULL_SCREEN = 3,
        QUNS_PRESENTATION_MODE = 4,
        QUNS_ACCEPTS_NOTIFICATIONS = 5,
        QUNS_QUIET_TIME = 6,
        QUNS_APP = 7
    }

    public delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
        [FieldOffset(0)]
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, ref int wParam, ref int lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd,
        int msg,
        ref int wParam,
        ref int lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        StringBuilder lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadImageW(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconExW(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    public static RECT GetMonitorBounds(IntPtr hwnd)
    {
        var hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (hMon != IntPtr.Zero && GetMonitorInfoW(hMon, ref info))
        {
            return info.rcMonitor;
        }
        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    public static RECT GetWorkAreaForPoint(int x, int y)
    {
        var hMon = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (hMon != IntPtr.Zero && GetMonitorInfoW(hMon, ref info))
        {
            return info.rcWork;
        }
        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    public delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("oleacc.dll")]
    public static extern int AccessibleObjectFromWindow(
        IntPtr hwnd,
        uint dwId,
        ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppvObject);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE pquns);

    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    private static readonly Lazy<IntPtr> StandardIBeamCursor = new(() => LoadCursorW(IntPtr.Zero, IDC_IBEAM));

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    public static bool IsCurrentCursorIBeam()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        return GetCursorInfo(ref ci) &&
               (ci.flags & CURSOR_SHOWING) != 0 &&
               ci.hCursor != IntPtr.Zero &&
               ci.hCursor == StandardIBeamCursor.Value;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    public static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    public static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    public static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    public static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// Reliably sends Ctrl+<key> with hardware scan codes, SelfInputExtraInfo marker,
    /// and modifier sanitization so held Alt/Shift won't turn Ctrl+C into Ctrl+Shift+C (DevTools).
    /// </summary>
    public static void SendCtrlKey(ushort keyVk)
    {
        var cbSize = Marshal.SizeOf<INPUT>();
        var preRelease = new List<INPUT>();

        if (IsKeyDown(VK_LSHIFT)) preRelease.Add(MakeKeyInput(VK_LSHIFT, KEYEVENTF_KEYUP));
        if (IsKeyDown(VK_RSHIFT)) preRelease.Add(MakeKeyInput(VK_RSHIFT, KEYEVENTF_KEYUP));
        if (IsKeyDown(VK_LMENU)) preRelease.Add(MakeKeyInput(VK_LMENU, KEYEVENTF_KEYUP));
        if (IsKeyDown(VK_RMENU)) preRelease.Add(MakeKeyInput(VK_RMENU, KEYEVENTF_KEYUP));

        if (preRelease.Count > 0)
        {
            SendInput((uint)preRelease.Count, preRelease.ToArray(), cbSize);
            Thread.Sleep(8);
        }

        var ctrlDown = new[] { MakeKeyInput(VK_LCONTROL, 0) };
        SendInput(1, ctrlDown, cbSize);
        Thread.Sleep(10);

        var keyTap = new[]
        {
            MakeKeyInput(keyVk, 0),
            MakeKeyInput(keyVk, KEYEVENTF_KEYUP)
        };
        SendInput(2, keyTap, cbSize);
        Thread.Sleep(10);

        var ctrlUp = new[] { MakeKeyInput(VK_LCONTROL, KEYEVENTF_KEYUP) };
        SendInput(1, ctrlUp, cbSize);
    }

    /// <summary>
    /// Sends an arbitrary key combination (translating macOS PopClip modifiers such as "command" -> Ctrl, "option" -> Alt)
    /// using hardware scan codes and SelfInputExtraInfo tagging so Selector's own hook ignores it.
    /// </summary>
    public static bool SendKeyCombo(string keyName, IReadOnlyList<string> modifiers)
    {
        if (!TryResolveVirtualKey(keyName, out var keyVk))
        {
            return false;
        }

        var wantCtrl = false;
        var wantShift = false;
        var wantAlt = false;

        foreach (var raw in modifiers)
        {
            var m = (raw ?? string.Empty).Trim().ToLowerInvariant();
            switch (m)
            {
                case "command" or "cmd" or "meta" or "super" or "control" or "ctrl":
                    wantCtrl = true;
                    break;
                case "shift":
                    wantShift = true;
                    break;
                case "option" or "opt" or "alt":
                    wantAlt = true;
                    break;
            }
        }

        var cbSize = Marshal.SizeOf<INPUT>();
        var preRelease = new List<INPUT>();
        if (!wantCtrl && IsKeyDown(VK_LCONTROL)) preRelease.Add(MakeKeyInput(VK_LCONTROL, KEYEVENTF_KEYUP));
        if (!wantCtrl && IsKeyDown(VK_RCONTROL)) preRelease.Add(MakeKeyInput(VK_RCONTROL, KEYEVENTF_KEYUP));
        if (!wantShift && IsKeyDown(VK_LSHIFT)) preRelease.Add(MakeKeyInput(VK_LSHIFT, KEYEVENTF_KEYUP));
        if (!wantShift && IsKeyDown(VK_RSHIFT)) preRelease.Add(MakeKeyInput(VK_RSHIFT, KEYEVENTF_KEYUP));
        if (!wantAlt && IsKeyDown(VK_LMENU)) preRelease.Add(MakeKeyInput(VK_LMENU, KEYEVENTF_KEYUP));
        if (!wantAlt && IsKeyDown(VK_RMENU)) preRelease.Add(MakeKeyInput(VK_RMENU, KEYEVENTF_KEYUP));

        if (preRelease.Count > 0)
        {
            SendInput((uint)preRelease.Count, preRelease.ToArray(), cbSize);
            Thread.Sleep(8);
        }

        var modDown = new List<INPUT>(3);
        if (wantCtrl) modDown.Add(MakeKeyInput(VK_LCONTROL, 0));
        if (wantShift) modDown.Add(MakeKeyInput(VK_LSHIFT, 0));
        if (wantAlt) modDown.Add(MakeKeyInput(VK_LMENU, 0));

        if (modDown.Count > 0)
        {
            SendInput((uint)modDown.Count, modDown.ToArray(), cbSize);
            Thread.Sleep(10);
        }

        var keyTap = new[]
        {
            MakeKeyInput(keyVk, 0),
            MakeKeyInput(keyVk, KEYEVENTF_KEYUP)
        };
        SendInput(2, keyTap, cbSize);
        Thread.Sleep(10);

        if (modDown.Count > 0)
        {
            var modUp = new List<INPUT>(3);
            if (wantAlt) modUp.Add(MakeKeyInput(VK_LMENU, KEYEVENTF_KEYUP));
            if (wantShift) modUp.Add(MakeKeyInput(VK_LSHIFT, KEYEVENTF_KEYUP));
            if (wantCtrl) modUp.Add(MakeKeyInput(VK_LCONTROL, KEYEVENTF_KEYUP));
            SendInput((uint)modUp.Count, modUp.ToArray(), cbSize);
        }

        return true;
    }

    private static bool TryResolveVirtualKey(string keyName, out ushort vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(keyName)) return false;

        var trimmed = keyName.Trim();
        if (trimmed.Length == 1)
        {
            var ch = char.ToUpperInvariant(trimmed[0]);
            if (ch is >= 'A' and <= 'Z') { vk = ch; return true; }
            if (ch is >= '0' and <= '9') { vk = ch; return true; }
            vk = ch switch
            {
                '-' => 0xBD,
                '=' => 0xBB,
                '[' => 0xDB,
                ']' => 0xDD,
                '\\' => 0xDC,
                ';' => 0xBA,
                '\'' => 0xDE,
                ',' => 0xBC,
                '.' => 0xBE,
                '/' => 0xBF,
                '`' => 0xC0,
                ' ' => 0x20,
                _ => (ushort)0
            };
            return vk != 0;
        }

        var lower = trimmed.ToLowerInvariant();
        if (lower.StartsWith('f') && int.TryParse(lower[1..], out var fNum) && fNum is >= 1 and <= 24)
        {
            vk = (ushort)(0x70 + fNum - 1);
            return true;
        }

        vk = lower switch
        {
            "return" or "enter" => 0x0D,
            "tab" => 0x09,
            "space" => 0x20,
            "backspace" or "back" => 0x08,
            "delete" or "del" or "forwarddelete" => 0x2E,
            "escape" or "esc" => 0x1B,
            "left" or "arrowleft" => 0x25,
            "up" or "arrowup" => 0x26,
            "right" or "arrowright" => 0x27,
            "down" or "arrowdown" => 0x28,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "insert" or "ins" => 0x2D,
            _ => (ushort)0
        };
        return vk != 0;
    }

    private static INPUT MakeKeyInput(ushort vk, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC),
                dwFlags = flags,
                dwExtraInfo = SelfInputExtraInfo
            }
        }
    };
}
