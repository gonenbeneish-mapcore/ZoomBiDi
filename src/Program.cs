using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace ZoomBiDi;

internal static class Program
{
    /// <summary>
    /// Usage: ZoomBiDi.exe [--settings &lt;path-to-settings.json&gt;]
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        string? settingsPath = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--settings") settingsPath = Path.GetFullPath(args[i + 1]);

        var settings = Settings.Load(settingsPath);

        // One instance per settings file. (string.GetHashCode is randomised per process, so use a stable hash.)
        var pathHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(settings.FilePath.ToLowerInvariant())), 0, 8);
        var mutexName = @"Local\ZoomBiDi_" + pathHash;
        using var mutex = new Mutex(true, mutexName, out bool first);
        if (!first) return 0;

        var log = new Logger(Path.GetDirectoryName(settings.FilePath)!, settings.DebugLog);
        log.Info($"starting, processes=[{string.Join(",", settings.ProcessNames)}], marker=U+{(int)settings.MarkerChar:X4}");

        Regex namePattern = BuildPattern(settings.ChatNamePattern);

        ApplicationConfiguration.Initialize();
        using var inspector = new ChatInspector(log, () => namePattern);
        using var monitor = new KeyboardMonitor(settings, inspector, log);
        try
        {
            monitor.Start();
        }
        catch (Exception ex)
        {
            log.Error("startup", ex);
            MessageBox.Show(ex.Message, "ZoomBiDi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        using var app = new TrayApp(settings, log, monitor);
        Application.Run(app);
        log.Info("exiting");
        return 0;
    }

    static Regex BuildPattern(string pattern)
    {
        try
        {
            return new Regex(string.IsNullOrWhiteSpace(pattern) ? "." : pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            return new Regex("message", RegexOptions.IgnoreCase);
        }
    }
}
