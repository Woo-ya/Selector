using System.Text.Json.Serialization;

namespace WinPopClip.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TriggerMode
{
    AutoOnSelection,
    RequireCtrlKey,
    RequireAltKey
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToolbarTheme
{
    System,
    Light,
    Dark
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToolbarDisplayMode
{
    IconAndText,
    IconOnly
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToolbarMaterial
{
    Acrylic,
    Solid,
    LiquidGlass
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToolbarSizePreset
{
    Auto,
    Compact,
    Standard,
    Large,
    Custom
}

public sealed class PluginStateOverride
{
    public bool Enabled { get; set; } = true;
    public int Order { get; set; }
    public string? CustomIcon { get; set; }
    public Dictionary<string, string> OptionValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>Defaults to 0 so legacy settings files without this field trigger one-time migration on load.</summary>
    public int SettingsSchemaVersion { get; set; }
    public bool Enabled { get; set; } = true;
    public TriggerMode TriggerMode { get; set; } = TriggerMode.AutoOnSelection;
    public bool EnableClipboardFallback { get; set; } = false;
    public bool EnableDoubleClickSelection { get; set; } = true;
    public bool DisableInFullscreen { get; set; } = true;
    public bool ShowGettingStarted { get; set; } = true;
    public int DragThresholdPixels { get; set; } = 8;
    public int SelectionDelayMs { get; set; } = 60;
    public ToolbarTheme Theme { get; set; } = ToolbarTheme.System;
    public ToolbarDisplayMode DisplayMode { get; set; } = ToolbarDisplayMode.IconAndText;
    public ToolbarMaterial ToolbarMaterial { get; set; } = ToolbarMaterial.Acrylic;
    public double ToolbarCornerRadius { get; set; } = 14;
    public double ToolbarScale { get; set; } = 1.0;
    public ToolbarSizePreset ToolbarSizePreset { get; set; } = ToolbarSizePreset.Auto;
    public double ToolbarCustomWidth { get; set; } = 460;
    public double ToolbarCustomHeight { get; set; } = 46;

    /// <summary>Backward-compatible convenience property synced with DisplayMode.</summary>
    [JsonIgnore]
    public bool ShowActionLabels
    {
        get => DisplayMode == ToolbarDisplayMode.IconAndText;
        set => DisplayMode = value ? ToolbarDisplayMode.IconAndText : ToolbarDisplayMode.IconOnly;
    }

    public int MaxVisibleActions { get; set; } = 10;

    /// <summary>Processes where the toolbar will never appear (without .exe).</summary>
    public List<string> ExcludedApps { get; set; } = new()
    {
        "mstsc",
        "KeePass",
        "1Password",
        "Bitwarden",
        "Taskmgr"
    };

    /// <summary>Processes where Ctrl+C fallback is disabled to avoid sending SIGINT.</summary>
    public List<string> ClipboardFallbackBlockedApps { get; set; } = new()
    {
        "WindowsTerminal",
        "cmd",
        "powershell",
        "pwsh",
        "ConEmu64",
        "mintty"
    };

    /// <summary>Per-plugin enabled state, order, custom icon, and user-configured option values.</summary>
    public Dictionary<string, PluginStateOverride> PluginStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>User-defined custom plugins created in the Settings UI or imported from PopClip snippets.</summary>
    public List<PluginManifest> CustomPlugins { get; set; } = new();
}
