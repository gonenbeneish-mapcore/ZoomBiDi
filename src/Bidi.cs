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

    const char LRE = (char)0x202A, RLE = (char)0x202B, PDF = (char)0x202C, LRO = (char)0x202D, RLO = (char)0x202E;
    const char LRI = (char)0x2066, RLI = (char)0x2067, FSI = (char)0x2068, PDI = (char)0x2069;
    const int MaxOpenMarks = 16;

    /// <summary>
    /// The characters that close the isolates and embeddings <paramref name="text"/> opens and leaves open
    /// (PDI for LRI/RLI/FSI, PDF for LRE/RLE/LRO/RLO), innermost first. Empty if it leaves nothing open - or opens
    /// more than <see cref="MaxOpenMarks"/>, which isn't worth repairing. Text copied from a chat can carry such a
    /// mark (the one that started the next line, say); typed after it, a line would take that mark's direction.
    /// </summary>
    public static string Closers(System.ReadOnlySpan<char> text)
    {
        // true = isolate, false = embedding/override; as UAX #9 pairs them (BD8-BD9, X5a-X7).
        var open = new System.Collections.Generic.List<bool>();
        foreach (char c in text)
        {
            if (IsLineBreak(c)) open.Clear(); // a line's marks end with the line
            switch (c)
            {
                case LRI or RLI or FSI:
                    open.Add(true);
                    break;
                case LRE or RLE or LRO or RLO:
                    open.Add(false);
                    break;
                case PDF: // closes the last embedding, unless an isolate was opened after it
                    if (open.Count > 0 && !open[^1]) open.RemoveAt(open.Count - 1);
                    break;
                case PDI: // closes the last isolate, and the embeddings opened inside it
                    int isolate = open.LastIndexOf(true);
                    if (isolate >= 0) open.RemoveRange(isolate, open.Count - isolate);
                    break;
            }
            if (open.Count > MaxOpenMarks) return "";
        }
        var closers = new char[open.Count];
        for (int i = 0; i < closers.Length; i++) closers[i] = open[open.Count - 1 - i] ? PDI : PDF;
        return new string(closers);
    }

    // NEL, LINE SEPARATOR, PARAGRAPH SEPARATOR written numerically (they are newlines inside C# source).
    public static bool IsLineBreak(char c) => c is '\n' or '\r' or '\v' or '\f' or (char)0x85 or (char)0x2028 or (char)0x2029;
}
