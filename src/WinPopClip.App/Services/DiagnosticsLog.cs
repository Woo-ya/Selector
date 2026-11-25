namespace WinPopClip.App.Services;

/// <summary>
/// Lightweight in-memory diagnostics log for real-time troubleshooting of selection capture
/// across Edge, Chrome, Notepad, VS Code, etc.
/// </summary>
public sealed class DiagnosticsLog
{
    private readonly object _sync = new();
    private readonly Queue<string> _entries = new();
    private const int MaxEntries = 200;

    public event EventHandler<string>? EntryAdded;

    public void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (_sync)
        {
            _entries.Enqueue(line);
            while (_entries.Count > MaxEntries)
            {
                _entries.Dequeue();
            }
        }
        EntryAdded?.Invoke(this, line);
    }

    public IReadOnlyList<string> GetSnapshot()
    {
        lock (_sync)
        {
            return _entries.ToList();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }
}
