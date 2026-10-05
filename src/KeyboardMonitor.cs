using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using static ZoomBiDi.Native;

namespace ZoomBiDi;

/// <summary>
/// Global low-level keyboard/mouse hook. While a Zoom window is in the foreground it watches for the first
/// strong (letter) character typed on a line. If that letter is Hebrew, the keystroke is held back while UI
/// Automation checks whether the caret really is at the start of a line in a chat box; then the held keys are
/// replayed, preceded by the marker character when needed.
///
/// The hook callback itself never blocks (Windows silently removes hooks that are slow, and the target app may
/// need keyboard input to flow before it can answer UI Automation). Everything runs on one dedicated thread:
/// hook callbacks, check results (posted messages) and timeouts (thread timers) are serialised, which keeps the
/// held/replayed keystrokes in their original order.
///
/// To keep typing fast, the check only happens for the first letter after something that could have moved the
/// caret to a new line (Enter, Backspace, arrows, clicks, shortcuts, window switches, ...).
/// </summary>
internal sealed class KeyboardMonitor : IDisposable
{
    /// <summary>Tag on our own SendInput events so the hook lets them through.</summary>
    static readonly UIntPtr OwnInputTag = new(0x5A544446);      // "ZTDF"
    /// <summary>Tag on the last event of a replay, so we know when the replay has passed the hook.</summary>
    static readonly UIntPtr ReplayEndTag = new(0x5A544447);

    const uint WM_APP_CHECK_RESULT = 0x8000 + 1; // WM_APP + 1
    const uint WM_TIMER = 0x0113;
    const uint CheckTimeoutMsDefault = 250;
    const uint DrainTimeoutMs = 1000;

    enum Phase
    {
        /// <summary>Keys pass through.</summary>
        Normal,
        /// <summary>Waiting for the UI Automation check; keys are held.</summary>
        Checking,
        /// <summary>Replay sent; keys are still held until the replay has passed the hook (keeps order).</summary>
        Draining,
    }

    readonly record struct HeldKey(KBDLLHOOKSTRUCT Data, bool Up);

    readonly Settings _settings;
    readonly ChatInspector _inspector;
    readonly Logger _log;
    readonly Thread _thread;
    readonly ManualResetEventSlim _started = new(false);
    readonly LowLevelProc _keyboardProc;
    readonly LowLevelProc _mouseProc;
    readonly Dictionary<uint, bool> _targetPidCache = new();
    readonly char[] _charBuffer = new char[8];
    readonly byte[] _keyState = new byte[256];
    readonly List<HeldKey> _held = new();

    uint _threadId;
    IntPtr _keyboardHook, _mouseHook;

    // --- hook-thread state ---
    /// <summary>The caret may be at the start of a line: check before the next letter.</summary>
    bool _dirty = true;
    IntPtr _lastForeground;
    Phase _phase = Phase.Normal;
    int _checkId;
    UIntPtr _timer;
    readonly Stopwatch _checkClock = new();

    public volatile bool Enabled;

    public KeyboardMonitor(Settings settings, ChatInspector inspector, Logger log)
    {
        _settings = settings;
        _inspector = inspector;
        _log = log;
        _keyboardProc = KeyboardHook;
        _mouseProc = MouseHook;
        Enabled = settings.Enabled;
        _thread = new Thread(Run) { IsBackground = true, Name = "Input hook" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public void Start()
    {
        _thread.Start();
        _started.Wait();
        if (_keyboardHook == IntPtr.Zero)
            throw new InvalidOperationException("Could not install the keyboard hook (error " + Marshal.GetLastWin32Error() + ").");
    }

    void Run()
    {
        _threadId = GetCurrentThreadId();
        var hMod = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
        _started.Set();
        if (_keyboardHook == IntPtr.Zero) return;

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.hwnd == IntPtr.Zero && msg.message == WM_APP_CHECK_RESULT)
            {
                OnCheckResult((int)msg.wParam, (CaretState)(int)msg.lParam);
                continue;
            }
            if (msg.hwnd == IntPtr.Zero && msg.message == WM_TIMER)
            {
                OnTimer((UIntPtr)(ulong)msg.wParam);
                continue;
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        StopTimer();
        UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
    }

    IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int m = (int)wParam;
            if (m is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
                _dirty = true; // a click may have moved the caret or switched chats
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

        if (_phase != Phase.Normal)
        {
            _held.Add(new HeldKey(k, !down));
            if (down && !IsModifier(k.vkCode) && Translate(k, ForegroundThread(), false) is null)
                _dirty = true; // e.g. a fast Enter while we were checking
            return true;
        }

        if (!down) return false;
        if ((k.flags & LLKHF_INJECTED) != 0 && !_settings.ProcessInjectedInput) return false;
        if (!Enabled) return false;
        if (IsModifier(k.vkCode)) return false;

        var fg = GetForegroundWindow();
        uint threadId = GetWindowThreadProcessId(fg, out var pid);
        bool isTarget = IsTargetProcess(pid);
        if (fg != _lastForeground)
        {
            _lastForeground = fg;
            _dirty = true;
            if (isTarget) _inspector.Prime(pid);
        }
        if (!isTarget) return false;

        bool ctrl = IsKeyDown(VK_CONTROL), alt = IsKeyDown(VK_MENU);
        bool win = IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN);
        bool altGr = ctrl && alt;
        if (win || ctrl != alt)
        {
            _dirty = true; // shortcut (Ctrl+V, Ctrl+Z, Ctrl+Enter, Alt+Tab, ...)
            return false;
        }

        char? ch = Translate(k, threadId, altGr);
        if (ch is null)
        {
            _dirty = true; // Enter, Backspace, Delete, arrows, Home/End, Tab, Esc, ...
            return false;
        }

        if (!_dirty) return false;

        var cls = Bidi.Classify(ch.Value);
        if (cls == CharClass.Neutral) return false; // spaces/digits/punctuation: still at "start", keep waiting
        _dirty = false;

        if (cls == CharClass.DirectionMark) return false;
        if (cls == CharClass.StrongLtr && _settings.Mode != InsertMode.Always) return false;

        // Hold this key and ask UI Automation whether we're at the start of a line in a chat box.
        _phase = Phase.Checking;
        _held.Add(new HeldKey(k, false));
        int id = ++_checkId;
        _checkClock.Restart();
        _log.Info($"'{ch}' U+{(int)ch.Value:X4}: checking (#{id})");
        uint hookThread = _threadId;
        _inspector.Query(pid, id, (rid, state) => PostThreadMessage(hookThread, WM_APP_CHECK_RESULT, rid, (int)state));
        StartTimer(_settings.UiaTimeoutMs > 0 ? (uint)_settings.UiaTimeoutMs : CheckTimeoutMsDefault);
        return true;
    }

    void OnCheckResult(int id, CaretState state)
    {
        if (_phase != Phase.Checking || id != _checkId) return; // stale (already timed out)
        StopTimer();
        bool insert = state == CaretState.LineStart;
        _log.Info($"check #{id} -> {state} after {_checkClock.ElapsedMilliseconds} ms, {(insert ? "inserting marker" : "no marker")}, replaying {_held.Count} key event(s)");
        Replay(insert);
    }

    void OnTimer(UIntPtr timerId)
    {
        if (timerId != _timer) return;
        StopTimer();
        if (_phase == Phase.Checking)
        {
            _log.Info($"check #{_checkId} timed out after {_checkClock.ElapsedMilliseconds} ms, replaying {_held.Count} key event(s) without marker");
            Replay(insertMarker: false);
        }
        else if (_phase == Phase.Draining)
        {
            // The end-of-replay event never came back through the hook; don't hold keys forever.
            _log.Error("replay end not seen, releasing held keys");
            var rest = _held.ToArray();
            _held.Clear();
            _phase = Phase.Normal;
            Send(BuildInputs(false, rest, endTag: false));
        }
    }

    /// <summary>Our previous replay has gone through the hook; replay whatever was held meanwhile.</summary>
    void OnReplayPassed()
    {
        StopTimer();
        if (_held.Count == 0)
        {
            _phase = Phase.Normal;
            return;
        }
        Replay(insertMarker: false);
    }

    void Replay(bool insertMarker)
    {
        var keys = _held.ToArray();
        _held.Clear();
        var inputs = BuildInputs(insertMarker, keys, endTag: true);
        _phase = Phase.Draining;
        if (Send(inputs))
        {
            StartTimer(DrainTimeoutMs);
        }
        else
        {
            _phase = Phase.Normal; // nothing will come back through the hook
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

    void StartTimer(uint ms)
    {
        StopTimer();
        _timer = SetTimer(IntPtr.Zero, UIntPtr.Zero, ms, IntPtr.Zero);
    }

    void StopTimer()
    {
        if (_timer == UIntPtr.Zero) return;
        KillTimer(IntPtr.Zero, _timer);
        _timer = UIntPtr.Zero;
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

    static uint ForegroundThread() => GetWindowThreadProcessId(GetForegroundWindow(), out _);

    static bool IsModifier(uint vk) => vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_CAPITAL
        or VK_LSHIFT or VK_RSHIFT or VK_LCONTROL or VK_RCONTROL or VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN;

    /// <summary>Translates the key using the focused thread's keyboard layout. Null for non-printable keys.</summary>
    char? Translate(KBDLLHOOKSTRUCT k, uint foregroundThreadId, bool altGr)
    {
        if (k.vkCode == VK_PACKET) return (char)k.scanCode; // a SendInput unicode character

        uint focusThread = foregroundThreadId;
        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(foregroundThreadId, ref gti) && gti.hwndFocus != IntPtr.Zero)
            focusThread = GetWindowThreadProcessId(gti.hwndFocus, out _);
        var layout = GetKeyboardLayout(focusThread);

        Array.Clear(_keyState);
        if (IsKeyDown(VK_SHIFT)) _keyState[VK_SHIFT] = 0x80;
        if (IsKeyDown(VK_LSHIFT)) _keyState[VK_LSHIFT] = 0x80;
        if (IsKeyDown(VK_RSHIFT)) _keyState[VK_RSHIFT] = 0x80;
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
