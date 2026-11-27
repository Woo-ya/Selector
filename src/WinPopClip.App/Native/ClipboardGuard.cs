using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace WinPopClip.App.Native;

/// <summary>
/// Safely reads/writes clipboard text off the WPF UI thread using direct Win32 Clipboard APIs
/// (never calling blocking OLE flush or Thread.Sleep on the UI thread) while preventing temporary
/// probes and replacements from polluting Windows 10/11 Clipboard History (Win+V) and Cloud Sync.
/// </summary>
public sealed class ClipboardGuard
{
    private const string ClipboardViewerIgnoreFormat = "Clipboard Viewer Ignore";
    private const string ExcludeFromMonitorFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string CanIncludeInHistoryFormat = "CanIncludeInClipboardHistory";
    private const string CanUploadToCloudFormat = "CanUploadToCloudClipboard";

    private static readonly Lazy<uint> ClipboardViewerIgnoreFormatId = new(() => Win32Interop.RegisterClipboardFormatW(ClipboardViewerIgnoreFormat));
    private static readonly Lazy<uint> ExcludeFromMonitorFormatId = new(() => Win32Interop.RegisterClipboardFormatW(ExcludeFromMonitorFormat));
    private static readonly Lazy<uint> CanIncludeInHistoryFormatId = new(() => Win32Interop.RegisterClipboardFormatW(CanIncludeInHistoryFormat));
    private static readonly Lazy<uint> CanUploadToCloudFormatId = new(() => Win32Interop.RegisterClipboardFormatW(CanUploadToCloudFormat));

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Optional callback to temporarily suppress hook dismissal during synthetic SendInput.</summary>
    public Action<bool>? SetSyntheticInputSuppression { get; set; }

    public async Task SetTextAsync(string text, bool excludeFromHistory = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SetTextWin32WithRetryAsync(text, excludeFromHistory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GetTextWin32WithRetryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sends Ctrl+C/V/X off the UI thread with synthetic input suppression so low-level hooks ignore our injected keys.
    /// </summary>
    public async Task SendShortcutAsync(ushort keyVk, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetSyntheticInputSuppression?.Invoke(true);
            try
            {
                await Task.Run(() => Win32Interop.SendCtrlKey(keyVk), cancellationToken).ConfigureAwait(false);
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                SetSyntheticInputSuppression?.Invoke(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sends an arbitrary key combo (e.g., Ctrl+A translated from PopClip's Command+A) off the UI thread
    /// with low-level keyboard hook suppression enabled.
    /// </summary>
    public async Task SendKeyComboAsync(string key, IReadOnlyList<string> modifiers, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetSyntheticInputSuppression?.Invoke(true);
            try
            {
                await Task.Run(() => Win32Interop.SendKeyCombo(key, modifiers), cancellationToken).ConfigureAwait(false);
                await Task.Delay(30, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                SetSyntheticInputSuppression?.Invoke(false);
            }
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await CaptureClipboardSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var beforeSeq = Win32Interop.GetClipboardSequenceNumber();
            var sentCtrlC = false;
            var changed = false;

            try
            {
                SetSyntheticInputSuppression?.Invoke(true);
                try
                {
                    sentCtrlC = true;
                    await Task.Run(() => Win32Interop.SendCtrlKey(Win32Interop.VK_C), cancellationToken).ConfigureAwait(false);
                    await Task.Delay(30, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    SetSyntheticInputSuppression?.Invoke(false);
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 180)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Win32Interop.GetClipboardSequenceNumber() != beforeSeq)
                    {
                        changed = true;
                        break;
                    }
                    await Task.Delay(12, cancellationToken).ConfigureAwait(false);
                }

                if (!changed)
                {
                    return null;
                }

                // Grace delay for Chromium/Electron/Qt/WeChat to finish populating all clipboard formats before read+restore
                await Task.Delay(35, cancellationToken).ConfigureAwait(false);
                return await GetTextWin32WithRetryAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (sentCtrlC)
                {
                    if (!changed && Win32Interop.GetClipboardSequenceNumber() == beforeSeq)
                    {
                        await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
                    }

                    if (changed || Win32Interop.GetClipboardSequenceNumber() != beforeSeq)
                    {
                        await RestoreClipboardSnapshotAsync(snapshot).ConfigureAwait(false);
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? backupText = null;
            if (restorePreviousClipboard)
            {
                backupText = await GetTextWin32WithRetryAsync(cancellationToken).ConfigureAwait(false);
            }

            await SetTextWin32WithRetryAsync(text, excludeFromHistory: true, cancellationToken).ConfigureAwait(false);
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);

            SetSyntheticInputSuppression?.Invoke(true);
            try
            {
                await Task.Run(() => Win32Interop.SendCtrlKey(Win32Interop.VK_V), cancellationToken).ConfigureAwait(false);
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                SetSyntheticInputSuppression?.Invoke(false);
            }

            if (restorePreviousClipboard && backupText is not null)
            {
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                await SetTextWin32WithRetryAsync(backupText, excludeFromHistory: true, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task SetTextWin32WithRetryAsync(string text, bool excludeFromHistory, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ok = await Task.Run(() => TrySetTextWin32Once(text, excludeFromHistory), cancellationToken).ConfigureAwait(false);
            if (ok) return;
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string?> GetTextWin32WithRetryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (opened, value) = await Task.Run(TryGetTextWin32Once, cancellationToken).ConfigureAwait(false);
            if (opened) return value;
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static bool TrySetTextWin32Once(string text, bool excludeFromHistory)
    {
        if (!Win32Interop.OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        try
        {
            if (!Win32Interop.EmptyClipboard())
            {
                return false;
            }

            var chars = text ?? string.Empty;
            var byteCount = checked((chars.Length + 1) * sizeof(char));
            var hGlobal = Win32Interop.GlobalAlloc(Win32Interop.GMEM_MOVEABLE, (UIntPtr)byteCount);
            if (hGlobal == IntPtr.Zero)
            {
                return false;
            }

            var locked = Win32Interop.GlobalLock(hGlobal);
            if (locked == IntPtr.Zero)
            {
                Win32Interop.GlobalFree(hGlobal);
                return false;
            }

            try
            {
                if (chars.Length > 0)
                {
                    Marshal.Copy(chars.ToCharArray(), 0, locked, chars.Length);
                }
                Marshal.WriteInt16(locked, chars.Length * sizeof(char), 0);
            }
            finally
            {
                Win32Interop.GlobalUnlock(hGlobal);
            }

            if (Win32Interop.SetClipboardData(Win32Interop.CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
            {
                Win32Interop.GlobalFree(hGlobal);
                return false;
            }

            if (excludeFromHistory)
            {
                TrySetClipboardDword(ClipboardViewerIgnoreFormatId.Value, 1);
                TrySetClipboardDword(ExcludeFromMonitorFormatId.Value, 1);
                TrySetClipboardDword(CanIncludeInHistoryFormatId.Value, 0);
                TrySetClipboardDword(CanUploadToCloudFormatId.Value, 0);
            }

            return true;
        }
        finally
        {
            Win32Interop.CloseClipboard();
        }
    }

    private static void TrySetClipboardDword(uint formatId, int value)
    {
        if (formatId == 0) return;

        var hMem = Win32Interop.GlobalAlloc(Win32Interop.GMEM_MOVEABLE, (UIntPtr)sizeof(int));
        if (hMem == IntPtr.Zero) return;

        var ptr = Win32Interop.GlobalLock(hMem);
        if (ptr == IntPtr.Zero)
        {
            Win32Interop.GlobalFree(hMem);
            return;
        }

        try
        {
            Marshal.WriteInt32(ptr, value);
        }
        finally
        {
            Win32Interop.GlobalUnlock(hMem);
        }

        if (Win32Interop.SetClipboardData(formatId, hMem) == IntPtr.Zero)
        {
            Win32Interop.GlobalFree(hMem);
        }
    }

    private static (bool Opened, string? Text) TryGetTextWin32Once()
    {
        if (!Win32Interop.IsClipboardFormatAvailable(Win32Interop.CF_UNICODETEXT))
        {
            return (true, null);
        }

        if (!Win32Interop.OpenClipboard(IntPtr.Zero))
        {
            return (false, null);
        }

        try
        {
            var handle = Win32Interop.GetClipboardData(Win32Interop.CF_UNICODETEXT);
            if (handle == IntPtr.Zero)
            {
                return (true, null);
            }

            var ptr = Win32Interop.GlobalLock(handle);
            if (ptr == IntPtr.Zero)
            {
                return (true, null);
            }

            try
            {
                return (true, Marshal.PtrToStringUni(ptr));
            }
            finally
            {
                Win32Interop.GlobalUnlock(handle);
            }
        }
        finally
        {
            Win32Interop.CloseClipboard();
        }
    }

    private sealed record ClipboardSnapshot(
        bool WasEmpty,
        string? PlainTextFallback,
        bool IsTextOnly,
        Dictionary<string, object> Formats);

    private static readonly HashSet<string> SynthesizedTextFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        DataFormats.UnicodeText,
        DataFormats.Text,
        DataFormats.OemText,
        DataFormats.StringFormat,
        DataFormats.Locale
    };

    private static async Task<ClipboardSnapshot> CaptureClipboardSnapshotAsync(CancellationToken cancellationToken)
    {
        // Fast pure-Win32 check first: if clipboard is empty, no STA round-trip needed
        var hasText = Win32Interop.IsClipboardFormatAvailable(Win32Interop.CF_UNICODETEXT);
        var plainText = hasText ? await GetTextWin32WithRetryAsync(cancellationToken).ConfigureAwait(false) : null;

        for (var i = 0; i < 5; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (ok, snap) = await RunOnStaAsync(() => TryCaptureClipboardSnapshotOnce(plainText)).ConfigureAwait(false);
            if (ok && snap is not null) return snap;
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }

        return plainText is not null
            ? new ClipboardSnapshot(false, plainText, true, new Dictionary<string, object> { [DataFormats.UnicodeText] = plainText })
            : new ClipboardSnapshot(true, null, true, new Dictionary<string, object>());
    }

    private static async Task RestoreClipboardSnapshotAsync(ClipboardSnapshot snapshot)
    {
        // Fast pure-Win32 path for empty or plain-text snapshots (off UI thread, < 1ms, 100% reliable)
        if (snapshot.WasEmpty || (snapshot.Formats.Count == 0 && snapshot.PlainTextFallback is null))
        {
            for (var i = 0; i < 8; i++)
            {
                var ok = await Task.Run(TryClearClipboardWin32Once).ConfigureAwait(false);
                if (ok) return;
                await Task.Delay(15, CancellationToken.None).ConfigureAwait(false);
            }
            return;
        }

        if (snapshot.IsTextOnly && snapshot.PlainTextFallback is not null)
        {
            await SetTextWin32WithRetryAsync(snapshot.PlainTextFallback, excludeFromHistory: true, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        for (var i = 0; i < 5; i++)
        {
            var ok = await RunOnStaAsync(() => TryRestoreClipboardSnapshotOnce(snapshot)).ConfigureAwait(false);
            if (ok) return;
            await Task.Delay(15, CancellationToken.None).ConfigureAwait(false);
        }

        // Guaranteed fallback: never leave newly probed selection stuck on clipboard if rich OLE restore failed
        if (snapshot.PlainTextFallback is not null)
        {
            await SetTextWin32WithRetryAsync(snapshot.PlainTextFallback, excludeFromHistory: true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool TryClearClipboardWin32Once()
    {
        if (!Win32Interop.OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            // Keep truly empty without writing binary marker formats that third-party clipboard tools display as "未知格式"
            return Win32Interop.EmptyClipboard();
        }
        finally
        {
            Win32Interop.CloseClipboard();
        }
    }

    private static (bool Ok, ClipboardSnapshot? Snapshot) TryCaptureClipboardSnapshotOnce(string? preReadPlainText)
    {
        try
        {
            var obj = Clipboard.GetDataObject();
            if (obj is null)
            {
                return preReadPlainText is not null
                    ? (true, new ClipboardSnapshot(false, preReadPlainText, true, new Dictionary<string, object> { [DataFormats.UnicodeText] = preReadPlainText }))
                    : (true, new ClipboardSnapshot(true, null, true, new Dictionary<string, object>()));
            }

            var formats = obj.GetFormats(autoConvert: false);
            if (formats is null || formats.Length == 0)
            {
                return preReadPlainText is not null
                    ? (true, new ClipboardSnapshot(false, preReadPlainText, true, new Dictionary<string, object> { [DataFormats.UnicodeText] = preReadPlainText }))
                    : (true, new ClipboardSnapshot(true, null, true, new Dictionary<string, object>()));
            }

            var saved = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var hasRichNonTextFormat = false;

            foreach (var fmt in formats)
            {
                if (string.Equals(fmt, ClipboardViewerIgnoreFormat, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fmt, ExcludeFromMonitorFormat, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fmt, CanIncludeInHistoryFormat, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fmt, CanUploadToCloudFormat, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fmt, DataFormats.Locale, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Skip OS-synthesized ANSI/OEM text duplicates when UnicodeText is already captured
                if (SynthesizedTextFormats.Contains(fmt) && !string.Equals(fmt, DataFormats.UnicodeText, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var val = obj.GetData(fmt, autoConvert: false);
                    if (val is MemoryStream ms)
                    {
                        saved[fmt] = new MemoryStream(ms.ToArray());
                        if (!SynthesizedTextFormats.Contains(fmt)) hasRichNonTextFormat = true;
                    }
                    else if (val is string or string[] or System.Windows.Media.Imaging.BitmapSource)
                    {
                        saved[fmt] = val;
                        if (!SynthesizedTextFormats.Contains(fmt)) hasRichNonTextFormat = true;
                    }
                }
                catch
                {
                    // Skip non-copyable transient OLE formats
                }
            }

            var plainText = preReadPlainText;
            if (plainText is null && saved.TryGetValue(DataFormats.UnicodeText, out var uniObj) && uniObj is string uniStr)
            {
                plainText = uniStr;
            }
            if (plainText is null && Clipboard.ContainsText())
            {
                plainText = Clipboard.GetText();
                if (plainText is not null) saved[DataFormats.UnicodeText] = plainText;
            }

            var isEmpty = saved.Count == 0 && plainText is null;
            var isTextOnly = !hasRichNonTextFormat && plainText is not null;
            return (true, new ClipboardSnapshot(isEmpty, plainText, isTextOnly, saved));
        }
        catch
        {
            return (false, null);
        }
    }

    private static bool TryRestoreClipboardSnapshotOnce(ClipboardSnapshot snapshot)
    {
        try
        {
            if (snapshot.WasEmpty || snapshot.Formats.Count == 0)
            {
                Clipboard.Clear();
                return true;
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

            data.SetData(ClipboardViewerIgnoreFormat, new MemoryStream(BitConverter.GetBytes(1)));
            data.SetData(ExcludeFromMonitorFormat, new MemoryStream(BitConverter.GetBytes(1)));
            data.SetData(CanIncludeInHistoryFormat, new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData(CanUploadToCloudFormat, new MemoryStream(BitConverter.GetBytes(0)));
            Clipboard.SetDataObject(data, true);
            return true;
        }
        catch
        {
            return false;
        }
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
