using System.IO;
using System.Windows;

namespace WinPopClip.App.Native;

/// <summary>
/// Safely reads/writes clipboard text while preventing temporary probes and replacements
/// from polluting Windows 10/11 Clipboard History (Win+V) and Cloud Sync.
/// </summary>
public sealed class ClipboardGuard
{
    private const string ExcludeFromMonitorFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string CanIncludeInHistoryFormat = "CanIncludeInClipboardHistory";
    private const string CanUploadToCloudFormat = "CanUploadToCloudClipboard";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Optional callback to temporarily suppress hook dismissal during synthetic SendInput.</summary>
    public Action<bool>? SetSyntheticInputSuppression { get; set; }

    public async Task SetTextAsync(string text, bool excludeFromHistory = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await RunOnStaAsync(() => SetClipboardTextCore(text, excludeFromHistory));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await RunOnStaAsync(GetClipboardTextCore);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Probes the current selection by snapshotting the entire clipboard (including empty, image,
    /// files, HTML/RTF, and text states), sending Ctrl+C, waiting for sequence number change,
    /// and unconditionally restoring the original clipboard in a cancel-safe finally block.
    /// </summary>
    public async Task<string?> CaptureSelectionViaCopyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await RunOnStaAsync(CaptureClipboardSnapshotCore);
            var beforeSeq = Win32Interop.GetClipboardSequenceNumber();
            var sentCtrlC = false;
            var changed = false;

            try
            {
                SetSyntheticInputSuppression?.Invoke(true);
                try
                {
                    sentCtrlC = true;
                    Win32Interop.SendCtrlKey(Win32Interop.VK_C);
                    // Keep suppression active briefly while the input queue processes injected key events
                    await Task.Delay(25, cancellationToken);
                }
                finally
                {
                    SetSyntheticInputSuppression?.Invoke(false);
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 260)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Win32Interop.GetClipboardSequenceNumber() != beforeSeq)
                    {
                        changed = true;
                        break;
                    }
                    await Task.Delay(15, cancellationToken);
                }

                if (!changed)
                {
                    return null;
                }

                // Small grace delay for Chromium/Electron/Office to finish populating clipboard formats
                await Task.Delay(20, cancellationToken);
                return await RunOnStaAsync(GetClipboardTextCore);
            }
            finally
            {
                // Always restore original clipboard (or clear if it was empty) even if canceled mid-flight
                if (sentCtrlC)
                {
                    if (!changed && Win32Interop.GetClipboardSequenceNumber() == beforeSeq)
                    {
                        await Task.Delay(35, CancellationToken.None);
                    }

                    if (changed || Win32Interop.GetClipboardSequenceNumber() != beforeSeq)
                    {
                        await RunOnStaAsync(() => RestoreClipboardSnapshotCore(snapshot));
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Places replacement text onto clipboard, sends Ctrl+V to target app, and restores prior text.
    /// </summary>
    public async Task PasteTextAsync(string text, bool restorePreviousClipboard = true, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string? backupText = null;
            if (restorePreviousClipboard)
            {
                backupText = await RunOnStaAsync(() => Clipboard.ContainsText() ? GetClipboardTextCore() : null);
            }

            await RunOnStaAsync(() => SetClipboardTextCore(text, excludeFromHistory: true));
            await Task.Delay(25, cancellationToken);

            SetSyntheticInputSuppression?.Invoke(true);
            try
            {
                Win32Interop.SendCtrlKey(Win32Interop.VK_V);
                await Task.Delay(25, cancellationToken);
            }
            finally
            {
                SetSyntheticInputSuppression?.Invoke(false);
            }

            if (restorePreviousClipboard && backupText is not null)
            {
                await Task.Delay(120, cancellationToken);
                await RunOnStaAsync(() => SetClipboardTextCore(backupText, excludeFromHistory: true));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record ClipboardSnapshot(bool WasEmpty, Dictionary<string, object> Formats);

    private static ClipboardSnapshot CaptureClipboardSnapshotCore()
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                var obj = Clipboard.GetDataObject();
                if (obj is null)
                {
                    return new ClipboardSnapshot(true, new Dictionary<string, object>());
                }

                var formats = obj.GetFormats(autoConvert: false);
                if (formats is null || formats.Length == 0)
                {
                    return new ClipboardSnapshot(true, new Dictionary<string, object>());
                }

                var saved = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var fmt in formats)
                {
                    if (string.Equals(fmt, ExcludeFromMonitorFormat, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fmt, CanIncludeInHistoryFormat, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fmt, CanUploadToCloudFormat, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        var val = obj.GetData(fmt, autoConvert: false);
                        if (val is MemoryStream ms)
                        {
                            saved[fmt] = new MemoryStream(ms.ToArray());
                        }
                        else if (val is string or string[] or System.Windows.Media.Imaging.BitmapSource)
                        {
                            saved[fmt] = val;
                        }
                    }
                    catch
                    {
                        // Skip non-copyable transient OLE formats
                    }
                }

                if (saved.Count == 0 && Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();
                    if (text is not null) saved[DataFormats.UnicodeText] = text;
                }

                return new ClipboardSnapshot(saved.Count == 0, saved);
            }
            catch
            {
                Thread.Sleep(15);
            }
        }
        return new ClipboardSnapshot(true, new Dictionary<string, object>());
    }

    private static void RestoreClipboardSnapshotCore(ClipboardSnapshot snapshot)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (snapshot.WasEmpty || snapshot.Formats.Count == 0)
                {
                    Clipboard.Clear();
                    return;
                }

                var data = new DataObject();
                foreach (var kv in snapshot.Formats)
                {
                    if (kv.Value is MemoryStream ms)
                    {
                        data.SetData(kv.Key, new MemoryStream(ms.ToArray()), autoConvert: false);
                    }
                    else
                    {
                        data.SetData(kv.Key, kv.Value, autoConvert: false);
                    }
                }

                data.SetData(ExcludeFromMonitorFormat, new MemoryStream(BitConverter.GetBytes(1)));
                data.SetData(CanIncludeInHistoryFormat, new MemoryStream(BitConverter.GetBytes(0)));
                data.SetData(CanUploadToCloudFormat, new MemoryStream(BitConverter.GetBytes(0)));
                Clipboard.SetDataObject(data, true);
                return;
            }
            catch
            {
                Thread.Sleep(15);
            }
        }
    }

    private static string? GetClipboardTextCore()
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch
            {
                Thread.Sleep(15);
            }
        }
        return null;
    }

    private static void SetClipboardTextCore(string text, bool excludeFromHistory)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                var data = new DataObject();
                data.SetText(text, TextDataFormat.UnicodeText);
                if (excludeFromHistory)
                {
                    data.SetData(ExcludeFromMonitorFormat, new MemoryStream(BitConverter.GetBytes(1)));
                    data.SetData(CanIncludeInHistoryFormat, new MemoryStream(BitConverter.GetBytes(0)));
                    data.SetData(CanUploadToCloudFormat, new MemoryStream(BitConverter.GetBytes(0)));
                }
                Clipboard.SetDataObject(data, true);
                return;
            }
            catch
            {
                Thread.Sleep(15);
            }
        }
    }

    private static Task RunOnStaAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return dispatcher.InvokeAsync(action).Task;
    }

    private static Task<T> RunOnStaAsync<T>(Func<T> func)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return Task.FromResult(func());
        }
        return dispatcher.InvokeAsync(func).Task;
    }
}
