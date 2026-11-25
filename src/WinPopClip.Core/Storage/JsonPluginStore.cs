using System.Text.Json;
using WinPopClip.Core.Models;
using WinPopClip.Core.Plugins;

namespace WinPopClip.Core.Storage;

/// <summary>
/// Loads declarative WinPopClip JSON manifests AND PopClip-compatible extensions
/// (*.popcliptxt, *.yaml, *.yml, *.js with #popclip header, and *.popclipext/Config.json) from plugin directories.
/// </summary>
public sealed class DirectoryJsonPluginProvider : IPluginProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly string[] PopClipSnippetExtensions = { ".popcliptxt", ".yaml", ".yml", ".js" };

    private readonly IReadOnlyList<string> _directories;

    public string ProviderName => "DirectoryPlugins";

    public DirectoryJsonPluginProvider(params string[] directories)
    {
        _directories = directories.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IEnumerable<PluginManifest> LoadPlugins()
    {
        var loaded = new List<PluginManifest>();

        foreach (var dir in _directories)
        {
            if (!Directory.Exists(dir)) continue;

            // 1. Direct *.json WinPopClip manifests
            foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            {
                TryAddJsonManifest(file, loaded);
            }

            // 2. PopClip snippet files (*.popcliptxt, *.yaml, *.yml, *.js)
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly))
            {
                var ext = Path.GetExtension(file);
                if (PopClipSnippetExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
                {
                    TryAddPopClipSnippetFile(file, loaded);
                }
            }

            // 3. Subfolders: plugins/<name>/plugin.json OR plugins/<name>.popclipext/Config.json
            foreach (var subDir in Directory.EnumerateDirectories(dir))
            {
                var manifestFile = Path.Combine(subDir, "plugin.json");
                if (File.Exists(manifestFile))
                {
                    TryAddJsonManifest(manifestFile, loaded);
                    continue;
                }

                var popClipConfig = Path.Combine(subDir, "Config.json");
                if (File.Exists(popClipConfig))
                {
                    TryAddPopClipExtFolder(subDir, popClipConfig, loaded);
                }
            }
        }

        return loaded;
    }

    private static void TryAddJsonManifest(string filePath, List<PluginManifest> target)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            var manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name))
            {
                return;
            }

            manifest.Source = PluginSourceType.ExternalFile;
            manifest.SourceFilePath = filePath;
            target.Add(manifest);
        }
        catch
        {
            // Ignore malformed external plugin files
        }
    }

    private static void TryAddPopClipSnippetFile(string filePath, List<PluginManifest> target)
    {
        try
        {
            var raw = File.ReadAllText(filePath);
            var fallbackName = Path.GetFileNameWithoutExtension(filePath);
            if (PopClipSnippetParser.TryParseSnippet(raw, out var manifest, out _, fallbackName) && manifest is not null)
            {
                manifest.Source = PluginSourceType.ExternalFile;
                manifest.SourceFilePath = filePath;
                target.Add(manifest);
            }
        }
        catch
        {
            // Ignore malformed snippets
        }
    }

    private static void TryAddPopClipExtFolder(string folderPath, string configFilePath, List<PluginManifest> target)
    {
        try
        {
            var json = File.ReadAllText(configFilePath);
            if (PopClipSnippetParser.TryParsePopClipExtJson(json, folderPath, out var manifest) && manifest is not null)
            {
                target.Add(manifest);
            }
        }
        catch
        {
            // Ignore malformed .popclipext folders
        }
    }
}
