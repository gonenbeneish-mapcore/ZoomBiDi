namespace ZoomBiDi;

internal enum CharClass { Neutral, StrongLtr, StrongRtl, DirectionMark }

internal static class Bidi
{
    public static CharClass Classify(char c)
    {
        // Explicit direction marks / embeddings / isolates – if one is already there, leave the line alone.
        if (c is '‎' or '‏' or '؜' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩'))
            return CharClass.DirectionMark;

        if (c is (>= '֐' and <= 'ࣿ')       // Hebrew, Arabic, Syriac, Thaana, NKo, Samaritan, ...
              or (>= 'יִ' and <= '﷿')        // Hebrew + Arabic presentation forms A
              or (>= 'ﹰ' and <= '﻿'))       // Arabic presentation forms B
            return char.IsLetter(c) ? CharClass.StrongRtl : CharClass.Neutral;

        // U+FFFC = embedded object (e.g. an @mention chip) – treat as content.
        if (char.IsLetter(c) || c == '￼') return CharClass.StrongLtr;

        return CharClass.Neutral;
    }

    // NEL, LINE SEPARATOR, PARAGRAPH SEPARATOR written numerically (they are newlines inside C# source).
    public static bool IsLineBreak(char c) => c is '\n' or '\r' or '\v' or '\f' or (char)0x85 or (char)0x2028 or (char)0x2029;
}
