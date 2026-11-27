namespace WinPopClip.Core.Models;

/// <summary>
/// Resolves user theme, material, corner radius, and size preferences cleanly.
/// </summary>
public static class ThemeResolver
{
    public const double MinToolbarScale = 0.75;
    public const double MaxToolbarScale = 1.50;
    public const double MinCustomWidth = 220;
    public const double MaxCustomWidth = 960;
    public const double MinCustomHeight = 34;
    public const double MaxCustomHeight = 128;
    public const int MinPopupDurationMs = 0;
    public const int MaxPopupDurationMs = 360;
    public const int InstantPopupDurationMs = 0;
    public const int FastPopupDurationMs = 110;
    public const int StandardPopupDurationMs = 190;
    public const int SlowPopupDurationMs = 300;

    public static ToolbarTheme ResolveEffectiveTheme(ToolbarTheme preference, bool systemPrefersDark)
    {
        return preference switch
        {
            ToolbarTheme.Dark => ToolbarTheme.Dark,
            ToolbarTheme.Light => ToolbarTheme.Light,
            _ => systemPrefersDark ? ToolbarTheme.Dark : ToolbarTheme.Light
        };
    }

    public static string GetDisplayName(ToolbarTheme theme) => theme switch
    {
        ToolbarTheme.Light => "浅色模式",
        ToolbarTheme.Dark => "深色模式",
        _ => "跟随系统"
    };

    public static string GetMaterialDisplayName(ToolbarMaterial material) => material switch
    {
        ToolbarMaterial.Solid => "实体",
        ToolbarMaterial.LiquidGlass => "液态玻璃",
        _ => "亚克力"
    };

    public static string GetSizePresetDisplayName(ToolbarSizePreset preset) => preset switch
    {
        ToolbarSizePreset.Compact => "紧凑",
        ToolbarSizePreset.Standard => "标准",
        ToolbarSizePreset.Large => "宽敞",
        ToolbarSizePreset.Custom => "自定义",
        _ => "自适应"
    };

    public static double ClampCornerRadius(double radius) => Math.Clamp(Math.Round(radius), 4, 28);
    public static double ClampToolbarScale(double scale) =>
        double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0
            ? 1.0
            : Math.Clamp(Math.Round(scale, 2), MinToolbarScale, MaxToolbarScale);
    public static double ClampCustomWidth(double width) => Math.Clamp(Math.Round(width), MinCustomWidth, MaxCustomWidth);
    public static double ClampCustomHeight(double height) => Math.Clamp(Math.Round(height), MinCustomHeight, MaxCustomHeight);

    public static int ClampPopupDurationMs(int durationMs) => Math.Clamp(durationMs, MinPopupDurationMs, MaxPopupDurationMs);

    public static int ResolvePopupDurationMs(ToolbarPopupSpeed speed, int customDurationMs) => speed switch
    {
        ToolbarPopupSpeed.Instant => InstantPopupDurationMs,
        ToolbarPopupSpeed.Fast => FastPopupDurationMs,
        ToolbarPopupSpeed.Standard => StandardPopupDurationMs,
        ToolbarPopupSpeed.Slow => SlowPopupDurationMs,
        _ => ClampPopupDurationMs(customDurationMs)
    };

    public static ToolbarPopupSpeed ResolvePopupSpeedPreset(int durationMs)
    {
        var clamped = ClampPopupDurationMs(durationMs);
        return clamped switch
        {
            InstantPopupDurationMs => ToolbarPopupSpeed.Instant,
            FastPopupDurationMs => ToolbarPopupSpeed.Fast,
            StandardPopupDurationMs => ToolbarPopupSpeed.Standard,
            SlowPopupDurationMs => ToolbarPopupSpeed.Slow,
            _ => ToolbarPopupSpeed.Custom
        };
    }

    public static string GetPopupSpeedDisplayName(ToolbarPopupSpeed speed) => speed switch
    {
        ToolbarPopupSpeed.Instant => "无动画",
        ToolbarPopupSpeed.Fast => "极速",
        ToolbarPopupSpeed.Standard => "标准",
        ToolbarPopupSpeed.Slow => "柔和",
        _ => "自定义"
    };
}
