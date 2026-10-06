using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using static ZoomBiDi.Native;

namespace ZoomBiDi;

/// <summary>
/// Watches typing in Zoom. On the first character typed on a line, the keystroke is held back while UI Automation
/// checks whether the caret really is at the start of a line in a chat box; then the held keys are replayed,
/// preceded by the marker character when needed.
///
/// Cost outside Zoom is zero: a cheap foreground-change notification (WinEvent) is all that runs; the low-level
/// keyboard/mouse hooks are only installed while a Zoom window is in the foreground.
///
/// The hook callback itself never blocks (Windows silently removes hooks that are slow, and the target app may
/// need keyboard input to flow before it can answer UI Automation). Everything runs on one dedicated thread:
/// hook callbacks, check results (posted messages) and timeouts (thread timers) are serialised, which keeps the
/// held/replayed keystrokes in their original order.
///
/// Keys typed while a check is running are held too, and examined as they arrive (with the modifier state the
/// held keys themselves produce), so a fast Enter + next line is still handled.
/// </summary>
internal sealed class KeyboardMonitor : IDisposable
{
    /// <summary>Tag on our own SendInput events so the hook lets them through.</summary>
    static readonly UIntPtr OwnInputTag = new(0x5A544446);      // "ZTDF"
    /// <summary>Tag on the last event of a replay, so we know when the replay has passed the hook.</summary>
    static readonly UIntPtr ReplayEndTag = new(0x5A544447);

    const uint WM_APP_CHECK_RESULT = 0x8000 + 1; // WM_APP + 1
    const uint WM_APP_SET_ENABLED = 0x8000 + 2;
    const uint WM_TIMER = 0x0113;
    const uint CheckTimeoutMsDefault = 250;
    const uint DrainTimeoutMs = 1000;
    const uint TrimDelayMs = 3000;
    /// <summary>
    /// Before checking a key that was held behind a replay: replayed keys have passed our hook, but Zoom handles
    /// them (and updates what UI Automation reports) a little later.
    /// </summary>
    const int BacklogSettleMs = 40;
    const int VK_RETURN = 0x0D, VK_TAB = 0x09, VK_F6 = 0x75, VK_ESCAPE = 0x1B, VK_A = 0x41;
    const uint GUI_INMENUMODE = 0x4, GUI_SYSTEMMENUMODE = 0x8, GUI_POPUPMENUMODE = 0x10;

    enum Phase
    {
        /// <summary>Keys pass through.</summary>
        Normal,
        /// <summary>Waiting for the UI Automation check; keys are held.</summary>
        Checking,
        /// <summary>Replay sent; keys are still held until the replay has passed the hook (keeps order).</summary>
        Draining,
    }

    /// <summary>What a key-down means for us, decided when it arrives.</summary>
    enum KeyKind
    {
        /// <summary>Key-up, or a modifier (Shift, Ctrl, Alt, Win, Caps Lock).</summary>
        Neutral,
        /// <summary>Types a character.</summary>
        Printable,
        /// <summary>Might move the caret to another line or box: Enter, Backspace, arrows, shortcuts, Tab, ...</summary>
        Dirty,
    }

    /// <param name="NewLine">Enter/Shift+Enter/Ctrl+Enter that starts a new line (or sends the message).</param>
    /// <param name="SelectAll">Ctrl+A: the next character replaces everything.</param>
    readonly record struct HeldKey(KBDLLHOOKSTRUCT Data, bool Up, KeyKind Kind, char Char, bool NewLine, bool SelectAll, bool FocusMove);

    [Flags]
    enum Mods { None = 0, Shift = 1, Ctrl = 2, Alt = 4, Win = 8 }

    readonly Settings _settings;
    readonly ChatInspector _inspector;
    readonly Logger _log;
    readonly Thread _thread;
    readonly ManualResetEventSlim _started = new(false);
    readonly LowLevelProc _keyboardProc;
    readonly LowLevelProc _mouseProc;
    readonly WinEventProc _foregroundProc;
    readonly Dictionary<uint, bool> _targetPidCache = new();
    readonly char[] _charBuffer = new char[8];
    readonly byte[] _keyState = new byte[256];
    readonly List<HeldKey> _held = new();

    uint _threadId;
    IntPtr _foregroundHook, _keyboardHook, _mouseHook;

    // --- hook-thread state ---
    /// <summary>Something may have moved the caret or changed the box: check before the next character.</summary>
    bool _dirty = true;
    /// <summary>
    /// The last caret-moving key started a new line (Enter, Shift+Enter, Ctrl+Enter), or was Ctrl+A: the next
    /// character starts a line. Zoom doesn't expose the caret position, so this is tracked from the keys.
    /// </summary>
    bool _lineStartPending;
    /// <summary>The word typed so far on this line ("@na" while picking a mention: Enter then picks it, not a new line).</summary>
    readonly System.Text.StringBuilder _word = new();
    /// <summary>Line start known for the check in progress.</summary>
    bool _checkLineStart;
    /// <summary>Focus may have left the chat box since the last check (click, Tab, window switch).</summary>
    bool _focusMayHaveMoved = true;
    bool _enabled;
    bool _zoomInForeground;
    Phase _phase = Phase.Normal;
    int _checkId;
    /// <summary>Modifier state as the held keys leave it (physical state at the start of holding + held events).</summary>
    Mods _heldMods;
    UIntPtr _timer, _trimTimer;
    readonly Stopwatch _checkClock = new();

    volatile bool _enabledPublic;

    public KeyboardMonitor(Settings settings, ChatInspector inspector, Logger log)
    {
        _settings = settings;
        _inspector = inspector;
        _log = log;
        _keyboardProc = KeyboardHook;
        _mouseProc = MouseHook;
        _foregroundProc = OnForegroundChanged;
        _enabled = _enabledPublic = settings.Enabled;
        _thread = new Thread(Run) { IsBackground = true, Name = "Input hook" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public bool Enabled
    {
        get => _enabledPublic;
        set
        {
            _enabledPublic = value;
            if (_threadId != 0) PostThreadMessage(_threadId, WM_APP_SET_ENABLED, value ? 1 : 0, IntPtr.Zero);
        }
    }

    public void Start()
    {
        _thread.Start();
        _started.Wait();
        if (_foregroundHook == IntPtr.Zero)
            throw new InvalidOperationException("Could not watch for window switches (error " + Marshal.GetLastWin32Error() + ").");
    }

    void Run()
    {
        _threadId = GetCurrentThreadId();
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundProc,
            0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _started.Set();
        if (_foregroundHook == IntPtr.Zero) return;

        Guard("startup", UpdateForeground);
        _trimTimer = SetTimer(IntPtr.Zero, UIntPtr.Zero, TrimDelayMs, IntPtr.Zero); // trim after startup

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.hwnd == IntPtr.Zero)
            {
                switch (msg.message)
                {
                    case WM_APP_CHECK_RESULT:
                        Guard("check result", () => OnCheckResult((int)msg.wParam, (CaretState)(int)msg.lParam));
                        continue;
                    case WM_APP_SET_ENABLED:
                        Guard("enable", () =>
                        {
                            _enabled = msg.wParam != IntPtr.Zero;
                            _dirty = true;
                            ApplyHooks();
                        });
                        continue;
                    case WM_TIMER:
                        Guard("timer", () => OnTimer((UIntPtr)(ulong)msg.wParam));
                        continue;
                }
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        // Exiting: never swallow what the user typed.
        if (_held.Count > 0) Send(BuildInputs(false, _held.ToArray(), endTag: false));
        _held.Clear();
        StopTimer(ref _timer);
        StopTimer(ref _trimTimer);
        RemoveInputHooks();
        UnhookWinEvent(_foregroundHook);
    }

    /// <summary>An exception must never kill the hook thread (the app would look alive but stop working).</summary>
    void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log.Error(what, ex);
            if (_phase != Phase.Normal) ReleaseEverything();
        }
    }

    /// <summary>Last resort: send all held keys unchanged and go back to normal.</summary>
    void ReleaseEverything()
    {
        StopTimer(ref _timer);
        var rest = _held.ToArray();
        _held.Clear();
        _phase = Phase.Normal;
        if (rest.Length > 0) Send(BuildInputs(false, rest, endTag: false));
        ApplyHooks();
    }

    // ---------------------------------------------------------------- foreground tracking / hook lifetime

    void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) =>
        Guard("foreground", UpdateForeground);

    void UpdateForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        _targetPidCache.Remove(pid); // process ids get reused: look again
        bool isZoom = IsTargetProcess(pid);
        _dirty = true; // a window switch may land anywhere
        _focusMayHaveMoved = true;
        _lineStartPending = false;
        _word.Clear();
        if (isZoom && _enabled) _inspector.Prime(pid);
        _zoomInForeground = isZoom;
        ApplyHooks();
    }

    /// <summary>Installs the input hooks while Zoom is in front (and we're enabled), removes them otherwise.</summary>
    void ApplyHooks()
    {
        bool want = _enabled && _zoomInForeground;
        if (want && _keyboardHook == IntPtr.Zero)
        {
            StopTimer(ref _trimTimer);
            var hMod = GetModuleHandle(null);
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
            if (_keyboardHook == IntPtr.Zero) _log.Error("could not install keyboard hook, error " + Marshal.GetLastWin32Error());
            _dirty = true;
            _focusMayHaveMoved = true;
            _log.Info("Zoom in front: input hooks on");
        }
        else if (!want && _keyboardHook != IntPtr.Zero && _phase == Phase.Normal)
        {
            RemoveInputHooks();
            _log.Info("Zoom not in front: input hooks off");
            // Give memory back while idle (after a short delay, so quick Alt+Tabs don't churn).
            StopTimer(ref _trimTimer);
            _trimTimer = SetTimer(IntPtr.Zero, UIntPtr.Zero, TrimDelayMs, IntPtr.Zero);
        }
    }

    void RemoveInputHooks()
    {
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
    }

    static void TrimMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        EmptyWorkingSet(GetCurrentProcess());
    }

    // ---------------------------------------------------------------- hooks

    IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int m = (int)wParam;
            if (m is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
            {
                _dirty = true; // a click may have moved the caret or switched chats
                _focusMayHaveMoved = true;
                _lineStartPending = false;
                _word.Clear();
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (OnKey((int)wParam, k)) return 1; // held – it will be replayed
            }
            catch (Exception ex)
            {
                _log.Error("keyboard hook", ex);
                if (_phase != Phase.Normal) ReleaseEverything();
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    /// <returns>true if the keystroke is held back (swallowed now, replayed later).</returns>
    bool OnKey(int message, KBDLLHOOKSTRUCT k)
    {
        if (k.dwExtraInfo == OwnInputTag) return false;
        if (k.dwExtraInfo == ReplayEndTag)
        {
            if (_phase == Phase.Draining) OnReplayPassed();
            return false;
        }

        bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        uint threadId = GetWindowThreadProcessId(GetForegroundWindow(), out var pid);

        if (_phase != Phase.Normal)
        {
            // Holding: keep order, and note what each key means using the modifier state the held keys produce.
            var held = Arrive(k, down, threadId, _heldMods);
            UpdateMods(ref _heldMods, k.vkCode, down);
            _held.Add(held);
            return true;
        }

        if (!down) return false;
        if ((k.flags & LLKHF_INJECTED) != 0 && !_settings.ProcessInjectedInput) return false;
        if (!IsTargetProcess(pid)) return false;

        var key = Arrive(k, true, threadId, PhysicalMods());

        // Switching keyboard language with Alt+Shift can leave Zoom's window in menu mode, where the next key is
        // swallowed by the (invisible) window menu. Leave menu mode first, then pass the key on.
        if (key.Kind != KeyKind.Neutral && k.vkCode != VK_ESCAPE && InMenuBarMode(threadId))
        {
            _log.Info($"Zoom's window is in menu mode: sending Escape before {DescribeForLog(key)}");
            Send([KeyInput(VK_ESCAPE, keyUp: false), KeyInput(VK_ESCAPE, keyUp: true)]);
            _held.Add(key);
            _heldMods = PhysicalMods();
            if (key.Kind == KeyKind.Printable && _dirty) StartCheck(pid);
            else Replay(insertMarker: false, keepFrom: _held.Count);
            return true;
        }

        switch (key.Kind)
        {
            case KeyKind.Neutral:
                return false;
            case KeyKind.Dirty:
                MarkDirty(key);
                return false;
        }

        // Printable: only the first character after something that may have started a new line is checked.
        if (!_dirty) return false;

        _held.Add(key);
        _heldMods = PhysicalMods();
        StartCheck(pid);
        return true;
    }

    void MarkDirty(HeldKey key)
    {
        _dirty = true;
        _lineStartPending = key.NewLine || key.SelectAll;
        if (key.FocusMove) _focusMayHaveMoved = true;
    }

    /// <summary>
    /// Describes a key as it arrives (arrival order = typing order, held or not) and keeps track of the word being
    /// typed, so Enter after "@na" (picking a mention) or ":smi" (picking an emoji) isn't taken for a new line.
    /// </summary>
    HeldKey Arrive(KBDLLHOOKSTRUCT k, bool down, uint threadId, Mods mods)
    {
        var key = Describe(k, down, threadId, mods);
        if (!down || key.Kind == KeyKind.Neutral) return key;

        if (key.Kind == KeyKind.Printable)
        {
            if (char.IsWhiteSpace(key.Char)) _word.Clear();
            else if (_word.Length < 64) _word.Append(key.Char);
            return key;
        }

        // Caret-moving key.
        if (key.NewLine && _word.Length > 0 && _word[0] is '@' or ':')
            key = key with { NewLine = false }; // Enter picks from Zoom's mention/emoji list
        if (k.vkCode == VK_BACK && key.Kind == KeyKind.Dirty && (mods & (Mods.Ctrl | Mods.Alt)) == 0)
        {
            if (_word.Length > 0) _word.Length--;
        }
        else
        {
            _word.Clear();
        }
        return key;
    }

    static string DescribeForLog(HeldKey key) =>
        key.Kind == KeyKind.Printable ? $"'{key.Char}'" : $"key 0x{key.Data.vkCode:X2}";

    /// <summary>
    /// Is the window in "menu bar" mode (Alt pressed alone, or after a language switch)? Not when a menu is open
    /// (e.g. Alt+Space): then keys are meant for that menu.
    /// </summary>
    static bool InMenuBarMode(uint threadId)
    {
        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(threadId, ref gti)) return false;
        return (gti.flags & (GUI_INMENUMODE | GUI_SYSTEMMENUMODE)) != 0 && (gti.flags & GUI_POPUPMENUMODE) == 0;
    }

    static INPUT KeyInput(int vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = (ushort)vk,
                wScan = (ushort)MapVirtualKey((uint)vk, 0),
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                dwExtraInfo = OwnInputTag,
            },
        },
    };

    /// <summary>Starts a check for <c>_held[0]</c>, the first character after something that may have started a new line.</summary>
    /// <param name="settleMs">Wait before looking (keys were just replayed and Zoom may not have processed them yet).</param>
    void StartCheck(uint pid, int settleMs = 0)
    {
        var first = _held[0];
        _dirty = false;
        _checkLineStart = _lineStartPending;
        _lineStartPending = false;

        // The user typed a direction mark themselves: leave the line alone.
        if (Bidi.IsDirectionMark(first.Char))
        {
            Replay(insertMarker: false, keepFrom: _held.Count);
            return;
        }

        _phase = Phase.Checking;
        int id = ++_checkId;
        bool mayUseLastChat = !_focusMayHaveMoved;
        _focusMayHaveMoved = false;
        _checkClock.Restart();
        if (_log.Enabled)
            _log.Info($"'{first.Char}' U+{(int)first.Char:X4}: checking (#{id}){(_checkLineStart ? ", new line" : "")}{(mayUseLastChat ? "" : ", focus may have moved")}");
        uint hookThread = _threadId;
        _inspector.Query(pid, id, mayUseLastChat, settleMs,
            (rid, state) => PostThreadMessage(hookThread, WM_APP_CHECK_RESULT, rid, (int)state));
        uint timeout = _settings.UiaTimeoutMs > 0 ? (uint)_settings.UiaTimeoutMs : CheckTimeoutMsDefault;
        StartTimer(ref _timer, timeout + (uint)settleMs);
    }

    // ---------------------------------------------------------------- check results, replay

    void OnCheckResult(int id, CaretState state)
    {
        if (_phase != Phase.Checking || id != _checkId) return; // stale (already timed out)
        StopTimer(ref _timer);
        // A line starts where we saw Enter/Ctrl+A just before, or in an empty chat box.
        bool insert = state == CaretState.ChatEmpty || state == CaretState.ChatNotEmpty && _checkLineStart;

        // Keys after the first caret-moving key (Enter, Backspace, click...) belong to what comes next:
        // they are examined again once this part has been replayed.
        int keepFrom = FirstAfterDirty(start: 1);

        if (_log.Enabled)
            _log.Info($"check #{id} -> {state} after {_checkClock.ElapsedMilliseconds} ms, {(insert ? "inserting marker" : "no marker")}" +
                $", replaying {keepFrom} key event(s){(keepFrom < _held.Count ? $", {_held.Count - keepFrom} more to examine" : "")}");
        Replay(insert, keepFrom);
    }

    /// <summary>Index of the first printable key-down that follows a caret-moving key, at or after <paramref name="start"/>.</summary>
    int FirstAfterDirty(int start)
    {
        bool sawDirty = false;
        for (int i = start; i < _held.Count; i++)
        {
            var h = _held[i];
            if (h.Kind == KeyKind.Dirty) sawDirty = true;
            else if (sawDirty && h.Kind == KeyKind.Printable) return i;
        }
        return _held.Count;
    }

    void OnTimer(UIntPtr timerId)
    {
        if (timerId == _trimTimer)
        {
            StopTimer(ref _trimTimer);
            if (_keyboardHook == IntPtr.Zero) TrimMemory();
            return;
        }
        if (timerId != _timer) return;
        StopTimer(ref _timer);
        if (_phase == Phase.Checking)
        {
            _log.Info($"check #{_checkId} timed out after {_checkClock.ElapsedMilliseconds} ms, replaying held keys without marker");
            Replay(insertMarker: false, keepFrom: FirstAfterDirty(start: 1));
        }
        else if (_phase == Phase.Draining)
        {
            // The end-of-replay event never came back through the hook; don't hold keys forever.
            _log.Error("replay end not seen, releasing held keys");
            ReleaseEverything();
        }
    }

    /// <summary>Our previous replay has gone through the hook; deal with whatever was held meanwhile.</summary>
    void OnReplayPassed()
    {
        StopTimer(ref _timer);
        if (_held.Count == 0)
        {
            _phase = Phase.Normal;
            ApplyHooks(); // Zoom may have lost the foreground while we were busy
            return;
        }

        // The held keys start with the first character after a caret-moving key (see OnCheckResult), or are
        // whatever arrived while draining. Examine them again, in order, as if they were arriving now.
        var backlog = _held.ToArray();
        _held.Clear();
        _phase = Phase.Normal;
        int i = 0;
        for (; i < backlog.Length; i++)
        {
            var h = backlog[i];
            if (h.Up || h.Kind == KeyKind.Neutral) continue;
            if (h.Kind == KeyKind.Dirty) { MarkDirty(h); continue; }
            if (_dirty && _zoomInForeground && _enabled) break; // first character after a caret-moving key
        }

        if (i == backlog.Length)
        {
            _held.AddRange(backlog);
            Replay(insertMarker: false, keepFrom: _held.Count);
            return;
        }

        // Send what comes before (unchanged), then check backlog[i] like a fresh key, holding the rest.
        _held.AddRange(backlog[..i]);
        var rest = backlog[i..];
        if (i > 0)
        {
            // Unchanged keys first; the new check starts once they've passed (order is kept by Draining).
            Replay(insertMarker: false, keepFrom: i, extra: rest);
            return;
        }
        _held.AddRange(rest);
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        StartCheck(pid, BacklogSettleMs);
    }

    /// <summary>
    /// Sends _held[0..keepFrom) (with the marker in front, if asked) and keeps _held[keepFrom..]
    /// (plus <paramref name="extra"/>) held.
    /// </summary>
    void Replay(bool insertMarker, int keepFrom, HeldKey[]? extra = null)
    {
        keepFrom = Math.Clamp(keepFrom, 0, _held.Count);
        var send = _held.GetRange(0, keepFrom).ToArray();
        var keep = _held.GetRange(keepFrom, _held.Count - keepFrom);
        if (extra is not null) keep.AddRange(extra);
        _held.Clear();
        _held.AddRange(keep);

        // Caret-moving keys among what's sent (e.g. a fast Enter after the first letter) start a new line or
        // move the caret: the next character must be checked again.
        foreach (var h in send)
            if (h.Kind == KeyKind.Dirty) MarkDirty(h);

        var inputs = BuildInputs(insertMarker, send, endTag: true);
        _phase = Phase.Draining;
        if (inputs.Length > 0 && Send(inputs))
        {
            // SendInput may already have run our hook (and finished draining) synchronously.
            if (_phase == Phase.Draining) StartTimer(ref _timer, DrainTimeoutMs);
        }
        else
        {
            // Nothing will come back through the hook: continue with what's held right away.
            OnReplayPassed();
        }
    }

    INPUT[] BuildInputs(bool insertMarker, HeldKey[] keys, bool endTag)
    {
        var list = new List<INPUT>(keys.Length + 2);
        if (insertMarker)
        {
            var marker = _settings.MarkerChar;
            list.Add(UnicodeKey(marker, keyUp: false));
            list.Add(UnicodeKey(marker, keyUp: true));
        }
        foreach (var key in keys) list.Add(ReplayKey(key));

        if (endTag && list.Count > 0)
        {
            var last = list[^1];
            last.u.ki.dwExtraInfo = ReplayEndTag;
            list[^1] = last;
        }
        return list.ToArray();
    }

    bool Send(INPUT[] inputs)
    {
        if (inputs.Length == 0) return false;
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            _log.Error($"SendInput sent {sent}/{inputs.Length}, error {Marshal.GetLastWin32Error()}");
            return false;
        }
        return true;
    }

    static void StartTimer(ref UIntPtr timer, uint ms)
    {
        StopTimer(ref timer);
        timer = SetTimer(IntPtr.Zero, UIntPtr.Zero, ms, IntPtr.Zero);
    }

    static void StopTimer(ref UIntPtr timer)
    {
        if (timer == UIntPtr.Zero) return;
        KillTimer(IntPtr.Zero, timer);
        timer = UIntPtr.Zero;
    }

    // ---------------------------------------------------------------- key analysis

    /// <summary>Decides what a key event means, given the modifier state in effect for it.</summary>
    HeldKey Describe(KBDLLHOOKSTRUCT k, bool down, uint threadId, Mods mods)
    {
        if (!down || IsModifier(k.vkCode)) return new HeldKey(k, !down, KeyKind.Neutral, '\0', false, false, false);

        bool ctrl = (mods & Mods.Ctrl) != 0, alt = (mods & Mods.Alt) != 0, win = (mods & Mods.Win) != 0;
        // Enter, Shift+Enter (new line) and Ctrl+Enter (send) all leave the caret at the start of a line.
        bool newLine = k.vkCode == VK_RETURN && !alt && !win;
        bool selectAll = k.vkCode == VK_A && ctrl && !alt && !win;
        bool focusMove = k.vkCode is VK_TAB or VK_F6 || (mods & (Mods.Alt | Mods.Win)) != 0 && (mods & Mods.Ctrl) == 0;
        bool altGr = ctrl && alt;
        if (win || ctrl != alt) // shortcut (Ctrl+V, Ctrl+Z, Ctrl+Enter, Alt+Tab, ...)
            return new HeldKey(k, false, KeyKind.Dirty, '\0', newLine, selectAll, focusMove);

        char? ch = Translate(k, threadId, (mods & Mods.Shift) != 0, altGr);
        if (ch is null) // Enter, Backspace, Delete, arrows, Home/End, Tab, Esc, ...
            return new HeldKey(k, false, KeyKind.Dirty, '\0', newLine, false, focusMove);
        return new HeldKey(k, false, KeyKind.Printable, ch.Value, false, false, false);
    }

    static Mods PhysicalMods()
    {
        var m = Mods.None;
        if (IsKeyDown(VK_SHIFT)) m |= Mods.Shift;
        if (IsKeyDown(VK_CONTROL)) m |= Mods.Ctrl;
        if (IsKeyDown(VK_MENU)) m |= Mods.Alt;
        if (IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN)) m |= Mods.Win;
        return m;
    }

    static void UpdateMods(ref Mods mods, uint vk, bool down)
    {
        Mods bit = vk switch
        {
            VK_SHIFT or VK_LSHIFT or VK_RSHIFT => Mods.Shift,
            VK_CONTROL or VK_LCONTROL or VK_RCONTROL => Mods.Ctrl,
            VK_MENU or VK_LMENU or VK_RMENU => Mods.Alt,
            VK_LWIN or VK_RWIN => Mods.Win,
            _ => Mods.None,
        };
        if (bit == Mods.None) return;
        mods = down ? mods | bit : mods & ~bit;
    }

    bool IsTargetProcess(uint pid)
    {
        if (pid == 0) return false;
        if (_targetPidCache.TryGetValue(pid, out var cached)) return cached;
        bool result = false;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            foreach (var name in _settings.ProcessNames)
                if (string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)) { result = true; break; }
        }
        catch
        {
            // process gone / access denied
        }
        if (_targetPidCache.Count > 256) _targetPidCache.Clear();
        _targetPidCache[pid] = result;
        return result;
    }

    static bool IsModifier(uint vk) => vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_CAPITAL
        or VK_LSHIFT or VK_RSHIFT or VK_LCONTROL or VK_RCONTROL or VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN;

    /// <summary>Translates the key using the focused thread's keyboard layout. Null for non-printable keys.</summary>
    char? Translate(KBDLLHOOKSTRUCT k, uint foregroundThreadId, bool shift, bool altGr)
    {
        if (k.vkCode == VK_PACKET) return (char)k.scanCode; // a SendInput unicode character

        uint focusThread = foregroundThreadId;
        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(foregroundThreadId, ref gti) && gti.hwndFocus != IntPtr.Zero)
            focusThread = GetWindowThreadProcessId(gti.hwndFocus, out _);
        var layout = GetKeyboardLayout(focusThread);

        Array.Clear(_keyState);
        if (shift) _keyState[VK_SHIFT] = _keyState[VK_LSHIFT] = 0x80;
        if ((GetKeyState(VK_CAPITAL) & 1) != 0) _keyState[VK_CAPITAL] = 0x01;
        if (altGr)
        {
            _keyState[VK_CONTROL] = 0x80;
            _keyState[VK_MENU] = 0x80;
            _keyState[VK_LCONTROL] = 0x80;
            _keyState[VK_RMENU] = 0x80;
        }

        // Flag 0x4: do not change the kernel keyboard state (keeps dead keys working for the real app).
        int n = ToUnicodeEx(k.vkCode, k.scanCode, _keyState, _charBuffer, _charBuffer.Length, 0x4, layout);
        if (n >= 1 && _charBuffer[0] >= 0x20 && _charBuffer[0] != 0x7F) return _charBuffer[0];
        return null;
    }

    static INPUT ReplayKey(HeldKey key)
    {
        var k = key.Data;
        if (k.vkCode == VK_PACKET) return UnicodeKey((char)k.scanCode, key.Up);
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)k.vkCode,
                    wScan = (ushort)k.scanCode,
                    dwFlags = ((k.flags & LLKHF_EXTENDED) != 0 ? KEYEVENTF_EXTENDEDKEY : 0) | (key.Up ? KEYEVENTF_KEYUP : 0),
                    dwExtraInfo = OwnInputTag,
                },
            },
        };
    }

    static INPUT UnicodeKey(char c, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                dwExtraInfo = OwnInputTag,
            },
        },
    };

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }
}
