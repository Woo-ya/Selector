using System.Diagnostics;
using WinPopClip.App.Native;
using WinPopClip.Core.Models;
using WinPopClip.Core.Plugins;

namespace WinPopClip.App.Services;

/// <summary>
/// Connects WinPopClip.Core's IPluginHostEnvironment to Windows clipboard, SendInput, shell open, and toolbar HUD.
/// </summary>
public sealed class AppHostEnvironment : IPluginHostEnvironment
{
    private readonly ClipboardGuard _clipboardGuard;
    private readonly Action<string, ScreenPoint?> _showToastCallback;
    private readonly Func<ScreenPoint?, CancellationToken, Task>? _reappearCallback;

    public AppHostEnvironment(
        ClipboardGuard clipboardGuard,
        Action<string, ScreenPoint?> showToastCallback,
        Func<ScreenPoint?, CancellationToken, Task>? reappearCallback = null)
    {
        _clipboardGuard = clipboardGuard;
        _showToastCallback = showToastCallback;
        _reappearCallback = reappearCallback;
    }

    public Task CopyTextAsync(string text, CancellationToken cancellationToken = default) =>
        _clipboardGuard.SetTextAsync(text, excludeFromHistory: false, cancellationToken);

    public Task<string?> ReadClipboardTextAsync(CancellationToken cancellationToken = default) =>
        _clipboardGuard.GetTextAsync(cancellationToken);

    public Task SendCopyShortcutAsync(CancellationToken cancellationToken = default) =>
        _clipboardGuard.SendShortcutAsync(Win32Interop.VK_C, cancellationToken);

    public Task SendCutShortcutAsync(CancellationToken cancellationToken = default) =>
        _clipboardGuard.SendShortcutAsync(Win32Interop.VK_X, cancellationToken);

    public Task PasteTextAsync(string text, bool restorePreviousClipboard = true, CancellationToken cancellationToken = default) =>
        _clipboardGuard.PasteTextAsync(text, restorePreviousClipboard, cancellationToken);

    public Task SendPasteShortcutAsync(CancellationToken cancellationToken = default) =>
        _clipboardGuard.SendShortcutAsync(Win32Interop.VK_V, cancellationToken);

    public Task SendKeyComboAsync(string key, IReadOnlyList<string> modifiers, CancellationToken cancellationToken = default) =>
        _clipboardGuard.SendKeyComboAsync(key, modifiers, cancellationToken);

    public Task ReappearToolbarAsync(ScreenPoint? anchor = null, CancellationToken cancellationToken = default) =>
        _reappearCallback is not null ? _reappearCallback(anchor, cancellationToken) : Task.CompletedTask;

    public Task OpenUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    public void ShowToast(string message, ScreenPoint? anchor = null)
    {
        _showToastCallback(message, anchor);
    }
}
