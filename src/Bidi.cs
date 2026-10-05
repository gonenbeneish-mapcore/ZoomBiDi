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

    // NEL, LINE SEPARATOR, PARAGRAPH SEPARATOR written numerically (they are newlines inside C# source).
    public static bool IsLineBreak(char c) => c is '\n' or '\r' or '\v' or '\f' or (char)0x85 or (char)0x2028 or (char)0x2029;
}
