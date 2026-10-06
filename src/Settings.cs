using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZoomBiDi;

internal sealed class Settings
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Hex code point of the character to insert. 2068 = FIRST STRONG ISOLATE: the line takes the direction of
    /// its first letter, so Hebrew lines read right-to-left and English lines stay left-to-right.
    /// (Up to 1.2 the default was 2067, RIGHT-TO-LEFT ISOLATE, which also reversed English-first lines.)
    /// </summary>
    public string MarkerHex { get; set; } = "2068";

    /// <summary>The default before 1.3; a settings file still holding it is upgraded (write "U+2067" to keep it).</summary>
    const string OldDefaultMarkerHex = "2067";

    /// <summary>Process names (without .exe) that are treated as Zoom.</summary>
    public string[] ProcessNames { get; set; } = ["Zoom"];

    /// <summary>
    /// Regex matched (case-insensitive) against the accessible name of the focused text box.
    /// Zoom names its chat input "Message to &lt;chat&gt;, ..." – this keeps the marker out of search boxes etc.
    /// </summary>
    public string ChatNamePattern { get; set; } = "message";

    /// <summary>Maximum time to wait for UI Automation when deciding whether to insert.</summary>
    public int UiaTimeoutMs { get; set; } = 250;

    public bool DebugLog { get; set; } = false;

    /// <summary>Testing only: also react to synthetic (SendInput) keystrokes from other programs.</summary>
    public bool ProcessInjectedInput { get; set; } = false;

    [JsonIgnore]
    public char MarkerChar =>
        int.TryParse(MarkerHex.Trim().Replace("U+", "", StringComparison.OrdinalIgnoreCase),
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp) && cp is > 0 and <= 0xFFFF
            ? (char)cp
            : (char)0x2068; // FIRST STRONG ISOLATE

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZoomBiDi");

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [JsonIgnore]
    public string FilePath { get; private set; } = Path.Combine(DefaultDirectory, "settings.json");

    public static Settings Load(string? path = null)
    {
        path ??= Path.Combine(DefaultDirectory, "settings.json");
        Settings s;
        try
        {
            s = File.Exists(path)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions) ?? new Settings()
                : new Settings();
        }
        catch
        {
            s = new Settings();
        }
        s.FilePath = path;
        if (!File.Exists(path))
        {
            s.Save();
        }
        else if (string.Equals(s.MarkerHex?.Trim(), OldDefaultMarkerHex, StringComparison.Ordinal))
        {
            s.MarkerHex = new Settings().MarkerHex; // old default: move to the new one
            s.Save();
        }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Settings are a convenience; never crash over them.
        }
    }
}
