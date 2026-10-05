using System;
using System.IO;

namespace ZoomBiDi;

internal sealed class Logger
{
    readonly object _lock = new();
    public string FilePath { get; }
    public bool Enabled { get; set; }

    public Logger(string directory, bool enabled)
    {
        FilePath = Path.Combine(directory, "log.txt");
        Enabled = enabled;
    }

    public void Info(string message)
    {
        if (!Enabled) return;
        Write(message);
    }

    public void Error(string message, Exception? ex = null) => Write("ERROR " + message + (ex is null ? "" : ": " + ex));

    void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 2_000_000) info.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch
            {
                // ignore logging failures
            }
        }
    }
}
