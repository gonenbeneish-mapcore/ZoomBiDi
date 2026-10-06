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
    /// <summary>
    /// Focused element is a chat box; the line already has content before the caret (e.g. its mark), but no
    /// letters yet - e.g. after Backspace, or Home on a fixed line (the caret is then moved past the mark).
    /// </summary>
    MidLineNoLetters,
    /// <summary>Focused element is a chat box, and the line already has letters before the caret.</summary>
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
    sealed record Request(uint ProcessId, int Id, bool AfterEnter, bool MayUseLastChat, int SettleMs,
        Action<int, CaretState>? Callback, long Created);

    const int MaxTransientRetries = 4;
    const int TransientRetryDelayMs = 25;
    /// <summary>
    /// Zoom's accessibility info lags its screen by a few ms: right after Enter it may still report the previous
    /// line. A "mid-line" answer right after Enter is therefore re-checked shortly (a real mid-line after Enter,
    /// e.g. an autocomplete pick, just costs this short wait).
    /// </summary>
    const int MaxAfterEnterRechecks = 3;
    const int AfterEnterRecheckDelayMs = 30;

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
    /// <param name="afterEnter">The last caret-moving key was Enter.</param>
    /// <param name="mayUseLastChat">
    /// Nothing that could move focus (click, Tab, window switch) happened since the last check, so if focus is
    /// momentarily on Zoom's menu bar the chat box from the last check can be asked instead.
    /// </param>
    /// <param name="settleMs">
    /// Wait this long first: keys we just replayed have passed our hook but Zoom may not have processed them yet.
    /// </param>
    public void Query(uint processId, int id, bool afterEnter, bool mayUseLastChat, int settleMs, Action<int, CaretState> callback) =>
        _queue.Add(new Request(processId, id, afterEnter, mayUseLastChat, settleMs, callback, Environment.TickCount64));

    /// <summary>Warms up the UIA connection (the first call into a Chromium-based window is slow).</summary>
    public void Prime(uint processId) => _queue.Add(new Request(processId, 0, false, false, 0, null, Environment.TickCount64));

    void Worker()
    {
        foreach (var req in _queue.GetConsumingEnumerable())
        {
            // The hook gives up long before this; don't waste time on stale requests.
            if (Environment.TickCount64 - req.Created > 2000) continue;

            var sw = Stopwatch.StartNew();
            if (req.SettleMs > 0) Thread.Sleep(req.SettleMs);
            CaretState result;
            string detail;
            int attempt = 0, enterRechecks = 0;
            while (true)
            {
                bool transient = false;
                var com = new List<object>();
                try
                {
                    _uia ??= (IUIAutomation)new CUIAutomation();
                    result = Inspect(req.ProcessId, req.MayUseLastChat, com, out detail, out transient);
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

                if (req.Callback is null) break;

                // Right after Enter, Zoom may still report the previous line: look again shortly.
                if (result is CaretState.MidLine or CaretState.MidLineNoLetters && req.AfterEnter && enterRechecks < MaxAfterEnterRechecks)
                {
                    enterRechecks++;
                    Thread.Sleep(AfterEnterRecheckDelayMs);
                    continue;
                }

                // Focus can be somewhere else for a moment - e.g. switching keyboard language with Alt+Shift briefly
                // puts it on the window's menu bar. Ask again shortly instead of giving up on the line.
                if (!transient || attempt >= MaxTransientRetries) break;
                attempt++;
                Thread.Sleep(TransientRetryDelayMs);
            }
            if (req.Callback is null) continue;
            if (_log.Enabled)
                _log.Info($"check #{req.Id}: {result} in {sw.ElapsedMilliseconds} ms ({detail})" +
                    $"{(attempt > 0 ? $" after {attempt} focus retr{(attempt == 1 ? "y" : "ies")}" : "")}" +
                    $"{(enterRechecks > 0 ? $" after {enterRechecks} after-Enter recheck(s)" : "")}");
            req.Callback(req.Id, result);
        }
    }

    static T Track<T>(List<object> com, T o) where T : class
    {
        com.Add(o);
        return o;
    }

    /// <param name="transient">
    /// True when focus isn't on any text box at all (menu bar, window, other process) - typically a passing state,
    /// worth asking again. False for a definite answer, including "a text box, but not a chat box".
    /// </param>
    CaretState Inspect(uint processId, bool mayUseLastChat, List<object> com, out string detail, out bool transient)
    {
        transient = true;
        var el = _uia!.GetFocusedElement();
        if (el is null)
        {
            if (mayUseLastChat) return FromLastChat(processId, "no focused element", com, out detail, ref transient);
            detail = "no focused element";
            return CaretState.NotChat;
        }
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
            var why = $"focus is control type {type} '{name}'";
            // Switching keyboard language with Alt+Shift can leave focus "on" Zoom's menu bar until the next key
            // arrives - which is the very key we're holding. If nothing could have moved focus since the last
            // check (no click, Tab, window switch), ask the chat box from that check directly.
            if (mayUseLastChat && type is Uia.UIA_MenuBarControlTypeId or Uia.UIA_MenuControlTypeId or Uia.UIA_MenuItemControlTypeId)
                return FromLastChat(processId, why, com, out detail, ref transient);
            detail = why;
            return CaretState.NotChat;
        }
        transient = false;
        if (el.GetCurrentPropertyValue(Uia.UIA_IsPasswordPropertyId) is true || !_namePattern().IsMatch(name))
        {
            ForgetLastChat(); // the user is in some other text box now
            detail = $"text box '{name}' is not a chat box";
            return CaretState.NotChat;
        }

        RememberChat(el, processId, com);
        return EvaluateChat(el, com, out detail);
    }

    // The chat box from the last successful check (inspector thread only).
    IUIAutomationElement? _lastChat;
    uint _lastChatOwner;

    void RememberChat(IUIAutomationElement el, uint processId, List<object> com)
    {
        com.Remove(el); // keep it alive past this check
        if (!ReferenceEquals(el, _lastChat)) ForgetLastChat();
        _lastChat = el;
        _lastChatOwner = processId;
    }

    void ForgetLastChat()
    {
        if (_lastChat is not null) Marshal.FinalReleaseComObject(_lastChat);
        _lastChat = null;
    }

    CaretState FromLastChat(uint processId, string why, List<object> com, out string detail, ref bool transient)
    {
        if (_lastChat is null || _lastChatOwner != processId) { detail = why; return CaretState.NotChat; }
        try
        {
            var state = EvaluateChat(_lastChat, com, out var d);
            transient = false;
            detail = $"{why}; used the chat box from the last check: {d}";
            return state;
        }
        catch (Exception)
        {
            ForgetLastChat(); // the box is gone (chat closed, window changed)
            detail = why;
            return CaretState.NotChat;
        }
    }

    static CaretState EvaluateChat(IUIAutomationElement el, List<object> com, out string detail)
    {
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
            // A selection that starts at the line start will be replaced by what's typed (mark included):
            // treat it as the start of the line.
            if (!string.IsNullOrEmpty(caret.GetText(1)))
            {
                detail = "selection from the start of the line (will be replaced)";
                return CaretState.LineStart;
            }

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
                return CaretState.MidLineNoLetters;
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

        bool hasLetters = false;
        foreach (var c in prefix)
            if (Bidi.IsLetterOrObject(c)) { hasLetters = true; break; }
        bool hasMark = prefix.IndexOfAny(Bidi.DirectionMarks) >= 0;
        detail = $"line has {(hasMark ? "a mark and " : "")}{(hasLetters ? "letters" : "no letters")} before the caret ('{Escape(prefix)}')";
        return hasLetters ? CaretState.MidLine : CaretState.MidLineNoLetters;
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
