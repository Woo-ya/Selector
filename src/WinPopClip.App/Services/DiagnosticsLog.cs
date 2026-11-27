using System.IO;
using System.Text;

namespace WinPopClip.App.Services;

/// <summary>
/// Lightweight in-memory + rolling disk diagnostics log (%APPDATA%\Selector\diagnostics.log)
/// for real-time troubleshooting of selection capture across Edge, Chrome, WorkBuddy, WeChat, etc.
/// </summary>
public sealed class DiagnosticsLog
{
    private readonly object _sync = new();
    private readonly Queue<string> _entries = new();
    private const int MaxEntries = 200;
    private const long MaxLogBytes = 64 * 1024;
    private readonly string _logFilePath;

    public event EventHandler<string>? EntryAdded;

    public DiagnosticsLog()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Selector");
        _logFilePath = Path.Combine(dir, "diagnostics.log");
    }

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

            try
            {
                var dir = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(_logFilePath) && new FileInfo(_logFilePath).Length > MaxLogBytes)
                {
                    File.WriteAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
                }
                else
                {
                    File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
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
            try
            {
                if (File.Exists(_logFilePath)) File.WriteAllText(_logFilePath, string.Empty, Encoding.UTF8);
            }
            catch { }
        }
    }
}
