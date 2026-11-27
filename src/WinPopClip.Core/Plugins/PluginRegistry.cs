using WinPopClip.Core.Models;
using WinPopClip.Core.Storage;

namespace WinPopClip.Core.Plugins;

/// <summary>
/// Central registry managing built-in, directory-based external JSON/PopClip, and user-created custom plugins,
/// along with enable/disable states, ordering, custom icons, and per-plugin option persistence.
/// </summary>
public sealed class PluginRegistry
{
    private readonly SettingsService _settingsService;
    private readonly IReadOnlyList<IPluginProvider> _providers;
    private readonly List<PluginManifest> _plugins = new();
    private readonly object _sync = new();

    public event EventHandler? PluginsChanged;

    public PluginRegistry(SettingsService settingsService, IEnumerable<IPluginProvider> providers)
    {
        _settingsService = settingsService;
        _providers = providers.ToList();
        Reload();
    }

    public IReadOnlyList<PluginManifest> GetAllPlugins()
    {
        lock (_sync)
        {
            return _plugins.OrderBy(p => p.Order).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public PluginManifest? GetById(string id)
    {
        lock (_sync)
        {
            return _plugins.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyDictionary<string, string> GetResolvedOptions(PluginManifest plugin)
    {
        lock (_sync)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var opt in plugin.Options)
            {
                result[opt.Key] = opt.DefaultValue;
            }

            if (_settingsService.Settings.PluginStates.TryGetValue(plugin.Id, out var state))
            {
                foreach (var kv in state.OptionValues)
                {
                    result[kv.Key] = kv.Value;
                }
            }

            return result;
        }
    }

    public void Reload()
    {
        lock (_sync)
        {
            var settings = _settingsService.Settings;
            var merged = new Dictionary<string, PluginManifest>(StringComparer.OrdinalIgnoreCase);
            var nextOrder = 0;

            foreach (var provider in _providers)
            {
                foreach (var raw in provider.LoadPlugins())
                {
                    var plugin = raw.Clone();
                    plugin.Order = nextOrder++;
                    merged[plugin.Id] = plugin;
                }
            }

            foreach (var custom in settings.CustomPlugins)
            {
                var plugin = custom.Clone();
                plugin.Source = PluginSourceType.UserCustom;
                plugin.Order = nextOrder++;
                merged[plugin.Id] = plugin;
            }

            // Apply saved state overrides (enabled + ordering + custom icon)
            foreach (var plugin in merged.Values)
            {
                plugin.DefaultIcon = plugin.Icon;
                if (settings.PluginStates.TryGetValue(plugin.Id, out var state))
                {
                    plugin.Enabled = state.Enabled;
                    plugin.Order = state.Order;
                    plugin.DisplayMode = state.DisplayMode;
                    if (!string.IsNullOrWhiteSpace(state.CustomIcon))
                    {
                        plugin.Icon = state.CustomIcon;
                    }
                }
            }

            _plugins.Clear();
            _plugins.AddRange(merged.Values.OrderBy(p => p.Order));
            NormalizeOrders();
        }

        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetEnabled(string pluginId, bool enabled)
    {
        lock (_sync)
        {
            var plugin = _plugins.FirstOrDefault(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
            if (plugin is null) return;

            plugin.Enabled = enabled;
            EnsureState(plugin).Enabled = enabled;
            _settingsService.Save();
        }

        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetCustomIcon(string pluginId, string icon)
    {
        lock (_sync)
        {
            var plugin = _plugins.FirstOrDefault(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
            if (plugin is null) return;

            var trimmed = string.IsNullOrWhiteSpace(icon) ? "⚡" : icon.Trim();
            plugin.Icon = trimmed;
            EnsureState(plugin).CustomIcon = trimmed;

            // Also mirror into CustomPlugins if it's a user custom plugin
            var custom = _settingsService.Settings.CustomPlugins
                .FirstOrDefault(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
            if (custom is not null)
            {
                custom.Icon = trimmed;
            }

            _settingsService.Save();
        }

        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetCustomIcon(string pluginId)
    {
        lock (_sync)
        {
            var plugin = _plugins.FirstOrDefault(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
            if (plugin is null) return;

            var restored = !string.IsNullOrWhiteSpace(plugin.DefaultIcon) ? plugin.DefaultIcon : "symbol:sparkle";
            plugin.Icon = restored;
            if (_settingsService.Settings.PluginStates.TryGetValue(plugin.Id, out var state))
            {
                state.CustomIcon = null;
            }

            _settingsService.Save();
        }

        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetDisplayMode(string pluginId, ToolbarDisplayMode? displayMode)
    {
        lock (_sync)
        {
            var plugin = _plugins.FirstOrDefault(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
            if (plugin is null) return;

            plugin.DisplayMode = displayMode;
            EnsureState(plugin).DisplayMode = displayMode;
            _settingsService.Save();
        }

        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MovePlugin(string pluginId, int delta)
    {
        var ordered = GetAllPlugins().ToList();
        var index = ordered.FindIndex(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;

        var targetIndex = Math.Clamp(index + delta, 0, ordered.Count - 1);
        MovePluginToIndex(pluginId, targetIndex);
    }

    public void MovePluginToIndex(string pluginId, int targetIndex)
    {
        lock (_sync)
        {
            var ordered = _plugins.OrderBy(p => p.Order).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var index = ordered.FindIndex(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return;

            var clampedTarget = Math.Clamp(targetIndex, 0, ordered.Count - 1);
            if (clampedTarget == index) return;

            var item = ordered[index];
            ordered.RemoveAt(index);
            ordered.Insert(clampedTarget, item);

            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].Order = i;
                EnsureState(ordered[i]).Order = i;
            }

            _settingsService.Save();
        }

        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SaveOptionValues(string pluginId, IDictionary<string, string> optionValues)
    {
        lock (_sync)
        {
            var plugin = _plugins.FirstOrDefault(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
            if (plugin is null) return;

            var state = EnsureState(plugin);
            state.OptionValues = new Dictionary<string, string>(optionValues, StringComparer.OrdinalIgnoreCase);
            _settingsService.Save();
        }

        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    public PluginManifest AddOrUpdateCustomPlugin(PluginManifest manifest)
    {
        string savedId;
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(manifest.Id))
            {
                manifest.Id = "custom." + Guid.NewGuid().ToString("N")[..8];
            }

            manifest.Source = PluginSourceType.UserCustom;
            var settings = _settingsService.Settings;
            var existingIndex = settings.CustomPlugins.FindIndex(p => p.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase));
            var copy = manifest.Clone();
            copy.Source = PluginSourceType.UserCustom;

            if (existingIndex >= 0)
            {
                settings.CustomPlugins[existingIndex] = copy;
                if (settings.PluginStates.TryGetValue(copy.Id, out var existingState))
                {
                    existingState.CustomIcon = copy.Icon;
                }
            }
            else
            {
                settings.CustomPlugins.Add(copy);
                settings.PluginStates[copy.Id] = new PluginStateOverride
                {
                    Enabled = true,
                    Order = _plugins.Count,
                    CustomIcon = copy.Icon
                };
            }

            savedId = copy.Id;
            _settingsService.Save();
        }

        Reload();
        return GetById(savedId) ?? manifest;
    }

    public bool TryImportPopClipSnippet(string snippetText, out PluginManifest? imported, out string? error)
    {
        imported = null;
        if (!PopClipSnippetParser.TryParseSnippet(snippetText, out var parsed, out error) || parsed is null)
        {
            return false;
        }

        imported = AddOrUpdateCustomPlugin(parsed);
        return true;
    }

    public bool RemoveCustomPlugin(string pluginId)
    {
        bool removed;
        lock (_sync)
        {
            var settings = _settingsService.Settings;
            removed = settings.CustomPlugins.RemoveAll(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase)) > 0;
            if (!removed) return false;

            settings.PluginStates.Remove(pluginId);
            _settingsService.Save();
        }

        Reload();
        return true;
    }

    /// <summary>
    /// Deletes an installed external plugin file or bundle folder inside userPluginsDir and reloads registry.
    /// </summary>
    public bool RemoveInstalledExternalPlugin(string pluginId, string userPluginsDir)
    {
        var plugin = GetById(pluginId);
        if (plugin is null || plugin.Source != PluginSourceType.ExternalFile || string.IsNullOrWhiteSpace(plugin.SourceFilePath))
        {
            return false;
        }

        var fullUserRoot = Path.GetFullPath(userPluginsDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullSourcePath = Path.GetFullPath(plugin.SourceFilePath);
        if (!fullSourcePath.StartsWith(fullUserRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relative = fullSourcePath[fullUserRoot.Length..];
        var firstSegment = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstSegment)) return false;

        var targetPath = Path.Combine(userPluginsDir, firstSegment);
        if (Directory.Exists(targetPath))
        {
            Directory.Delete(targetPath, recursive: true);
        }
        else if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        lock (_sync)
        {
            _settingsService.Settings.PluginStates.Remove(pluginId);
            _settingsService.Save();
        }

        Reload();
        return true;
    }

    private PluginStateOverride EnsureState(PluginManifest plugin)
    {
        var states = _settingsService.Settings.PluginStates;
        if (!states.TryGetValue(plugin.Id, out var state))
        {
            state = new PluginStateOverride
            {
                Enabled = plugin.Enabled,
                Order = plugin.Order
            };
            states[plugin.Id] = state;
        }
        return state;
    }

    private void NormalizeOrders()
    {
        for (var i = 0; i < _plugins.Count; i++)
        {
            _plugins[i].Order = i;
        }
    }
}
