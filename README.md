<p align="center">
  <img src="assets/ZoomBiDi.png" width="128" alt="ZoomBiDi icon">
</p>

<h1 align="center">ZoomBiDi</h1>

<p align="center"><b>Type Hebrew and English together in Zoom chat, without the text jumping around.</b></p>

<p align="center">
  <a href="https://github.com/gonenbeneish-mapcore/ZoomBiDi/releases/latest"><b>Download for Windows</b></a>
</p>

---

## What it does

When you write a Zoom chat message that mixes Hebrew and English, Zoom often treats the line as left-to-right.
Words come out in the wrong order and the cursor jumps around while you type:

<p align="center"><img src="assets/before-after.png" width="640" alt="Before: Hebrew and English words out of order in Zoom chat. After: the line reads right-to-left correctly."></p>

The known workaround is to type an invisible Unicode character at the start of every line to tell Zoom which
way the line goes. ZoomBiDi does that for you automatically:

* It runs quietly in the system tray.
* Every time you start a new line in a Zoom chat box, it slips U+2068 (FIRST STRONG ISOLATE) in at the start of
  the line. That character gives the line the direction of its first letter: lines that start in Hebrew read
  right-to-left, and lines that start in English stay left-to-right (`hello שלום` stays `hello שלום`).
* Search boxes and other fields in Zoom aren't touched. Outside Zoom it does nothing at all.
* It's light: about 3 MB of memory while you're in other apps, about 12–17 MB while Zoom is in front, and no
  CPU at all outside Zoom. The first character of a line is held for a few milliseconds (median 6 ms); the rest
  of your typing passes straight through.
* It works with a Zoom that's already running; there's no need to restart Zoom.

You just type as usual.

## Install

1. Download from the [latest release](https://github.com/gonenbeneish-mapcore/ZoomBiDi/releases/latest):
   * **`ZoomBiDi-standalone.exe`** (about 68 MB): runs on any 64-bit Windows 10/11. Pick this one if unsure.
   * **`ZoomBiDi.exe`** (about 0.3 MB): needs the
     [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64) or newer.
2. Put it anywhere (for example `Documents\ZoomBiDi`) and run it. There's no installer.
3. The exe isn't code-signed, so Windows SmartScreen may warn on first run: click **More info**, then **Run anyway**.
   Or, before running it, right-click the exe → **Properties** → tick **Unblock** → OK.

A blue **Aא** icon appears in the system tray (it may be under the **^** overflow arrow; you can drag it onto
the taskbar).

## Use

Just chat in Zoom. Tray icon controls:

* **Left-click**: pause / resume. Grey means paused.
* **Right-click** for the menu:
  * **Start with Windows**: launch ZoomBiDi automatically when you sign in
  * **Debug log** / **Open log**: see what it decided and why (useful if something doesn't work)
  * **Exit**

**Privacy:** ZoomBiDi never connects to the network and never stores what you type. The optional debug log (off
by default) records only the first character of each new line and the short bit of text before the cursor.

---

## Technical details

### How it works

* **Hooks only while Zoom is in front.** Normally the only thing running is a cheap foreground-change
  notification (`SetWinEventHook` with `EVENT_SYSTEM_FOREGROUND`). The low-level keyboard and mouse hooks
  (`WH_KEYBOARD_LL`, `WH_MOUSE_LL`) are installed when a Zoom window comes to the front and removed when it
  leaves, so typing in other apps never passes through ZoomBiDi. Hooks run on a dedicated thread, and each key
  is translated with the focused window's keyboard layout (`ToUnicodeEx`).
* **Cheap by default.** Ordinary typing passes straight through. The app only looks closer at the *first
  character after something that could have started a new line*: Enter, Backspace, Delete, arrows, Home/End,
  Tab, mouse clicks, shortcuts (Ctrl+V…), or switching windows.
* **Checking the caret.** For that character, the key is held while UI Automation inspects the focused element.
  This runs on a separate MTA thread, calling the UI Automation COM API directly. It checks three things:
  * The element is a Zoom message box: an Edit control whose accessible name matches `message` (Zoom names it
    "Message to …").
  * It belongs to Zoom, or to a child process of Zoom. Zoom's chat is an embedded WebView2, so the box lives in
    `msedgewebview2.exe`.
  * Nothing sits between the start of the line and the caret. Chromium reports the caret on a new empty line as
    before the line break, so an empty line is detected separately via the `TextUnit.Line` range. A selection
    that starts at the line start counts as the start of the line (typing replaces it, mark included). If the
    caret is right before the line's existing mark (Home on a line that's already fixed), it's moved past the
    mark instead, so the mark stays first.
* **Replay in order.** The held keys are then replayed with `SendInput`, with U+2068 in front when needed.
  A typical check takes 3–10 ms (median 6 ms).
* **Keys typed during a check.** They're held too, and each is classified as it arrives, using the modifier
  state the held keys themselves produce (so a held Ctrl+V is still seen as a shortcut). If they include a new
  line (e.g. a fast Enter and the next line's first letter), everything up to that point is replayed, and the
  next line's first character gets its own check. That check waits 40 ms first, so Zoom has processed the
  replayed keys before UI Automation is asked.
* **Zoom's accessibility info lags its screen.** Right after Enter, Zoom may still report the previous line for
  a few milliseconds. A "mid-line" answer right after Enter is therefore re-checked (up to 3 times, 30 ms apart).
* **Switching keyboard language.** Alt+Shift briefly puts the focus on Zoom's menu bar, often until the next key
  arrives, which is the very key being held. In that case the chat box from the last check is asked directly,
  as long as nothing could have moved the focus since (no click, Tab or window switch). Other passing focus
  states get up to 4 short retries.
* **Small footprint.** Calling the UI Automation COM API directly, instead of the managed
  `System.Windows.Automation` wrapper, means WPF is never loaded. ICU globalization data and background GC are
  off. When Zoom leaves the foreground, the app compacts its heap and releases its working set.
* **Never blocks.** Windows silently removes hooks that respond slowly, and the target app may need input to
  flow before it can answer UI Automation. So the hook callback never waits: results and timeouts arrive as
  messages on the hook thread. If UI Automation takes longer than `UiaTimeoutMs`, the keys are released
  unchanged.
* **Never loses keys.** An error in any handler is logged and the held keys are released unchanged, rather than
  stopping the hook thread. Keys held at exit are sent before ZoomBiDi closes. The debug log is written on a
  background thread, so the disk can never slow down typing.

### Settings

Advanced settings live in `%APPDATA%\ZoomBiDi\settings.json`. To open it, paste `%APPDATA%\ZoomBiDi` into
the Explorer address bar. Restart ZoomBiDi after editing.

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Same as the tray toggle |
| `MarkerHex` | `2068` | Code point to insert. `2068` (FSI) follows each line's first letter. Up to 1.2 the default was `2067` (RLI, always right-to-left); settings files still holding `2067` are upgraded automatically, so write `U+2067` to keep it on purpose |
| `ProcessNames` | `["Zoom"]` | Processes (without `.exe`) treated as Zoom |
| `ChatNamePattern` | `message` | Regex matched (case-insensitive) against the focused box's accessible name |
| `UiaTimeoutMs` | `250` | Give up and type without the mark if the check takes longer |
| `DebugLog` | `false` | Write decisions to `%APPDATA%\ZoomBiDi\log.txt` |
| `ProcessInjectedInput` | `false` | Testing only: also react to synthetic keystrokes |

`--settings <path>` on the command line uses a different settings file (one instance runs per settings file).

### Project layout

| Path | What |
|---|---|
| `src/KeyboardMonitor.cs` | Hook thread: hooks only while Zoom is in front, key classification, hold/replay state machine, marker injection |
| `src/ChatInspector.cs` | UI Automation check: is the caret at the start of a line in a chat box? (retries, remembered chat box) |
| `src/UiaCom.cs` | Minimal bindings to the UI Automation COM API (verified against the Windows SDK header) |
| `src/ProcessTree.cs` | Parent/child process lookup (for Zoom's WebView2 child process) |
| `src/Bidi.cs` | Direction marks, letters and line breaks |
| `src/TrayApp.cs`, `src/Program.cs` | Tray icon, menu, startup, single instance |
| `src/Settings.cs`, `src/Logger.cs`, `src/Native.cs` | Settings file, debug log, Win32 interop |
| `tools/MakeIcon.ps1` | Generates the icons in `assets/` (and `assets/icon-preview.png`) |
| `test/` | Typing test harness and stand-in chat page |

### Build

Requires the .NET 8 SDK or newer.

```
dotnet publish ZoomBiDi.csproj -c Release -r win-x64 -o publish -p:PublishSingleFile=true -p:SelfContained=false
```

For the standalone build, use `-p:SelfContained=true -p:EnableCompressionInSingleFile=true` instead.

### Test

`test/testpage.html` is a Chromium contenteditable box labelled like Zoom's chat input. `test/TypeHarness.ps1`
types 23 cases into it (Hebrew, English and mixed lines, multiple lines, Backspace, Home, arrows, selections,
and "bursts" sent in one go so that keys arrive while a check is running) and checks exactly where U+2068
ended up, using an exact comparison (culture-aware string comparison ignores invisible characters).

| Option | Effect |
|---|---|
| `-Mode layout` (default) | Real key presses through the Hebrew and English keyboard layouts |
| `-Mode unicode` | Injected characters, no keyboard layout involved |
| `-AltShift` | Switch keyboard language with Alt+Shift, like a person, instead of asking the window directly |
| `-DelayMs <n>` | Delay between keys (default 70; 10 = very fast typing) |
| `-Only <i,j,...>` | Run only some cases (0-based indexes) |

1. Start ZoomBiDi with a test settings file containing `"ProcessNames": ["msedge"]` and
   `"ProcessInjectedInput": true`:
   `ZoomBiDi.exe --settings test-settings.json`
2. Open `test/testpage.html` in Edge.
3. Run:

   ```
   powershell -ExecutionPolicy Bypass -Command "& .\test\TypeHarness.ps1 -Mode layout"
   ```

The harness types into the foreground window, and stops as soon as the test page loses focus, so it never types
anywhere else. The Hebrew and English (US) keyboard layouts must both be installed for `-Mode layout`.

### Known limitations

* Only typed text is handled. Pasted text doesn't get the marker.
* The caret sits at the right of a Hebrew line instead of after the last word. That's Zoom's own behaviour (it
  happens without ZoomBiDi too): Zoom's editor has no right-to-left support, and no invisible character changes
  it. 16 combinations were tested in Zoom. For the same reason ZoomBiDi doesn't right-align Hebrew lines (Zoom's
  Ctrl+Shift+R would do it, but the caret stays on the wrong side).
* The chat inside a Zoom *meeting* hasn't been tested and may name its text box differently. If it's ignored
  there, the debug log shows the name, and `ChatNamePattern` can be widened.
* Not yet tested: @mentions at the start of a line (the mark goes before the `@`).
* A mouse click within a few milliseconds of a line's first character can reach Zoom before that character.
