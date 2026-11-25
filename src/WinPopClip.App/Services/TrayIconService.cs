using System.IO;
using System.Windows;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using WinPopClip.App.Native;
using WinPopClip.App.Views;
using WinPopClip.Core.Models;
using WinPopClip.Core.Storage;

namespace WinPopClip.App.Services;

/// <summary>
/// Manages the Windows system tray icon and its themed WPF context menu GUI.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService;
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly TrayContextMenuWindow _contextMenu;

    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? OpenAppearanceRequested;
    public event EventHandler? ReloadPluginsRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService(SettingsService settingsService, ThemeService themeService)
    {
        _settingsService = settingsService;
        _themeService = themeService;

        _contextMenu = new TrayContextMenuWindow(_settingsService, _themeService);
        _contextMenu.OpenSettingsRequested += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
        _contextMenu.OpenAppearanceRequested += (_, _) => OpenAppearanceRequested?.Invoke(this, EventArgs.Empty);
        _contextMenu.ReloadPluginsRequested += (_, _) => ReloadPluginsRequested?.Invoke(this, EventArgs.Empty);
        _contextMenu.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true
        };

        _notifyIcon.MouseUp += OnNotifyIconMouseUp;
        _notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                    OpenSettingsRequested?.Invoke(this, EventArgs.Empty));
            }
        };

        _settingsService.SettingsChanged += OnStateChanged;
        _themeService.ThemeChanged += OnThemeChanged;
        SyncState();
    }

    private void OnNotifyIconMouseUp(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button == WinForms.MouseButtons.Right)
        {
            var pt = Win32Interop.GetCursorPos(out var cursor)
                ? cursor
                : new Win32Interop.POINT { X = WinForms.Cursor.Position.X, Y = WinForms.Cursor.Position.Y };

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                _contextMenu.ShowNearCursor(pt.X, pt.Y);
            });
        }
    }

    private void OnStateChanged(object? sender, EventArgs e) => SyncState();

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => SyncState();

    public void SyncState()
    {
        UpdateTooltip();
        Application.Current?.Dispatcher.InvokeAsync(() => _contextMenu.SyncState());
    }

    public void ShowBalloon(string title, string text)
    {
        _notifyIcon.ShowBalloonTip(2500, title, text, WinForms.ToolTipIcon.Info);
    }

    private void UpdateTooltip()
    {
        var state = _settingsService.Settings.Enabled ? "运行中" : "已暂停";
        var themeText = ThemeResolver.GetDisplayName(_themeService.CurrentPreference);
        _notifyIcon.Text = $"Selector ({state} · {themeText})";
    }

    private static Drawing.Icon LoadAppIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "selector.ico");
            if (File.Exists(iconPath))
            {
                return new Drawing.Icon(iconPath, new Drawing.Size(32, 32));
            }
        }
        catch { }

        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/selector.ico", UriKind.Absolute));
            if (res?.Stream is not null)
            {
                using var stream = res.Stream;
                return new Drawing.Icon(stream, new Drawing.Size(32, 32));
            }
        }
        catch { }

        using var bmp = new Drawing.Bitmap(32, 32);
        using var g = Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Drawing.Color.Transparent);

        using var bgBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 79, 70, 229));
        g.FillEllipse(bgBrush, 2, 2, 28, 28);

        using var font = new Drawing.Font("Segoe UI", 14, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
        using var sf = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
        g.DrawString("S", font, Drawing.Brushes.White, new Drawing.RectangleF(1, 2, 30, 28), sf);

        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _settingsService.SettingsChanged -= OnStateChanged;
        _themeService.ThemeChanged -= OnThemeChanged;
        _notifyIcon.MouseUp -= OnNotifyIconMouseUp;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.ForceClose();
    }
}
