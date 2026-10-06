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
    /// Also align each line by its first letter: Ctrl+Shift+R (right) for Hebrew/Arabic, Ctrl+Shift+L (left) for
    /// other letters. These are Zoom's own alignment shortcuts; a new line inherits the previous line's alignment,
    /// so every line gets one.
    /// </summary>
    public bool AlignLines { get; set; } = true;

    /// <summary>Hex code point of the character to insert. 2067 = RIGHT-TO-LEFT ISOLATE.</summary>
    public string MarkerHex { get; set; } = "2067";

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
            : (char)0x2067; // RIGHT-TO-LEFT ISOLATE

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
        if (!File.Exists(path)) s.Save();
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
