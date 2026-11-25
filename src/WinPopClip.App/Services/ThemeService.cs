using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using WinPopClip.App.Native;
using WinPopClip.Core.Models;
using WinPopClip.Core.Storage;

namespace WinPopClip.App.Services;

public sealed record ThemeChangedEventArgs(ToolbarTheme Preference, ToolbarTheme EffectiveTheme)
{
    public bool IsDark => EffectiveTheme == ToolbarTheme.Dark;
}

/// <summary>
/// Centralized Selector theme & appearance service aligned with DropShelf's warm-paper (Light)
/// and refined-charcoal (Dark) surface hierarchy, paired with Selector's signature Indigo accent.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private const string PersonalizeRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private readonly SettingsService _settingsService;
    private readonly DiagnosticsLog? _diagnostics;

    public ToolbarTheme CurrentPreference => _settingsService.Settings.Theme;
    public ToolbarTheme EffectiveTheme { get; private set; }
    public bool IsDarkEffective => EffectiveTheme == ToolbarTheme.Dark;
    public ToolbarMaterial CurrentMaterial => _settingsService.Settings.ToolbarMaterial;
    public double CurrentCornerRadius => ThemeResolver.ClampCornerRadius(_settingsService.Settings.ToolbarCornerRadius);
    public double CurrentToolbarScale => ThemeResolver.ClampToolbarScale(_settingsService.Settings.ToolbarScale);
    public ToolbarSizePreset CurrentSizePreset => _settingsService.Settings.ToolbarSizePreset;
    public double CurrentCustomWidth => ThemeResolver.ClampCustomWidth(_settingsService.Settings.ToolbarCustomWidth);
    public double CurrentCustomHeight => ThemeResolver.ClampCustomHeight(_settingsService.Settings.ToolbarCustomHeight);

    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
    public event EventHandler? ToolbarAppearanceChanged;

    public ThemeService(SettingsService settingsService, DiagnosticsLog? diagnostics = null)
    {
        _settingsService = settingsService;
        _diagnostics = diagnostics;
        EffectiveTheme = ResolveCurrentEffectiveTheme();

        SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;
        ApplyApplicationResources(EffectiveTheme == ToolbarTheme.Dark);
    }

    public void SetTheme(ToolbarTheme preference)
    {
        var previousPref = _settingsService.Settings.Theme;
        var previousEffective = EffectiveTheme;

        _settingsService.Settings.Theme = preference;
        EffectiveTheme = ResolveCurrentEffectiveTheme();
        _settingsService.Save();

        ApplyToApplicationAndWindows();

        if (previousPref != preference || previousEffective != EffectiveTheme)
        {
            _diagnostics?.Write($"主题切换：{ThemeResolver.GetDisplayName(preference)}（生效：{ThemeResolver.GetDisplayName(EffectiveTheme)}）");
        }

        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(preference, EffectiveTheme));
        ToolbarAppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetToolbarMaterial(ToolbarMaterial material)
    {
        if (_settingsService.Settings.ToolbarMaterial == material) return;
        _settingsService.Settings.ToolbarMaterial = material;
        _settingsService.Save();
        _diagnostics?.Write($"浮窗材质切换：{ThemeResolver.GetMaterialDisplayName(material)}");
        ToolbarAppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetToolbarCornerRadius(double radius)
    {
        var clamped = ThemeResolver.ClampCornerRadius(radius);
        if (Math.Abs(_settingsService.Settings.ToolbarCornerRadius - clamped) < 0.01) return;
        _settingsService.Settings.ToolbarCornerRadius = clamped;
        _settingsService.Save();
        ToolbarAppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetToolbarScale(double scale)
    {
        var clamped = ThemeResolver.ClampToolbarScale(scale);
        if (Math.Abs(_settingsService.Settings.ToolbarScale - clamped) < 0.005) return;
        _settingsService.Settings.ToolbarScale = clamped;
        _settingsService.Save();
        ToolbarAppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetToolbarSizePreset(ToolbarSizePreset preset)
    {
        if (_settingsService.Settings.ToolbarSizePreset == preset) return;
        _settingsService.Settings.ToolbarSizePreset = preset;
        _settingsService.Save();
        _diagnostics?.Write($"浮窗尺寸切换：{ThemeResolver.GetSizePresetDisplayName(preset)}");
        ToolbarAppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetToolbarCustomSize(double width, double height)
    {
        var clampedW = ThemeResolver.ClampCustomWidth(width);
        var clampedH = ThemeResolver.ClampCustomHeight(height);
        if (Math.Abs(_settingsService.Settings.ToolbarCustomWidth - clampedW) < 0.01 &&
            Math.Abs(_settingsService.Settings.ToolbarCustomHeight - clampedH) < 0.01)
        {
            return;
        }

        _settingsService.Settings.ToolbarCustomWidth = clampedW;
        _settingsService.Settings.ToolbarCustomHeight = clampedH;
        _settingsService.Save();
        ToolbarAppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyToolbarAppearanceChanged()
    {
        ToolbarAppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyWindowTheme(Window window)
    {
        if (window is null) return;

        void ApplyNativeFrame()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var useDark = IsDarkEffective ? 1 : 0;
            if (Win32Interop.DwmSetWindowAttribute(hwnd, Win32Interop.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
            {
                _ = Win32Interop.DwmSetWindowAttribute(hwnd, Win32Interop.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDark, sizeof(int));
            }

            var cornerPref = Win32Interop.DWMWCP_ROUND;
            _ = Win32Interop.DwmSetWindowAttribute(hwnd, Win32Interop.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
        }

        if (window.IsLoaded)
        {
            ApplyNativeFrame();
        }
        else
        {
            window.SourceInitialized += (_, _) => ApplyNativeFrame();
        }
    }

    public static bool QuerySystemPrefersDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath);
            if (key?.GetValue(AppsUseLightThemeValue) is int value)
            {
                return value == 0;
            }
        }
        catch { }
        return false;
    }

    private ToolbarTheme ResolveCurrentEffectiveTheme() =>
        ThemeResolver.ResolveEffectiveTheme(_settingsService.Settings.Theme, QuerySystemPrefersDarkMode());

    private void OnSystemUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General &&
            e.Category != UserPreferenceCategory.Color &&
            e.Category != UserPreferenceCategory.VisualStyle)
        {
            return;
        }

        if (CurrentPreference != ToolbarTheme.System)
        {
            return;
        }

        var nextEffective = ResolveCurrentEffectiveTheme();
        if (nextEffective == EffectiveTheme)
        {
            return;
        }

        EffectiveTheme = nextEffective;
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            ApplyToApplicationAndWindows();
            _diagnostics?.Write($"检测到系统主题变更，已自动切换为：{ThemeResolver.GetDisplayName(EffectiveTheme)}");
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(CurrentPreference, EffectiveTheme));
            ToolbarAppearanceChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void ApplyToApplicationAndWindows()
    {
        ApplyApplicationResources(IsDarkEffective);

        if (Application.Current is null) return;
        foreach (Window win in Application.Current.Windows)
        {
            if (win.WindowStyle != WindowStyle.None)
            {
                ApplyWindowTheme(win);
            }
        }
    }

    private static void ApplyApplicationResources(bool isDark)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        void SetBrush(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }

        void SetArgb(string key, byte a, byte r, byte g, byte b) => SetBrush(key, Color.FromArgb(a, r, g, b));

        // Selector's signature Indigo accent adapted for Light/Dark contrast (aligned with DropShelf's accent model)
        var accent = isDark ? Color.FromRgb(129, 140, 248) : Color.FromRgb(79, 70, 229);
        var accentHover = Color.FromArgb(0xE6, accent.R, accent.G, accent.B);
        var accentPressed = Color.FromArgb(0xCC, accent.R, accent.G, accent.B);
        var accentText = isDark ? Color.FromRgb(165, 180, 252) : Color.FromRgb(55, 48, 163);
        var accentSubtle = Color.FromArgb(isDark ? (byte)0x2E : (byte)0x1F, accent.R, accent.G, accent.B);
        var accentFaint = Color.FromArgb(isDark ? (byte)0x18 : (byte)0x10, accent.R, accent.G, accent.B);
        var textOnAccent = isDark ? Color.FromArgb(0xE8, 0x0F, 0x11, 0x17) : Colors.White;

        SetBrush("AccentBrush", accent);
        SetBrush("AccentHoverBrush", accentHover);
        SetBrush("AccentPressedBrush", accentPressed);
        SetBrush("AccentTextBrush", accentText);
        SetBrush("AccentSubtleBrush", accentSubtle);
        SetBrush("AccentFaintBrush", accentFaint);
        SetBrush("TextOnAccentBrush", textOnAccent);

        // DropShelf-style SheetBrush: subtle radial accent wash on the settings sheet card
        var sheetGlow = TintOverShade(accent, 0.09, isDark ? 0.14 : 0.0);
        var stackEdge = TintOverShade(accent, 0.045, isDark ? 0.14 : 0.0);
        var sheetBorder = Color.FromArgb(isDark ? (byte)0x38 : (byte)0x29, accent.R, accent.G, accent.B);
        SetBrush("StackEdgeBrush", stackEdge);
        SetBrush("SheetBorderBrush", sheetBorder);

        var sheetBrush = new RadialGradientBrush
        {
            Center = new Point(0, 0),
            GradientOrigin = new Point(0, 0),
            RadiusX = 1.1,
            RadiusY = 0.7,
            GradientStops = new GradientStopCollection
            {
                new(sheetGlow, 0.0),
                new(stackEdge, 0.72)
            }
        };
        sheetBrush.Freeze();
        resources["SheetBrush"] = sheetBrush;

        if (isDark)
        {
            // DropShelf Dark palette: calm neutral charcoal (#202020) with luminous translucent cards
            SetArgb("WindowBackgroundBrush", 0xFF, 0x20, 0x20, 0x20);
            SetArgb("AppBgBrush", 0xFF, 0x20, 0x20, 0x20);
            SetArgb("LayerBrush", 0x4C, 0x3A, 0x3A, 0x3A);
            SetArgb("SurfaceBrush", 0xFF, 0x27, 0x27, 0x27);
            SetArgb("SurfaceAltBrush", 0x12, 0xFF, 0xFF, 0xFF);
            SetArgb("CardBrush", 0x0D, 0xFF, 0xFF, 0xFF);
            SetArgb("CardHoverBrush", 0x14, 0xFF, 0xFF, 0xFF);
            SetArgb("CardBorderBrush", 0x33, 0x00, 0x00, 0x00);
            SetArgb("ControlBrush", 0x0F, 0xFF, 0xFF, 0xFF);
            SetArgb("ControlHoverBrush", 0x15, 0xFF, 0xFF, 0xFF);
            SetArgb("ControlPressedBrush", 0x08, 0xFF, 0xFF, 0xFF);
            SetArgb("ControlBorderBrush", 0x17, 0xFF, 0xFF, 0xFF);
            SetArgb("ControlStrongBorderBrush", 0x8B, 0xFF, 0xFF, 0xFF);
            SetArgb("ControlAltBrush", 0x1F, 0x00, 0x00, 0x00);
            SetArgb("SubtleHoverBrush", 0x0F, 0xFF, 0xFF, 0xFF);
            SetArgb("SubtlePressedBrush", 0x0A, 0xFF, 0xFF, 0xFF);
            SetArgb("SubtleSelectedBrush", 0x14, 0xFF, 0xFF, 0xFF);
            SetArgb("DividerBrush", 0x15, 0xFF, 0xFF, 0xFF);
            SetArgb("FlyoutBrush", 0xFF, 0x2C, 0x2C, 0x2C);
            SetArgb("FlyoutBorderBrush", 0x26, 0xFF, 0xFF, 0xFF);
            SetArgb("TextPrimaryBrush", 0xFF, 0xFF, 0xFF, 0xFF);
            SetArgb("TextSecondaryBrush", 0xC5, 0xFF, 0xFF, 0xFF);
            SetArgb("TextTertiaryBrush", 0x87, 0xFF, 0xFF, 0xFF);
            SetArgb("TextDisabledBrush", 0x5D, 0xFF, 0xFF, 0xFF);
            SetArgb("ScrollThumbBrush", 0x8B, 0xFF, 0xFF, 0xFF);
            SetArgb("SuccessBrush", 0xFF, 0x6C, 0xCB, 0x5F);
            SetArgb("CautionBrush", 0xFF, 0xFC, 0xE1, 0x00);
            SetArgb("CriticalBrush", 0xFF, 0xFF, 0x99, 0xA4);
            SetArgb("LogBgBrush", 0xFF, 0x17, 0x17, 0x17);
            SetArgb("LogFgBrush", 0xE0, 0xFF, 0xFF, 0xFF);
            SetArgb("StatusOkBrush", 0xFF, 0x6C, 0xCB, 0x5F);
            SetArgb("StatusErrBrush", 0xFF, 0xFF, 0x99, 0xA4);
        }
        else
        {
            // DropShelf Light palette: signature warm porcelain paper (#F9F7F1) with translucent white cards
            SetArgb("WindowBackgroundBrush", 0xFF, 0xF9, 0xF7, 0xF1);
            SetArgb("AppBgBrush", 0xFF, 0xF9, 0xF7, 0xF1);
            SetArgb("LayerBrush", 0xCC, 0xFF, 0xFD, 0xF8);
            SetArgb("SurfaceBrush", 0xFF, 0xFD, 0xFC, 0xF8);
            SetArgb("SurfaceAltBrush", 0x08, 0x00, 0x00, 0x00);
            SetArgb("CardBrush", 0xB3, 0xFF, 0xFF, 0xFF);
            SetArgb("CardHoverBrush", 0xE6, 0xFF, 0xFF, 0xFF);
            SetArgb("CardBorderBrush", 0x14, 0x00, 0x00, 0x00);
            SetArgb("ControlBrush", 0xB3, 0xFF, 0xFF, 0xFF);
            SetArgb("ControlHoverBrush", 0x80, 0xF9, 0xF9, 0xF9);
            SetArgb("ControlPressedBrush", 0x4D, 0xF9, 0xF9, 0xF9);
            SetArgb("ControlBorderBrush", 0x1F, 0x00, 0x00, 0x00);
            SetArgb("ControlStrongBorderBrush", 0x72, 0x00, 0x00, 0x00);
            SetArgb("ControlAltBrush", 0x07, 0x00, 0x00, 0x00);
            SetArgb("SubtleHoverBrush", 0x09, 0x00, 0x00, 0x00);
            SetArgb("SubtlePressedBrush", 0x06, 0x00, 0x00, 0x00);
            SetArgb("SubtleSelectedBrush", 0x0D, 0x00, 0x00, 0x00);
            SetArgb("DividerBrush", 0x0F, 0x00, 0x00, 0x00);
            SetArgb("FlyoutBrush", 0xFF, 0xFD, 0xFC, 0xF8);
            SetArgb("FlyoutBorderBrush", 0x1A, 0x00, 0x00, 0x00);
            SetArgb("TextPrimaryBrush", 0xE4, 0x00, 0x00, 0x00);
            SetArgb("TextSecondaryBrush", 0x9E, 0x00, 0x00, 0x00);
            SetArgb("TextTertiaryBrush", 0x72, 0x00, 0x00, 0x00);
            SetArgb("TextDisabledBrush", 0x5C, 0x00, 0x00, 0x00);
            SetArgb("ScrollThumbBrush", 0x72, 0x00, 0x00, 0x00);
            SetArgb("SuccessBrush", 0xFF, 0x0F, 0x7B, 0x0F);
            SetArgb("CautionBrush", 0xFF, 0x9D, 0x5D, 0x00);
            SetArgb("CriticalBrush", 0xFF, 0xC4, 0x2B, 0x1C);
            SetArgb("LogBgBrush", 0xFF, 0x1E, 0x1F, 0x24);
            SetArgb("LogFgBrush", 0xEE, 0xF3, 0xF4, 0xF6);
            SetArgb("StatusOkBrush", 0xFF, 0x0F, 0x7B, 0x0F);
            SetArgb("StatusErrBrush", 0xFF, 0xC4, 0x2B, 0x1C);
        }

        // Legacy compatibility aliases used by sub-dialogs
        resources["BorderBrushTheme"] = resources["CardBorderBrush"];
        resources["InputBgBrush"] = resources["ControlBrush"];
        resources["ButtonBorderBrush"] = resources["ControlBorderBrush"];
        resources["TextMutedBrush"] = resources["TextSecondaryBrush"];
    }

    /// <summary>Matches DropShelf's tint(color, alpha, shade) formula for subtle settings sheet backdrop.</summary>
    private static Color TintOverShade(Color color, double alpha, double shade)
    {
        var total = 1.0 - (1.0 - shade) * (1.0 - alpha);
        var k = alpha / total;
        return Color.FromArgb(
            (byte)Math.Round(total * 255),
            (byte)Math.Round(color.R * k),
            (byte)Math.Round(color.G * k),
            (byte)Math.Round(color.B * k));
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged;
    }
}
