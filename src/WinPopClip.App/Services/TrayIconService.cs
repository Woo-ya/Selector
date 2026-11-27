using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WinPopClip.App.Native;
using WinPopClip.App.Views;
using WinPopClip.Core.Models;
using WinPopClip.Core.Storage;

namespace WinPopClip.App.Services;

/// <summary>
/// Pure Win32 Shell_NotifyIconW system tray service (zero WinForms / System.Drawing overhead)
/// paired with Selector's themed WPF TrayContextMenuWindow.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint TrayIconId = 1;
    private const int WmTrayIcon = Win32Interop.WM_APP + 1;

    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService;
    private readonly TrayContextMenuWindow _contextMenu;
    private readonly HwndSource _messageWindow;
    private readonly uint _taskbarCreatedMsg;

    private IntPtr _hIcon;
    private bool _ownsIcon;
    private bool _iconAdded;
    private bool _disposed;

    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? ReloadPluginsRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService(SettingsService settingsService, ThemeService themeService)
    {
        _settingsService = settingsService;
        _themeService = themeService;

        _contextMenu = new TrayContextMenuWindow(_settingsService, _themeService);
        _contextMenu.OpenSettingsRequested += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
        _contextMenu.ReloadPluginsRequested += (_, _) => ReloadPluginsRequested?.Invoke(this, EventArgs.Empty);
        _contextMenu.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _taskbarCreatedMsg = Win32Interop.RegisterWindowMessageW("TaskbarCreated");

        var parameters = new HwndSourceParameters("SelectorTrayMessageWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0
        };
        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WndProc);

        (_hIcon, _ownsIcon) = LoadAppIconHandle();
        AddOrUpdateTrayIcon(add: true);

        _settingsService.SettingsChanged += OnStateChanged;
        _themeService.ThemeChanged += OnThemeChanged;
        SyncState();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmTrayIcon)
        {
            var mouseMsg = lParam.ToInt32() & 0xFFFF;
            if (mouseMsg == Win32Interop.WM_RBUTTONUP)
            {
                if (Win32Interop.GetCursorPos(out var cursor))
                {
                    Application.Current?.Dispatcher.InvokeAsync(() =>
                        _contextMenu.ShowNearCursor(cursor.X, cursor.Y));
                }
                handled = true;
            }
            else if (mouseMsg == Win32Interop.WM_LBUTTONDBLCLK)
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                    OpenSettingsRequested?.Invoke(this, EventArgs.Empty));
                handled = true;
            }
        }
        else if (_taskbarCreatedMsg != 0 && msg == (int)_taskbarCreatedMsg)
        {
            // Automatically restore tray icon if Explorer restarts
            AddOrUpdateTrayIcon(add: true);
        }

        return IntPtr.Zero;
    }

    private void OnStateChanged(object? sender, EventArgs e) => SyncState();

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => SyncState();

    public void SyncState()
    {
        if (_disposed) return;
        AddOrUpdateTrayIcon(add: !_iconAdded);
        Application.Current?.Dispatcher.InvokeAsync(() => _contextMenu.SyncState());
    }

    public void ShowBalloon(string title, string text)
    {
        if (_disposed || !_iconAdded) return;

        var data = CreateBaseData();
        data.uFlags = Win32Interop.NIF_INFO;
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = Win32Interop.NIIF_INFO;
        Win32Interop.Shell_NotifyIconW(Win32Interop.NIM_MODIFY, ref data);
    }

    private void AddOrUpdateTrayIcon(bool add)
    {
        if (_disposed || _messageWindow.Handle == IntPtr.Zero) return;

        var state = _settingsService.Settings.Enabled ? "运行中" : "已暂停";
        var themeText = ThemeResolver.GetDisplayName(_themeService.CurrentPreference);
        var tip = Truncate($"Selector ({state} · {themeText})", 127);

        var data = CreateBaseData();
        data.uFlags = Win32Interop.NIF_MESSAGE | Win32Interop.NIF_ICON | Win32Interop.NIF_TIP;
        data.uCallbackMessage = WmTrayIcon;
        data.hIcon = _hIcon;
        data.szTip = tip;

        var ok = Win32Interop.Shell_NotifyIconW(add ? Win32Interop.NIM_ADD : Win32Interop.NIM_MODIFY, ref data);
        if (add && ok)
        {
            _iconAdded = true;
        }
    }

    private Win32Interop.NOTIFYICONDATAW CreateBaseData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<Win32Interop.NOTIFYICONDATAW>(),
        hWnd = _messageWindow.Handle,
        uID = TrayIconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private static (IntPtr Handle, bool OwnsHandle) LoadAppIconHandle()
    {
        // 1. Load Assets\selector.ico from disk if present next to exe
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "selector.ico");
            if (File.Exists(iconPath))
            {
                var h = Win32Interop.LoadImageW(IntPtr.Zero, iconPath, Win32Interop.IMAGE_ICON, 32, 32, Win32Interop.LR_LOADFROMFILE);
                if (h != IntPtr.Zero) return (h, true);
            }
        }
        catch { }

        // 2. Extract embedded Win32 application icon directly from Selector.exe (works even when single-file exe is moved alone)
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                var count = Win32Interop.ExtractIconExW(exePath, 0, out var hLarge, out var hSmall, 1);
                if (count > 0)
                {
                    if (hSmall != IntPtr.Zero)
                    {
                        if (hLarge != IntPtr.Zero) Win32Interop.DestroyIcon(hLarge);
                        return (hSmall, true);
                    }
                    if (hLarge != IntPtr.Zero) return (hLarge, true);
                }
            }
        }
        catch { }

        // 3. Fallback to standard Windows application icon
        return (Win32Interop.LoadIconW(IntPtr.Zero, Win32Interop.IDI_APPLICATION), false);
    }

    private static string Truncate(string s, int maxLen) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= maxLen ? s : s[..maxLen]);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _settingsService.SettingsChanged -= OnStateChanged;
        _themeService.ThemeChanged -= OnThemeChanged;

        if (_iconAdded && _messageWindow.Handle != IntPtr.Zero)
        {
            var data = CreateBaseData();
            Win32Interop.Shell_NotifyIconW(Win32Interop.NIM_DELETE, ref data);
            _iconAdded = false;
        }

        if (_ownsIcon && _hIcon != IntPtr.Zero)
        {
            Win32Interop.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        _messageWindow.RemoveHook(WndProc);
        _messageWindow.Dispose();
        _contextMenu.ForceClose();
    }
}
