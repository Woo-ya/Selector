using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinPopClip.App.Native;
using WinPopClip.App.Services;
using WinPopClip.Core.Models;
using WinPopClip.Core.Storage;

namespace WinPopClip.App.Views;

/// <summary>
/// DropShelf-inspired themed WPF system tray context menu GUI with live theme sync,
/// master listening toggle, quick navigation, and click-outside auto-dismiss.
/// </summary>
public partial class TrayContextMenuWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService;
    private bool _suppressEvents;
    private bool _allowClose;
    private bool _isOpening;

    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? ReloadPluginsRequested;
    public event EventHandler? ExitRequested;

    public TrayContextMenuWindow(SettingsService settingsService, ThemeService themeService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _themeService = themeService;

        MenuLogoHost.Content = VectorIconFactory.CreateBrandLogo(20);
        SyncState();
    }

    public void SyncState()
    {
        _suppressEvents = true;
        var enabled = _settingsService.Settings.Enabled;
        EnableToggle.IsChecked = enabled;
        StatusLabel.Text = enabled ? "运行中" : "已暂停";
        EnableSubText.Text = enabled ? "全局监听鼠标拖拽划词" : "划词监听已暂停，随时可开启";
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, enabled ? "SuccessBrush" : "CautionBrush");

        var pref = _themeService.CurrentPreference;
        var effectiveText = _themeService.IsDarkEffective ? "当前：深色" : "当前：浅色";
        ThemeEffectiveText.Text = pref == ToolbarTheme.System ? $"跟随系统（{effectiveText}）" : effectiveText;

        ApplyThemeButtonVisual(ThemeSystemBtn, ThemeSystemIcon, "symbol:contrast", pref == ToolbarTheme.System);
        ApplyThemeButtonVisual(ThemeLightBtn, ThemeLightIcon, "symbol:sun", pref == ToolbarTheme.Light);
        ApplyThemeButtonVisual(ThemeDarkBtn, ThemeDarkIcon, "symbol:moon", pref == ToolbarTheme.Dark);
        _suppressEvents = false;
    }

    public void ShowNearCursor(int screenX, int screenY)
    {
        _isOpening = true;
        try
        {
            SyncState();

            if (!IsVisible)
            {
                Opacity = 0;
                Show();
            }
            UpdateLayout();

            var dpiX = 1.0;
            var dpiY = 1.0;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is not null)
            {
                dpiX = source.CompositionTarget.TransformToDevice.M11;
                dpiY = source.CompositionTarget.TransformToDevice.M22;
            }

            var winW = ActualWidth * dpiX;
            var winH = ActualHeight * dpiY;
            var work = Win32Interop.GetWorkAreaForPoint(screenX, screenY);
            var margin = 6 * dpiX;

            // Anchor cleanly near the tray icon, preferring above the taskbar
            var leftPx = screenX - winW * 0.5;
            var topPx = screenY - winH;

            if (topPx < work.Top + margin)
            {
                topPx = screenY + margin;
            }

            leftPx = Math.Clamp(leftPx, work.Left + margin, Math.Max(work.Left + margin, work.Right - winW - margin));
            topPx = Math.Clamp(topPx, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - winH - margin));

            Left = leftPx / dpiX;
            Top = topPx / dpiY;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                Win32Interop.SetForegroundWindow(hwnd);
            }
            Activate();

            if (SelectorMotion.Enabled)
            {
                var dur = TimeSpan.FromMilliseconds(140);
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = SelectorMotion.StandardEase });
                MenuSlideTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, dur) { EasingFunction = SelectorMotion.StandardEase });
            }
            else
            {
                BeginAnimation(OpacityProperty, null);
                MenuSlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
                Opacity = 1;
                MenuSlideTransform.Y = 0;
            }
        }
        finally
        {
            _isOpening = false;
        }
    }

    public void HideMenu()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private void ApplyThemeButtonVisual(Button button, ContentControl iconHost, string symbolKey, bool isSelected)
    {
        if (isSelected)
        {
            button.SetResourceReference(BackgroundProperty, "AccentBrush");
            button.SetResourceReference(ForegroundProperty, "TextOnAccentBrush");
        }
        else
        {
            button.Background = Brushes.Transparent;
            button.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
        }

        var fg = TryFindResource(isSelected ? "TextOnAccentBrush" : "TextSecondaryBrush") as Brush ?? Brushes.Gray;
        var invFg = TryFindResource("SurfaceBrush") as Brush ?? Brushes.White;
        iconHost.Content = VectorIconFactory.CreateIcon(symbolKey, fg, invFg, size: 12.5);
    }

    private void OnEnableToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settingsService.Settings.Enabled = EnableToggle.IsChecked == true;
        _settingsService.Save();
        SyncState();
    }

    private void OnThemeSystemClick(object sender, RoutedEventArgs e)
    {
        _themeService.SetTheme(ToolbarTheme.System);
        SyncState();
    }

    private void OnThemeLightClick(object sender, RoutedEventArgs e)
    {
        _themeService.SetTheme(ToolbarTheme.Light);
        SyncState();
    }

    private void OnThemeDarkClick(object sender, RoutedEventArgs e)
    {
        _themeService.SetTheme(ToolbarTheme.Dark);
        SyncState();
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        HideMenu();
        OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnReloadPluginsClick(object sender, RoutedEventArgs e)
    {
        HideMenu();
        ReloadPluginsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        HideMenu();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideMenu();
            e.Handled = true;
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (!_isOpening)
        {
            HideMenu();
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
