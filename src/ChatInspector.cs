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
    /// <summary>Focused element is a chat box, and the box is empty.</summary>
    ChatEmpty,
    /// <summary>Focused element is a chat box with some text in it.</summary>
    ChatNotEmpty,
    /// <summary>Focused element is not a recognised chat box.</summary>
    NotChat,
    /// <summary>UI Automation failed.</summary>
    Unknown,
}

/// <summary>
/// Answers "is the focus in a Zoom chat box, and is that box empty?" using UI Automation, asynchronously, on its
/// own MTA thread. The keyboard hook must never wait for this: the target app may itself be waiting for keyboard
/// input to flow while it answers us.
///
/// Zoom doesn't expose the caret or the message text the usual way: its text box's Text pattern returns only the
/// box's label ("Message to …"), whatever has been typed. Its accessible *name*, however, is that label followed
/// by the draft. So "empty" = the name has nothing beyond the label. Where the caret is (start of a line or not)
/// is worked out by <see cref="KeyboardMonitor"/> from the keys themselves.
/// </summary>
internal sealed class ChatInspector : IDisposable
{
    sealed record Request(uint ProcessId, int Id, bool MayUseLastChat, int SettleMs,
        Action<int, CaretState>? Callback, long Created);

    const int MaxTransientRetries = 4;
    const int TransientRetryDelayMs = 25;

    readonly BlockingCollection<Request> _queue = new();
    /// <summary>Which chat box each finished check was about (request id → key), collected by the hook thread.</summary>
    readonly ConcurrentDictionary<int, string> _chatKeys = new();

    /// <summary>
    /// The identity of the chat box a check found ("Message to …" label), or null. Call once per check result.
    /// </summary>
    public string? TakeChatKey(int id) => _chatKeys.TryRemove(id, out var key) ? key : null;
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
    /// <param name="mayUseLastChat">
    /// Nothing that could move focus (click, Tab, window switch) happened since the last check, so if focus is
    /// momentarily on Zoom's menu bar the chat box from the last check can be asked instead.
    /// </param>
    /// <param name="settleMs">
    /// Wait this long first: keys we just replayed have passed our hook but Zoom may not have processed them yet.
    /// </param>
    public void Query(uint processId, int id, bool mayUseLastChat, int settleMs, Action<int, CaretState> callback) =>
        _queue.Add(new Request(processId, id, mayUseLastChat, settleMs, callback, Environment.TickCount64));

    /// <summary>Warms up the UIA connection (the first call into a Chromium-based window is slow).</summary>
    public void Prime(uint processId) => _queue.Add(new Request(processId, 0, false, 0, null, Environment.TickCount64));

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
            string? chatKey = null;
            int attempt = 0;
            while (true)
            {
                bool transient = false;
                var com = new List<object>();
                try
                {
                    _uia ??= (IUIAutomation)new CUIAutomation();
                    result = Inspect(req.ProcessId, req.MayUseLastChat, com, out detail, out transient, out chatKey);
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

                // Focus can be somewhere else for a moment: ask again shortly instead of giving up on the line.
                if (!transient || req.Callback is null || attempt >= MaxTransientRetries) break;
                attempt++;
                Thread.Sleep(TransientRetryDelayMs);
            }
            if (req.Callback is null) continue;
            if (chatKey is not null) _chatKeys[req.Id] = chatKey;
            if (_log.Enabled)
                _log.Info($"check #{req.Id}: {result} in {sw.ElapsedMilliseconds} ms ({detail})" +
                    $"{(attempt > 0 ? $" after {attempt} focus retr{(attempt == 1 ? "y" : "ies")}" : "")}");
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
    CaretState Inspect(uint processId, bool mayUseLastChat, List<object> com, out string detail, out bool transient,
        out string? chatKey)
    {
        transient = true;
        chatKey = null;
        var el = _uia!.GetFocusedElement();
        if (el is null)
        {
            if (mayUseLastChat) return FromLastChat(processId, "no focused element", com, out detail, ref transient, out chatKey);
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
            var why = $"focus is control type {type}";
            // Switching keyboard language with Alt+Shift can leave focus "on" Zoom's menu bar until the next key
            // arrives - which is the very key we're holding. If nothing could have moved focus since the last
            // check (no click, Tab, window switch), ask the chat box from that check directly.
            if (mayUseLastChat && type is Uia.UIA_MenuBarControlTypeId or Uia.UIA_MenuControlTypeId or Uia.UIA_MenuItemControlTypeId)
                return FromLastChat(processId, why, com, out detail, ref transient, out chatKey);
            detail = why;
            return CaretState.NotChat;
        }
        transient = false;
        if (el.GetCurrentPropertyValue(Uia.UIA_IsPasswordPropertyId) is true || !_namePattern().IsMatch(name))
        {
            ForgetLastChat(); // the user is in some other text box now
            detail = "a text box that is not a chat box";
            return CaretState.NotChat;
        }

        RememberChat(el, processId, com);
        return EvaluateChat(el, name, com, out detail, out chatKey);
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

    CaretState FromLastChat(uint processId, string why, List<object> com, out string detail, ref bool transient,
        out string? chatKey)
    {
        chatKey = null;
        if (_lastChat is null || _lastChatOwner != processId) { detail = why; return CaretState.NotChat; }
        try
        {
            var state = EvaluateChat(_lastChat, _lastChat.get_CurrentName() ?? "", com, out var d, out chatKey);
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

    /// <summary>
    /// Does the chat box look empty? For Zoom's box this comes from the name (label + the draft as it was when the
    /// chat was opened - it isn't updated while typing), so the hook only trusts it when the user just arrived in
    /// the chat. <paramref name="chatKey"/> identifies the chat (Zoom's "Message to …" label).
    /// </summary>
    static CaretState EvaluateChat(IUIAutomationElement el, string name, List<object> com, out string detail, out string chatKey)
    {
        string? text = null;
        if (el.GetCurrentPattern(Uia.UIA_TextPatternId) is IUIAutomationTextPattern tp)
        {
            Track(com, tp);
            text = Track(com, tp.get_DocumentRange()).GetText(4096);
        }
        else if (el.GetCurrentPattern(Uia.UIA_ValuePatternId) is IUIAutomationValuePattern vp)
        {
            Track(com, vp);
            text = vp.get_CurrentValue();
        }
        text ??= "";

        string content;
        var label = text.Trim();
        if (label.Length > 0 && name.StartsWith(label, StringComparison.Ordinal))
        {
            // Zoom: the "text" is just the label, and the name is the label followed by the draft.
            content = name[label.Length..].Trim().TrimEnd(',').Trim();
            chatKey = label;
            detail = $"Zoom-style box, draft of {content.Length} char(s)";
        }
        else
        {
            content = text;
            chatKey = name;
            detail = $"text of {content.Length} char(s)";
        }

        bool empty = true;
        foreach (var c in content)
            if (!char.IsWhiteSpace(c) && !Bidi.IsLineBreak(c)) { empty = false; break; } // a lone mark counts as content
        return empty ? CaretState.ChatEmpty : CaretState.ChatNotEmpty;
    }

    public void Dispose() => _queue.CompleteAdding();
}
