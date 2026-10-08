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
    sealed record Request(uint ProcessId, int Id, bool MayUseLastChat, bool Arrival, int SettleMs,
        Action<int, CaretState>? Callback, long Created);

    const int MaxTransientRetries = 4;
    const int TransientRetryDelayMs = 25;

    readonly BlockingCollection<Request> _queue = new();
    /// <summary>What each finished check found out about the chat box (request id → info), collected by the hook thread.</summary>
    readonly ConcurrentDictionary<int, ChatInfo> _chatInfo = new();

    /// <summary>
    /// The chat box a check found: <paramref name="Key"/> identifies it ("Message to …" label). <paramref name="Live"/>:
    /// "empty or not" is up to date (read from Zoom's Send button, which is disabled while the box is empty), not
    /// from the box's name, which only changes when the chat is opened.
    /// </summary>
    /// <param name="CaretLineEmpty">
    /// The line the caret is on has no text (from Zoom's editor, which shows its lines and, through its hidden input
    /// box, where the caret is); null if that couldn't be worked out.
    /// </param>
    public readonly record struct ChatInfo(string Key, bool Live, bool? CaretLineEmpty = null);

    /// <summary>What a check found about the chat box, or null. Call once per check result.</summary>
    public ChatInfo? TakeChatInfo(int id) => _chatInfo.TryRemove(id, out var info) ? info : null;
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
    /// <param name="arrival">
    /// The user may have just arrived in the box (click, window switch): also find out whether the box and the line
    /// the caret is on are empty. (After Enter and the like, the keys already say that a line starts.)
    /// </param>
    /// <param name="settleMs">
    /// Wait this long first: keys we just replayed have passed our hook but Zoom may not have processed them yet.
    /// </param>
    public void Query(uint processId, int id, bool mayUseLastChat, bool arrival, int settleMs, Action<int, CaretState> callback) =>
        _queue.Add(new Request(processId, id, mayUseLastChat, arrival, settleMs, callback, Environment.TickCount64));

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
            ChatInfo? chatInfo = null;
            int attempt = 0;
            while (true)
            {
                bool transient = false;
                var com = new List<object>();
                try
                {
                    _uia ??= (IUIAutomation)new CUIAutomation();
                    result = Inspect(req.ProcessId, req.MayUseLastChat, req.Arrival, com, out detail, out transient, out chatInfo);
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
            if (chatInfo is not null) _chatInfo[req.Id] = chatInfo.Value;
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
    CaretState Inspect(uint processId, bool mayUseLastChat, bool arrival, List<object> com, out string detail, out bool transient,
        out ChatInfo? chatKey)
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
        return EvaluateChat(el, name, arrival, com, out detail, out chatKey);
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
        out ChatInfo? chatKey)
    {
        chatKey = null;
        if (_lastChat is null || _lastChatOwner != processId) { detail = why; return CaretState.NotChat; }
        try
        {
            var state = EvaluateChat(_lastChat, _lastChat.get_CurrentName() ?? "", false, com, out var d, out chatKey);
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
    /// Is the chat box empty? Zoom's Send button says so, up to date (disabled while the box is empty). Without it,
    /// for Zoom's box this comes from the name (label + the draft as it was when the chat was opened - it isn't
    /// updated while typing), so the hook only trusts it when the user just arrived in a chat not typed into yet.
    /// </summary>
    CaretState EvaluateChat(IUIAutomationElement el, string name, bool arrival, List<object> com, out string detail, out ChatInfo? info)
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

        var label = text.Trim();
        if (label.Length > 0 && name.StartsWith(label, StringComparison.Ordinal))
        {
            // Zoom: the "text" is just the label, and the name is the label followed by the draft.
            var chatKey = label;
            bool? caretLineEmpty = null, sendEnabled = null;
            string extra = "";
            if (arrival)
            {
                // Zoom's composer: the box sits in Zoom's editor ("zm-doc-sdk-editor"), whose lines show the text, and
                // the Send button is in the container a level or two above that. Extra information only: if Zoom is
                // redrawing and this fails, the check still stands.
                try
                {
                    var ancestors = Ancestors(el, com);
                    int editor = ancestors.FindIndex(a => HasClass(a, "zm-doc-sdk-editor"));
                    if (editor >= 0)
                    {
                        caretLineEmpty = CaretLineEmpty(el, ancestors.GetRange(0, editor + 1), com);
                        sendEnabled = SendButtonEnabled(
                            ancestors.GetRange(editor + 1, Math.Min(MaxSendButtonLevels, ancestors.Count - editor - 1)), com);
                    }
                }
                catch (Exception ex)
                {
                    extra = $", editor not readable ({ex.GetType().Name})";
                }
                extra += caretLineEmpty switch { true => ", caret on an empty line", false => ", caret on a line with text", _ => "" };
            }
            if (sendEnabled is bool enabled)
            {
                info = new ChatInfo(chatKey, Live: true, caretLineEmpty);
                detail = $"Zoom-style box, Send button {(enabled ? "enabled" : "disabled")}{extra}";
                return enabled ? CaretState.ChatNotEmpty : CaretState.ChatEmpty;
            }
            var content = name[label.Length..].Trim().TrimEnd(',').Trim();
            info = new ChatInfo(chatKey, Live: false, caretLineEmpty);
            detail = $"Zoom-style box, draft of {content.Length} char(s) when opened{extra}";
            return IsBlank(content) ? CaretState.ChatEmpty : CaretState.ChatNotEmpty;
        }

        // A box that exposes its text: that is up to date.
        info = new ChatInfo(name, Live: true);
        detail = $"text of {text.Length} char(s)";
        return IsBlank(text) ? CaretState.ChatEmpty : CaretState.ChatNotEmpty;
    }

    /// <summary>Only spaces, line breaks and zero-width spaces (Zoom's empty line). A lone mark counts as content.</summary>
    static bool IsBlank(string s)
    {
        foreach (var c in s)
            if (!char.IsWhiteSpace(c) && !Bidi.IsLineBreak(c) && c != (char)0x200B) return false;
        return true;
    }

    const int MaxAncestorLevels = 8;
    /// <summary>How far above Zoom's editor container the Send button is looked for.</summary>
    const int MaxSendButtonLevels = 3;
    // Inspector thread only.
    IUIAutomationTreeWalker? _walker, _rawWalker;
    IUIAutomationCondition? _sendButtonCondition;

    /// <summary>The chat box's ancestors, nearest first (up to <see cref="MaxAncestorLevels"/>).</summary>
    List<IUIAutomationElement> Ancestors(IUIAutomationElement box, List<object> com)
    {
        _walker ??= _uia!.get_ControlViewWalker();
        var list = new List<IUIAutomationElement>(MaxAncestorLevels);
        var current = box;
        while (list.Count < MaxAncestorLevels && _walker.GetParentElement(current) is { } parent)
        {
            current = Track(com, parent);
            list.Add(current);
        }
        return list;
    }

    /// <summary>
    /// Is the Send button next to this chat box enabled (the box has content)? Null if there's no such button.
    /// Zoom's composer puts the box and its Send button in one container, a few levels above the box. Searching
    /// from the nearest ancestor outwards finds this box's own button (the containers below it are tiny).
    /// </summary>
    bool? SendButtonEnabled(IEnumerable<IUIAutomationElement> ancestors, List<object> com)
    {
        _sendButtonCondition ??= _uia!.CreateAndCondition(
            _uia.CreatePropertyCondition(Uia.UIA_ControlTypePropertyId, Uia.UIA_ButtonControlTypeId),
            _uia.CreatePropertyCondition(Uia.UIA_NamePropertyId, "Send"));
        foreach (var ancestor in ancestors)
            if (ancestor.FindFirst(Uia.TreeScope_Descendants, _sendButtonCondition) is { } button)
                return Track(com, button).GetCurrentPropertyValue(Uia.UIA_IsEnabledPropertyId) is true;
        return null;
    }

    /// <summary>
    /// Is the line the caret is on empty? Zoom's editor shows each line as a "zm-paragraph-block" with its text,
    /// next to the hidden input box that receives the keys - and that box sits where the caret is, so its height
    /// tells the line. Null if this can't be worked out (not Zoom's editor, or the caret in a list or quote).
    /// </summary>
    bool? CaretLineEmpty(IUIAutomationElement box, List<IUIAutomationElement> ancestors, List<object> com)
    {
        if (Rect(box) is not { } caret) return null;
        double caretY = caret.Top + Math.Min(caret.Height, 24) / 2;
        // The line elements are plain containers, which aren't in UI Automation's control view (so FindAll doesn't
        // see them): walk the raw tree.
        _rawWalker ??= _uia!.get_RawViewWalker();

        for (int level = 0; level < ancestors.Count; level++)
        {
            var paragraphs = new List<IUIAutomationElement>();
            CollectParagraphs(ancestors[level], 0, paragraphs, com);
            if (paragraphs.Count == 0) continue;
            foreach (var p in paragraphs)
                if (Rect(p) is { } r && caretY >= r.Top && caretY < r.Top + r.Height)
                    return IsBlank(TextOf(p, 0, com));
            return null; // the editor, but the caret isn't on a plain line
        }
        return null;
    }

    const int MaxEditorDepth = 5, MaxParagraphs = 200;

    static bool HasClass(IUIAutomationElement el, string cls) =>
        el.GetCurrentPropertyValue(Uia.UIA_ClassNamePropertyId) is string classes
        && Array.IndexOf(classes.Split(' '), cls) >= 0;

    void CollectParagraphs(IUIAutomationElement el, int depth, List<IUIAutomationElement> found, List<object> com)
    {
        if (depth >= MaxEditorDepth) return;
        for (var c = _rawWalker!.GetFirstChildElement(el); c is not null && found.Count < MaxParagraphs; c = _rawWalker.GetNextSiblingElement(c))
        {
            Track(com, c);
            if (HasClass(c, "zm-paragraph-block")) found.Add(c);
            else
                CollectParagraphs(c, depth + 1, found, com);
        }
    }

    /// <summary>The text of a line: the names of the text elements in it.</summary>
    string TextOf(IUIAutomationElement el, int depth, List<object> com)
    {
        if (depth >= 3) return "";
        var sb = new System.Text.StringBuilder();
        for (var c = _rawWalker!.GetFirstChildElement(el); c is not null; c = _rawWalker.GetNextSiblingElement(c))
        {
            Track(com, c);
            sb.Append(c.get_CurrentControlType() == Uia.UIA_TextControlTypeId ? c.get_CurrentName() : TextOf(c, depth + 1, com));
        }
        return sb.ToString();
    }

    readonly record struct Bounds(double Left, double Top, double Width, double Height);

    static Bounds? Rect(IUIAutomationElement el) =>
        el.GetCurrentPropertyValue(Uia.UIA_BoundingRectanglePropertyId) is double[] { Length: 4 } r
        && double.IsFinite(r[1]) && double.IsFinite(r[3]) && r[3] > 0
            ? new Bounds(r[0], r[1], r[2], r[3])
            : null;

    public void Dispose() => _queue.CompleteAdding();
}
