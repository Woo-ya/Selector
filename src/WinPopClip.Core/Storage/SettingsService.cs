using System.Text.Json;
using WinPopClip.Core.Models;

namespace WinPopClip.Core.Storage;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _settingsFilePath;

    public AppSettings Settings { get; private set; }
    public event EventHandler? SettingsChanged;

    public SettingsService(string? settingsFilePath = null)
    {
        _settingsFilePath = settingsFilePath ?? AppPaths.GetSettingsFilePath();
        Settings = Load();
    }

    public string SettingsFilePath => _settingsFilePath;

    public AppSettings Load()
    {
        try
        {
            var sourcePath = _settingsFilePath;
            if (!File.Exists(sourcePath) &&
                sourcePath.Equals(AppPaths.GetSettingsFilePath(), StringComparison.OrdinalIgnoreCase))
            {
                var legacyPath = AppPaths.GetSettingsFilePath(AppPaths.LegacyDataDirectory);
                if (File.Exists(legacyPath))
                {
                    sourcePath = legacyPath;
                }
            }

            if (File.Exists(sourcePath))
            {
                var json = File.ReadAllText(sourcePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    var migrated = false;
                    if (loaded.SettingsSchemaVersion < AppSettings.CurrentSchemaVersion)
                    {
                        // v2: disable automatic Ctrl+C clipboard fallback by default so selecting text never triggers copy
                        loaded.EnableClipboardFallback = false;
                        loaded.SettingsSchemaVersion = AppSettings.CurrentSchemaVersion;
                        migrated = true;
                    }

                    loaded.ToolbarCornerRadius = ThemeResolver.ClampCornerRadius(loaded.ToolbarCornerRadius);
                    loaded.ToolbarScale = ThemeResolver.ClampToolbarScale(loaded.ToolbarScale);
                    loaded.ToolbarCustomWidth = ThemeResolver.ClampCustomWidth(loaded.ToolbarCustomWidth);
                    loaded.ToolbarCustomHeight = ThemeResolver.ClampCustomHeight(loaded.ToolbarCustomHeight);
                    loaded.ToolbarPopupDurationMs = ThemeResolver.ResolvePopupDurationMs(loaded.ToolbarPopupSpeed, loaded.ToolbarPopupDurationMs);
                    Settings = loaded;
                    if (migrated)
                    {
                        Save();
                    }
                    return loaded;
                }
            }
        }
        catch
        {
            // Fall back to default settings if corrupted
        }

        Settings = new AppSettings { SettingsSchemaVersion = AppSettings.CurrentSchemaVersion };
        return Settings;
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(Settings, JsonOptions);
        File.WriteAllText(_settingsFilePath, json);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
