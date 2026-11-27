using System.IO;
using Microsoft.Win32;

namespace WinPopClip.App.Services;

/// <summary>
/// Manages per-user Windows login autostart via HKCU\Software\Microsoft\Windows\CurrentVersion\Run
/// (no administrator privileges required). Launches silently into the system tray with --minimized.
/// </summary>
public static class StartupService
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Selector";

    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            var raw = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(raw);
        }
        catch
        {
            return false;
        }
    }

    public static (bool Success, string? ErrorMessage) SetStartupEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);
            if (key is null)
            {
                return (false, "无法打开当前用户的 Windows 启动项注册表键。");
            }

            if (!enabled)
            {
                if (key.GetValue(ValueName) is not null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
                return (true, null);
            }

            var command = ResolveLaunchCommand();
            if (string.IsNullOrWhiteSpace(command))
            {
                return (false, "无法定位 Selector 可执行文件路径，请运行打包后的 Selector.exe 再开启开机自启。");
            }

            key.SetValue(ValueName, command, RegistryValueKind.String);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string? ResolveLaunchCommand()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            return null;
        }

        // If running via `dotnet run`, prefer Selector.exe next to the entry assembly if present
        var fileName = Path.GetFileName(processPath);
        if (fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, "Selector.exe");
            if (File.Exists(candidate))
            {
                processPath = candidate;
            }
            else
            {
                return null;
            }
        }

        return $"\"{processPath}\" --minimized";
    }
}
