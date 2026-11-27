using System.IO.Compression;
using System.Text.Json;
using WinPopClip.Core.Models;

namespace WinPopClip.Core.Plugins;

public sealed record PluginInstallItemResult(
    string SourceName,
    bool Success,
    string? PluginId = null,
    string? PluginName = null,
    string? ErrorMessage = null,
    bool Recompiled = false);

public sealed record PluginInstallBatchResult(
    IReadOnlyList<PluginInstallItemResult> Items)
{
    public int SucceededCount => Items.Count(i => i.Success);
    public int FailedCount => Items.Count(i => !i.Success);
}

/// <summary>
/// Installs dropped or selected plugin files/packages directly into the user's plugins directory
/// and reloads PluginRegistry immediately. Supports:
/// - PopClip zipped bundles (*.popclipextz, *.zip containing .popclipext / Config.json / Config.yaml / Config.plist)
/// - PopClip unpacked folders (*.popclipext) or Selector plugin folders (plugin.json)
/// - PopClip snippet files (*.popcliptxt, *.yaml, *.yml, *.js, *.txt with #popclip header)
/// - Selector declarative JSON plugin files (*.json)
/// </summary>
public static class PluginPackageInstaller
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly HashSet<string> SnippetFileExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".popcliptxt", ".yaml", ".yml", ".js", ".ts", ".txt"
    };

    public static PluginInstallBatchResult InstallFromPaths(
        IEnumerable<string> paths,
        string userPluginsDir,
        PluginRegistry registry)
    {
        Directory.CreateDirectory(userPluginsDir);
        var results = new List<PluginInstallItemResult>();
        var installedIds = new List<string>();

        foreach (var rawPath in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = rawPath.Trim();
            var displayName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(displayName)) displayName = path;

            try
            {
                if (Directory.Exists(path))
                {
                    var res = InstallFromDirectory(path, displayName, userPluginsDir);
                    results.Add(res);
                    if (res.Success && !string.IsNullOrWhiteSpace(res.PluginId)) installedIds.Add(res.PluginId);
                }
                else if (File.Exists(path))
                {
                    var ext = Path.GetExtension(path);
                    PluginInstallItemResult res;
                    if (ext.Equals(".popclipextz", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        res = InstallFromArchive(path, displayName, userPluginsDir);
                    }
                    else if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        res = InstallFromJsonFile(path, displayName, userPluginsDir);
                    }
                    else if (SnippetFileExts.Contains(ext))
                    {
                        res = InstallFromSnippetFile(path, displayName, userPluginsDir);
                    }
                    else
                    {
                        res = new PluginInstallItemResult(displayName, false, ErrorMessage: $"不支持的文件格式（{ext}），请拖入 .popclipextz / .popclipext / .json / .js / .popcliptxt");
                    }

                    results.Add(res);
                    if (res.Success && !string.IsNullOrWhiteSpace(res.PluginId)) installedIds.Add(res.PluginId);
                }
                else
                {
                    results.Add(new PluginInstallItemResult(displayName, false, ErrorMessage: "文件或目录不存在"));
                }
            }
            catch (Exception ex)
            {
                results.Add(new PluginInstallItemResult(displayName, false, ErrorMessage: ex.Message));
            }
        }

        if (installedIds.Count > 0)
        {
            registry.Reload();
            foreach (var id in installedIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                registry.SetEnabled(id, true);
            }
        }

        return new PluginInstallBatchResult(results);
    }

    private static PluginInstallItemResult InstallFromArchive(string archivePath, string displayName, string userPluginsDir)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SelectorExt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            ExtractZipSafely(archivePath, tempDir);

            // Find .popclipext bundle or folder containing Config.json / Config.yaml / Config.plist / plugin.json
            var bundleDir = FindExtensionRoot(tempDir);
            if (bundleDir is null)
            {
                return new PluginInstallItemResult(displayName, false, ErrorMessage: "压缩包内未找到有效的 PopClip（Config.json / Config.yaml / Config.plist）或 Selector（plugin.json）扩展");
            }

            var targetFolderName = ResolveArchiveFolderName(archivePath, bundleDir, tempDir);
            return InstallValidatedFolder(bundleDir, targetFolderName, displayName, userPluginsDir);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static PluginInstallItemResult InstallFromDirectory(string sourceDir, string displayName, string userPluginsDir)
    {
        var bundleDir = FindExtensionRoot(sourceDir);
        if (bundleDir is null)
        {
            return new PluginInstallItemResult(displayName, false, ErrorMessage: "目录中未找到 Config.json、Config.yaml、Config.plist 或 plugin.json");
        }

        var folderName = Path.GetFileName(bundleDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return InstallValidatedFolder(bundleDir, folderName, displayName, userPluginsDir);
    }

    private static PluginInstallItemResult InstallValidatedFolder(
        string bundleDir,
        string targetFolderName,
        string displayName,
        string userPluginsDir)
    {
        PluginManifest? manifest = null;
        string? parseError = null;

        var selectorPluginJson = Path.Combine(bundleDir, "plugin.json");
        if (File.Exists(selectorPluginJson))
        {
            var json = File.ReadAllText(selectorPluginJson);
            manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name))
            {
                parseError = "plugin.json 缺少必要的 id 或 name 字段";
                manifest = null;
            }
        }
        else
        {
            PopClipSnippetParser.TryParsePopClipExtFolder(bundleDir, out manifest, out parseError);
        }

        if (manifest is null)
        {
            return new PluginInstallItemResult(displayName, false, ErrorMessage: parseError ?? "无法解析该扩展包");
        }

        var recompiled = File.Exists(Path.Combine(bundleDir, PopClipExtensionCompiler.CompiledMetaFileName));

        var safeName = SanitizeFileName(targetFolderName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = SanitizeFileName(manifest.Name) + ".popclipext";
        }

        var destDir = Path.Combine(userPluginsDir, safeName);
        if (Directory.Exists(destDir))
        {
            Directory.Delete(destDir, recursive: true);
        }
        CopyDirectoryRecursive(bundleDir, destDir);

        return new PluginInstallItemResult(displayName, true, manifest.Id, manifest.Name, Recompiled: recompiled);
    }

    private static PluginInstallItemResult InstallFromJsonFile(string jsonPath, string displayName, string userPluginsDir)
    {
        var json = File.ReadAllText(jsonPath);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name))
        {
            return new PluginInstallItemResult(displayName, false, ErrorMessage: "JSON 插件缺少有效的 id 或 name 字段");
        }

        var destFile = Path.Combine(userPluginsDir, Path.GetFileName(jsonPath));
        File.Copy(jsonPath, destFile, overwrite: true);
        return new PluginInstallItemResult(displayName, true, manifest.Id, manifest.Name);
    }

    private static PluginInstallItemResult InstallFromSnippetFile(string filePath, string displayName, string userPluginsDir)
    {
        var raw = File.ReadAllText(filePath);
        var ext = Path.GetExtension(filePath);
        var fallbackName = Path.GetFileNameWithoutExtension(filePath);

        // Standalone .ts or ESM .js snippet -> wrap into a .popclipext folder and recompile on import
        if (PopClipExtensionCompiler.NeedsCompilation(filePath, raw))
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "SelectorTs_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);
            try
            {
                var entryName = ext.Equals(".ts", StringComparison.OrdinalIgnoreCase) ? "Config.ts" : "Config.js";
                File.WriteAllText(Path.Combine(tempDir, entryName), raw);
                var folderName = SanitizeFileName(fallbackName) + ".popclipext";
                return InstallValidatedFolder(tempDir, folderName, displayName, userPluginsDir);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        if (!PopClipSnippetParser.TryParseSnippet(raw, out var manifest, out var err, fallbackName) || manifest is null)
        {
            return new PluginInstallItemResult(displayName, false, ErrorMessage: err ?? "不是有效的 PopClip Snippet");
        }

        var targetFileName = ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(filePath) + ".popcliptxt"
            : Path.GetFileName(filePath);

        var destFile = Path.Combine(userPluginsDir, SanitizeFileName(targetFileName));
        File.Copy(filePath, destFile, overwrite: true);
        return new PluginInstallItemResult(displayName, true, manifest.Id, manifest.Name);
    }

    private static void ExtractZipSafely(string archivePath, string destinationDir)
    {
        var fullDestRoot = Path.GetFullPath(destinationDir + Path.DirectorySeparatorChar);
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.FullName) ||
                entry.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetPath = Path.GetFullPath(Path.Combine(destinationDir, entry.FullName));
            if (!targetPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("扩展压缩包包含非法路径，已拒绝解压。");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private static string? FindExtensionRoot(string rootDir)
    {
        if (HasConfigFile(rootDir)) return rootDir;

        // Prefer *.popclipext subfolder at depth 1 or 2
        foreach (var dir in Directory.EnumerateDirectories(rootDir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(dir);
            if (name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith(".popclipext", StringComparison.OrdinalIgnoreCase) && HasConfigFile(dir))
            {
                return dir;
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(rootDir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(dir);
            if (name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;
            if (HasConfigFile(dir)) return dir;
        }

        return null;
    }

    private static bool HasConfigFile(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("Config.json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Config.yaml", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Config.yml", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Config.ts", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Config.js", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Config.plist", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("plugin.json", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string ResolveArchiveFolderName(string archivePath, string bundleDir, string tempDir)
    {
        if (!bundleDir.Equals(tempDir, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(bundleDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        var baseName = Path.GetFileNameWithoutExtension(archivePath);
        return baseName.EndsWith(".popclipext", StringComparison.OrdinalIgnoreCase)
            ? baseName
            : baseName + ".popclipext";
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var sub in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(sub);
            if (name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;
            CopyDirectoryRecursive(sub, Path.Combine(destDir, name));
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
    }
}
