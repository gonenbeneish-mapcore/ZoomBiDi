namespace ZoomBiDi;

internal static class Bidi
{
    /// <summary>
    /// Explicit direction marks, embeddings and isolates (LRM, RLM, ALM, LRE…RLO, LRI…PDI).
    /// If a line already has one, it is left alone.
    /// </summary>
    public static readonly char[] DirectionMarks =
    [
        (char)0x200E, (char)0x200F, (char)0x061C,
        (char)0x202A, (char)0x202B, (char)0x202C, (char)0x202D, (char)0x202E,
        (char)0x2066, (char)0x2067, (char)0x2068, (char)0x2069,
    ];

    public static bool IsDirectionMark(char c) => System.Array.IndexOf(DirectionMarks, c) >= 0;

    /// <summary>A letter, or U+FFFC (an embedded object such as an @mention chip, which counts as content).</summary>
    public static bool IsLetterOrObject(char c) => char.IsLetter(c) || c == (char)0xFFFC;

    /// <summary>A right-to-left letter: Hebrew, Arabic, Syriac, Thaana, NKo, Samaritan… and their presentation forms.</summary>
    public static bool IsRtlLetter(char c) =>
        char.IsLetter(c) && c is (>= (char)0x0590 and <= (char)0x08FF) or (>= (char)0xFB1D and <= (char)0xFDFF) or (>= (char)0xFE70 and <= (char)0xFEFF);

    // NEL, LINE SEPARATOR, PARAGRAPH SEPARATOR written numerically (they are newlines inside C# source).
    public static bool IsLineBreak(char c) => c is '\n' or '\r' or '\v' or '\f' or (char)0x85 or (char)0x2028 or (char)0x2029;
}
