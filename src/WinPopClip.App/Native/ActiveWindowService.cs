using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WinForms = System.Windows.Forms;

namespace WinPopClip.App.Native;

public sealed record ActiveWindowInfo(
    IntPtr Handle,
    uint ProcessId,
    string ProcessName,
    string WindowTitle,
    bool IsFullscreen,
    bool IsPotentiallyElevated);

public sealed class ActiveWindowService
{
    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd"
    };

    public ActiveWindowInfo GetForegroundWindowInfo()
    {
        var hwnd = Win32Interop.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return new ActiveWindowInfo(IntPtr.Zero, 0, string.Empty, string.Empty, false, false);
        }

        _ = Win32Interop.GetWindowThreadProcessId(hwnd, out var pid);
        var processName = string.Empty;
        var isPotentiallyElevated = false;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            processName = proc.ProcessName;
            try
            {
                // Accessing MainModule throws Win32Exception(5) when target process is elevated (UIPI)
                _ = proc.MainModule;
            }
            catch
            {
                isPotentiallyElevated = true;
            }
        }
        catch
        {
            isPotentiallyElevated = true;
        }

        var titleBuilder = new StringBuilder(512);
        _ = Win32Interop.GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);
        var title = titleBuilder.ToString();

        var isFullscreen = IsTrueFullscreenWindow(hwnd, processName);
        return new ActiveWindowInfo(hwnd, pid, processName, title, isFullscreen, isPotentiallyElevated);
    }

    /// <summary>
    /// Accurately detects exclusive fullscreen apps/games without false-positiving on maximized
    /// Edge/Chrome/VS Code windows (even with auto-hide taskbar or secondary monitors).
    /// </summary>
    private static bool IsTrueFullscreenWindow(IntPtr hwnd, string processName)
    {
        if (processName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var classBuilder = new StringBuilder(128);
        _ = Win32Interop.GetClassName(hwnd, classBuilder, classBuilder.Capacity);
        if (ShellWindowClasses.Contains(classBuilder.ToString()))
        {
            return false;
        }

        // 1. OS-level D3D exclusive fullscreen / presentation mode check
        if (Win32Interop.SHQueryUserNotificationState(out var state) == 0 &&
            (state == Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN ||
             state == Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE))
        {
            return true;
        }

        // 2. Normal maximized windows (Edge, Chrome, Office, VS Code) are NOT exclusive fullscreen
        if (Win32Interop.IsZoomed(hwnd))
        {
            return false;
        }

        // 3. Standard captioned/resizable windows with title bar are NOT borderless fullscreen games
        var style = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_STYLE).ToInt64();
        var hasCaptionOrBorder = (style & Win32Interop.WS_CAPTION) == Win32Interop.WS_CAPTION ||
                                 (style & Win32Interop.WS_THICKFRAME) != 0;
        if (hasCaptionOrBorder)
        {
            return false;
        }

        // 4. Borderless window covering the entire monitor bounds -> treat as borderless fullscreen
        Win32Interop.RECT rect;
        if (Win32Interop.DwmGetWindowAttribute(
                hwnd,
                Win32Interop.DWMWA_EXTENDED_FRAME_BOUNDS,
                out rect,
                Marshal.SizeOf<Win32Interop.RECT>()) != 0)
        {
            if (!Win32Interop.GetWindowRect(hwnd, out rect))
            {
                return false;
            }
        }

        var screen = WinForms.Screen.FromHandle(hwnd);
        var bounds = screen.Bounds;
        return rect.Left <= bounds.Left &&
               rect.Top <= bounds.Top &&
               rect.Right >= bounds.Right &&
               rect.Bottom >= bounds.Bottom;
    }
}
