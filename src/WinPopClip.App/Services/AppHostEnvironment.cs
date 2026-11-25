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

    public AppHostEnvironment(ClipboardGuard clipboardGuard, Action<string, ScreenPoint?> showToastCallback)
    {
        _clipboardGuard = clipboardGuard;
        _showToastCallback = showToastCallback;
    }

    public Task CopyTextAsync(string text, CancellationToken cancellationToken = default) =>
        _clipboardGuard.SetTextAsync(text, excludeFromHistory: false, cancellationToken);

    public Task<string?> ReadClipboardTextAsync(CancellationToken cancellationToken = default) =>
        _clipboardGuard.GetTextAsync(cancellationToken);

    public Task SendCopyShortcutAsync(CancellationToken cancellationToken = default)
    {
        Win32Interop.SendCtrlKey(Win32Interop.VK_C);
        return Task.CompletedTask;
    }

    public Task SendCutShortcutAsync(CancellationToken cancellationToken = default)
    {
        Win32Interop.SendCtrlKey(Win32Interop.VK_X);
        return Task.CompletedTask;
    }

    public Task PasteTextAsync(string text, bool restorePreviousClipboard = true, CancellationToken cancellationToken = default) =>
        _clipboardGuard.PasteTextAsync(text, restorePreviousClipboard, cancellationToken);

    public Task SendPasteShortcutAsync(CancellationToken cancellationToken = default)
    {
        Win32Interop.SendCtrlKey(Win32Interop.VK_V);
        return Task.CompletedTask;
    }

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
