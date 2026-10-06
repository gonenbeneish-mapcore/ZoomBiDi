using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace ZoomBiDi;

/// <summary>
/// Debug log. Lines are timestamped when logged and written by a background thread, so a slow disk (or a virus
/// scanner) never stalls the keyboard hook - Windows removes hooks that respond slowly.
/// </summary>
internal sealed class Logger
{
    readonly BlockingCollection<string> _lines = new(boundedCapacity: 10_000);
    public string FilePath { get; }
    public bool Enabled { get; set; }

    public Logger(string directory, bool enabled)
    {
        FilePath = Path.Combine(directory, "log.txt");
        Enabled = enabled;
        new Thread(Writer) { IsBackground = true, Name = "Log writer", Priority = ThreadPriority.BelowNormal }.Start();
    }

    public void Info(string message)
    {
        if (!Enabled) return;
        Enqueue(message);
    }

    public void Error(string message, Exception? ex = null) => Enqueue("ERROR " + message + (ex is null ? "" : ": " + ex));

    void Enqueue(string line) => _lines.TryAdd($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}");

    void Writer()
    {
        foreach (var line in _lines.GetConsumingEnumerable())
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 2_000_000) info.Delete();
                using var w = new StreamWriter(FilePath, append: true);
                w.WriteLine(line);
                // write whatever else is already queued in the same go
                while (_lines.TryTake(out var more)) w.WriteLine(more);
            }
            catch
            {
                // ignore logging failures
            }
        }
    }
}
