using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace ZoomBiDi;

internal enum CaretState
{
    /// <summary>Focused element is a chat box and nothing precedes the caret on its line.</summary>
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
    IUIAutomation? _uia;

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
            var com = new List<object>();
            try
            {
                _uia ??= (IUIAutomation)new CUIAutomation();
                result = Inspect(req.ProcessId, com, out detail);
            }
            catch (Exception ex)
            {
                result = CaretState.Unknown;
                detail = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                // Release remote UIA objects right away instead of waiting for the GC.
                foreach (var o in com) Marshal.FinalReleaseComObject(o);
            }
            if (req.Callback is null) continue;
            if (_log.Enabled) _log.Info($"check #{req.Id}: {result} in {sw.ElapsedMilliseconds} ms ({detail})");
            req.Callback(req.Id, result);
        }
    }

    static T Track<T>(List<object> com, T o) where T : class
    {
        com.Add(o);
        return o;
    }

    CaretState Inspect(uint processId, List<object> com, out string detail)
    {
        var el = _uia!.GetFocusedElement();
        if (el is null) { detail = "no focused element"; return CaretState.NotChat; }
        Track(com, el);

        // Zoom's chat is an embedded WebView2: the text box lives in a child msedgewebview2.exe process.
        int pid = el.get_CurrentProcessId();
        if (!ProcessTree.IsSameOrDescendant((uint)pid, processId))
        {
            detail = $"focus in unrelated pid {pid}";
            return CaretState.NotChat;
        }

        int type = el.get_CurrentControlType();
        var name = el.get_CurrentName() ?? "";
        if (type != Uia.UIA_EditControlTypeId && type != Uia.UIA_DocumentControlTypeId)
        {
            detail = $"focus is control type {type} '{name}'";
            return CaretState.NotChat;
        }
        if (el.GetCurrentPropertyValue(Uia.UIA_IsPasswordPropertyId) is true) { detail = "password box"; return CaretState.NotChat; }
        if (!_namePattern().IsMatch(name)) { detail = $"name '{name}' does not match"; return CaretState.NotChat; }

        if (el.GetCurrentPattern(Uia.UIA_TextPatternId) is IUIAutomationTextPattern tp)
        {
            Track(com, tp);
            var selection = Track(com, tp.GetSelection());
            if (selection.get_Length() > 0)
                return Evaluate(tp, Track(com, selection.GetElement(0)), com, out detail);
        }

        if (el.GetCurrentPattern(Uia.UIA_ValuePatternId) is IUIAutomationValuePattern vp)
        {
            Track(com, vp);
            // No caret information: only an empty box is known to be "start of line".
            var value = vp.get_CurrentValue() ?? "";
            detail = $"value-only, len={value.Length}";
            return value.Length == 0 ? CaretState.LineStart : CaretState.MidLine;
        }

        detail = "no text/value pattern";
        return CaretState.NotChat;
    }

    static CaretState Evaluate(IUIAutomationTextPattern tp, IUIAutomationTextRange caret, List<object> com, out string detail)
    {
        // 1) Text from the start of the box to the caret; look at what follows the last hard line break.
        var before = Track(com, Track(com, tp.get_DocumentRange()).Clone());
        before.MoveEndpointByRange(Uia.TextPatternRangeEndpoint_End, caret, Uia.TextPatternRangeEndpoint_Start);
        var text = before.GetText(-1) ?? "";

        int lineStart = text.Length;
        while (lineStart > 0 && !Bidi.IsLineBreak(text[lineStart - 1])) lineStart--;
        var prefix = text[lineStart..];

        if (prefix.Length == 0)
        {
            // Caret at the start of a line that already begins with a mark (e.g. Home on a fixed line):
            // step past the mark so the new text goes after it and the mark stays first.
            var next = Track(com, caret.Clone());
            next.MoveEndpointByUnit(Uia.TextPatternRangeEndpoint_End, Uia.TextUnit_Character, 1);
            var after = next.GetText(1) ?? "";
            if (after.Length > 0 && Bidi.IsDirectionMark(after[0]))
            {
                var moved = Track(com, caret.Clone());
                moved.Move(Uia.TextUnit_Character, 1);
                moved.Select();
                detail = "caret was before the line's mark; moved past it";
                return CaretState.MidLine;
            }
            detail = "nothing before the caret on this line";
            return CaretState.LineStart;
        }
        // 2) Chromium reports the caret on a new, empty last line as sitting *before* the trailing "\n",
        //    so (1) still sees the previous line. Detect "caret is on an empty line" via the Line unit
        //    (before looking at the prefix, which here belongs to the previous line).
        var line = Track(com, caret.Clone());
        line.ExpandToEnclosingUnit(Uia.TextUnit_Line);
        var lineText = line.GetText(64) ?? "";
        if (lineText.Length == 0 || IsAllLineBreaks(lineText))
        {
            detail = "caret on an empty line";
            return CaretState.LineStart;
        }

        detail = prefix.IndexOfAny(Bidi.DirectionMarks) >= 0
            ? "line already has a direction mark"
            : $"line has content before the caret ('{Escape(prefix)}')";
        return CaretState.MidLine;
    }

    static bool IsAllLineBreaks(string s)
    {
        foreach (var c in s)
            if (!Bidi.IsLineBreak(c)) return false;
        return true;
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
