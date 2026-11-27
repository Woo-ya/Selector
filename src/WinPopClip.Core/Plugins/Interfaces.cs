using WinPopClip.Core.Models;

namespace WinPopClip.Core.Plugins;

/// <summary>
/// Abstraction of host OS/UI capabilities exposed to plugin executors.
/// Keeps WinPopClip.Core completely decoupled from WPF/Win32 and fully testable.
/// </summary>
public interface IPluginHostEnvironment
{
    Task CopyTextAsync(string text, CancellationToken cancellationToken = default);
    Task<string?> ReadClipboardTextAsync(CancellationToken cancellationToken = default);
    Task SendCopyShortcutAsync(CancellationToken cancellationToken = default);
    Task SendCutShortcutAsync(CancellationToken cancellationToken = default);
    Task PasteTextAsync(string text, bool restorePreviousClipboard = true, CancellationToken cancellationToken = default);
    Task SendPasteShortcutAsync(CancellationToken cancellationToken = default);
    Task SendKeyComboAsync(string key, IReadOnlyList<string> modifiers, CancellationToken cancellationToken = default);
    Task ReappearToolbarAsync(ScreenPoint? anchor = null, CancellationToken cancellationToken = default);
    Task OpenUrlAsync(string url, CancellationToken cancellationToken = default);
    void ShowToast(string message, ScreenPoint? anchor = null);
}

/// <summary>
/// Extensible executor interface. Register new implementations (e.g., JavaScriptExecutor, DllExecutor)
/// in PluginExecutionEngine without changing toolbar or selection logic.
/// </summary>
public interface IPluginExecutor
{
    PluginKind Kind { get; }

    Task<PluginExecutionResult> ExecuteAsync(
        PluginExecutionRequest request,
        IPluginHostEnvironment host,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Extensible plugin provider interface for supplying plugins from built-in catalog,
/// JSON directories, remote feeds, or future compiled .dll assemblies.
/// </summary>
public interface IPluginProvider
{
    string ProviderName { get; }
    IEnumerable<PluginManifest> LoadPlugins();
}

/// <summary>
/// Optional programmatic plugin action contract for future in-process C# / DLL plugins.
/// </summary>
public interface IPluginAction
{
    PluginManifest Metadata { get; }
    bool CanExecute(SelectionContext context);
    Task<PluginExecutionResult> ExecuteAsync(
        PluginExecutionRequest request,
        IPluginHostEnvironment host,
        CancellationToken cancellationToken = default);
}
