namespace WinPopClip.Core.Storage;

public static class AppPaths
{
    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Selector");

    public static string LegacyDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinPopClip");

    public static string GetSettingsFilePath(string? dataDirectory = null) =>
        Path.Combine(dataDirectory ?? DefaultDataDirectory, "settings.json");

    public static string GetUserPluginsDirectory(string? dataDirectory = null) =>
        Path.Combine(dataDirectory ?? DefaultDataDirectory, "plugins");

    public static string GetCustomIconsDirectory(string? dataDirectory = null) =>
        Path.Combine(dataDirectory ?? DefaultDataDirectory, "icons");
}
