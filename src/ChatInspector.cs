using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace ZoomBiDi;

internal enum CaretState
{
    /// <summary>Focused element is a chat box and the caret has only neutral characters before it on its line.</summary>
    LineStart,
    /// <summary>Focused element is a chat box, but the line already has content (or a direction mark).</summary>
    MidLine,
    /// <summary>Focused element is not a recognised chat box.</summary>
    NotChat,
    /// <summary>UI Automation failed.</summary>
    Unknown,
}

/// <summary>
/// Answers "is the caret at the start of a line in a Zoom chat box?" using UI Automation, asynchronously,
/// on its own MTA thread. The keyboard hook must never wait for this: the target app may itself be waiting
/// for keyboard input to flow while it answers us.
/// </summary>
internal sealed class ChatInspector : IDisposable
{
    sealed record Request(uint ProcessId, int Id, Action<int, CaretState>? Callback, long Created);

    readonly BlockingCollection<Request> _queue = new();
    readonly Thread _thread;
    readonly Logger _log;
    readonly Func<Regex> _namePattern;

    public ChatInspector(Logger log, Func<Regex> namePattern)
    {
        _log = log;
        _namePattern = namePattern;
        _thread = new Thread(Worker) { IsBackground = true, Name = "UIA inspector" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Queues a check; <paramref name="callback"/> runs on the inspector thread.</summary>
    public void Query(uint processId, int id, Action<int, CaretState> callback) =>
        _queue.Add(new Request(processId, id, callback, Environment.TickCount64));

    /// <summary>Warms up the UIA connection (the first call into a Chromium-based window is slow).</summary>
    public void Prime(uint processId) => _queue.Add(new Request(processId, 0, null, Environment.TickCount64));

    void Worker()
    {
        foreach (var req in _queue.GetConsumingEnumerable())
        {
            // The hook gives up long before this; don't waste time on stale requests.
            if (Environment.TickCount64 - req.Created > 2000) continue;

            var sw = Stopwatch.StartNew();
            CaretState result;
            string detail;
            try
            {
                result = Inspect(req.ProcessId, out detail);
            }
            catch (Exception ex)
            {
                result = CaretState.Unknown;
                detail = ex.GetType().Name + ": " + ex.Message;
            }
            if (req.Callback is null) continue;
            _log.Info($"check #{req.Id}: {result} in {sw.ElapsedMilliseconds} ms ({detail})");
            req.Callback(req.Id, result);
        }
    }

    CaretState Inspect(uint processId, out string detail)
    {
        var el = AutomationElement.FocusedElement;
        if (el is null) { detail = "no focused element"; return CaretState.NotChat; }

        var cur = el.Current;
        // Zoom's chat is an embedded WebView2: the text box lives in a child msedgewebview2.exe process.
        if (!ProcessTree.IsSameOrDescendant((uint)cur.ProcessId, processId))
        {
            detail = $"focus in unrelated pid {cur.ProcessId}";
            return CaretState.NotChat;
        }

        var type = cur.ControlType;
        if (type != ControlType.Edit && type != ControlType.Document)
        {
            detail = $"focus is {type.ProgrammaticName} '{cur.Name}'";
            return CaretState.NotChat;
        }
        if (cur.IsPassword) { detail = "password box"; return CaretState.NotChat; }

        var name = cur.Name ?? "";
        if (!_namePattern().IsMatch(name)) { detail = $"name '{name}' does not match"; return CaretState.NotChat; }

        if (el.TryGetCurrentPattern(TextPattern.Pattern, out var tpObj) && tpObj is TextPattern tp)
        {
            var selection = tp.GetSelection();
            if (selection.Length > 0)
                return Evaluate(tp, selection[0], out detail);
        }

        if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj) && vpObj is ValuePattern vp)
        {
            // No caret information: only an empty box is known to be "start of line".
            var value = vp.Current.Value ?? "";
            detail = $"value-only, len={value.Length}";
            return value.Length == 0 ? CaretState.LineStart : CaretState.MidLine;
        }

        detail = "no text/value pattern";
        return CaretState.NotChat;
    }

    static CaretState Evaluate(TextPattern tp, TextPatternRange caret, out string detail)
    {
        // 1) Text from the start of the box to the caret; look at what follows the last hard line break.
        var before = tp.DocumentRange.Clone();
        before.MoveEndpointByRange(TextPatternRangeEndpoint.End, caret, TextPatternRangeEndpoint.Start);
        var text = before.GetText(-1) ?? "";

        int lineStart = text.Length;
        while (lineStart > 0 && !Bidi.IsLineBreak(text[lineStart - 1])) lineStart--;

        var verdict = ClassifyPrefix(text, lineStart, out var firstNonNeutral);
        if (verdict == CaretState.LineStart)
        {
            detail = $"line prefix '{Escape(text[lineStart..])}' is neutral";
            return CaretState.LineStart;
        }
        if (verdict == CaretState.MidLine && Bidi.Classify(firstNonNeutral) == CharClass.DirectionMark)
        {
            detail = "line already has a direction mark";
            return CaretState.MidLine;
        }

        // 2) Chromium reports the caret on a new, empty last line as sitting *before* the trailing "\n",
        //    so (1) still sees the previous line. Detect "caret is on an empty line" via the Line unit.
        var line = caret.Clone();
        line.ExpandToEnclosingUnit(TextUnit.Line);
        var lineText = line.GetText(64) ?? "";
        bool emptyLine = lineText.Length == 0 || IsAllLineBreaks(lineText);
        if (emptyLine)
        {
            detail = "caret on an empty line";
            return CaretState.LineStart;
        }

        detail = $"line prefix '{Escape(text[lineStart..])}' has content";
        return CaretState.MidLine;
    }

    static bool IsAllLineBreaks(string s)
    {
        foreach (var c in s)
            if (!Bidi.IsLineBreak(c)) return false;
        return true;
    }

    static CaretState ClassifyPrefix(string text, int start, out char firstNonNeutral)
    {
        for (int i = start; i < text.Length; i++)
        {
            if (Bidi.Classify(text[i]) != CharClass.Neutral)
            {
                firstNonNeutral = text[i];
                return CaretState.MidLine;
            }
        }
        firstNonNeutral = '\0';
        return CaretState.LineStart;
    }

    static string Escape(string s)
    {
        if (s.Length > 40) s = "…" + s[^40..];
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c < 0x20 || (c >= 0x2000 && c <= 0x206F) ? $"\\u{(int)c:X4}" : c.ToString());
        return sb.ToString();
    }

    public void Dispose() => _queue.CompleteAdding();
}
