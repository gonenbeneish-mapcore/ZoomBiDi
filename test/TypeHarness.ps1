# Types test sentences (Hebrew, English, mixed) into the test page in Edge and checks where the marker landed.
#   -Mode layout  : real key presses translated by the Hebrew / English keyboard layouts (default, like a person typing)
#   -Mode unicode : characters injected directly (no keyboard layout involved)
# Aborts if the foreground window is not the test page, so keystrokes never go anywhere else.
param([ValidateSet('layout', 'unicode')][string]$Mode = 'layout', [int]$DelayMs = 70, [int[]]$Only, [switch]$AltShift)

[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class H {
  [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort vk; public ushort scan; public uint flags; public uint time; public UIntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct MI { public int dx; public int dy; public uint data; public uint flags; public uint time; public UIntPtr extra; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public MI mi; [FieldOffset(0)] public KI ki; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public U u; }
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] i, int cb);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint tid);
  [DllImport("user32.dll")] public static extern int GetKeyboardLayoutList(int n, IntPtr[] list);
  [DllImport("user32.dll")] public static extern short VkKeyScanEx(char c, IntPtr hkl);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern uint MapVirtualKeyEx(uint code, uint type, IntPtr hkl);
  public delegate bool EP(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EP f, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

  public static bool Activate(string prefix) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { if (!IsWindowVisible(h)) return true; var sb = new StringBuilder(512); GetWindowText(h, sb, 512); if (sb.ToString().StartsWith(prefix)) { found = h; return false; } return true; }, IntPtr.Zero);
    if (found == IntPtr.Zero) return false;
    keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);
    return SetForegroundWindow(found);
  }
  public static string FgTitle() { var sb = new StringBuilder(4096); GetWindowText(GetForegroundWindow(), sb, 4096); return sb.ToString(); }

  public static IntPtr FindLayout(int langId) {
    var list = new IntPtr[32]; int n = GetKeyboardLayoutList(32, list);
    for (int i = 0; i < n; i++) if (((long)list[i] & 0xFFFF) == langId) return list[i];
    return IntPtr.Zero;
  }
  public static IntPtr CurrentLayout() { uint pid; return GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out pid)); }
  public static void RequestLayout(IntPtr hkl) { PostMessage(GetForegroundWindow(), 0x0050 /*WM_INPUTLANGCHANGEREQUEST*/, IntPtr.Zero, hkl); }

  static INPUT K(ushort vk, ushort scan, uint flags) { var i = new INPUT(); i.type = 1; i.u.ki.vk = vk; i.u.ki.scan = scan; i.u.ki.flags = flags; return i; }
  static void Send(params INPUT[] a) { SendInput((uint)a.Length, a, Marshal.SizeOf(typeof(INPUT))); }
  public static void Char(char c) { Send(K(0, c, 4), K(0, c, 6)); }
  public static void Vk(ushort vk) { Send(K(vk, 0, 0), K(vk, 0, 2)); }
  public static void AltShift() { Send(K(0xA4,0x38,0), K(0xA0,0x2A,0), K(0xA0,0x2A,2), K(0xA4,0x38,2)); }
  public static void ShiftHome() { Send(K(0x10,0x2A,0), K(0x24,0x47,1), K(0x24,0x47,3), K(0x10,0x2A,2)); } // Home = extended key (not numpad 7)
  // One SendInput call for a whole sequence: no gaps between keys, so later keys arrive while ZoomBiDi checks.
  // Newline = Enter, backspace char = Backspace, U+21E4 = Home, U+21F1 = Shift+Home, U+24B6 = Ctrl+A;
  // ASCII letters go as real keys (Shift for capitals), everything else as Unicode characters.
  public static void Burst(string s) {
    var list = new System.Collections.Generic.List<INPUT>();
    foreach (char c in s) {
      if (c == '\n') { list.Add(K(0x0D,0,0)); list.Add(K(0x0D,0,2)); }
      else if (c == '\b') { list.Add(K(0x08,0,0)); list.Add(K(0x08,0,2)); }
      else if (c == '\u21E4') { list.Add(K(0x24,0,0)); list.Add(K(0x24,0,2)); }
      else if (c == '\u21F1') { list.Add(K(0x10,0x2A,0)); list.Add(K(0x24,0x47,1)); list.Add(K(0x24,0x47,3)); list.Add(K(0x10,0x2A,2)); }
      else if (c == '\u24B6') { list.Add(K(0x11,0x1D,0)); list.Add(K(0x41,0x1E,0)); list.Add(K(0x41,0x1E,2)); list.Add(K(0x11,0x1D,2)); }
      else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) {
        ushort vk = (ushort)char.ToUpperInvariant(c);
        bool up = char.IsUpper(c);
        if (up) list.Add(K(0x10,0x2A,0));
        list.Add(K(vk,0,0)); list.Add(K(vk,0,2));
        if (up) list.Add(K(0x10,0x2A,2));
      }
      else { list.Add(K(0, c, 4)); list.Add(K(0, c, 6)); }
    }
    SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(INPUT)));
  }
  public static void CtrlA() { Send(K(0x11,0,0), K(0x41,0,0), K(0x41,0,2), K(0x11,0,2)); }
  // Types c as a real key press in layout hkl; false if the layout has no plain/shift key for it.
  public static bool KeyFor(char c, IntPtr hkl) {
    short r = VkKeyScanEx(c, hkl);
    if (r == -1) return false;
    ushort vk = (ushort)(r & 0xFF); int sh = (r >> 8) & 0xFF;
    if ((sh & 6) != 0) return false; // needs Ctrl/Alt - skip
    ushort scan = (ushort)MapVirtualKeyEx(vk, 0, hkl);
    if ((sh & 1) != 0) Send(K(0x10, 0x2A, 0), K(vk, scan, 0), K(vk, scan, 2), K(0x10, 0x2A, 2));
    else Send(K(vk, scan, 0), K(vk, scan, 2));
    return true;
  }
}
"@

$hebrew = [H]::FindLayout(0x040D)
$english = [H]::FindLayout(0x0409)
if ($Mode -eq 'layout' -and ($hebrew -eq [IntPtr]::Zero -or $english -eq [IntPtr]::Zero)) { throw "Hebrew and English keyboard layouts must both be installed for -Mode layout" }

function Assert-Fg { if (-not ([H]::FgTitle()).StartsWith("ZTDF-TEST")) { throw "Foreground is '$([H]::FgTitle())' - aborting" } }
function Use-Layout([IntPtr]$hkl) {
  if ([H]::CurrentLayout() -eq $hkl) { return }
  # -AltShift: switch like a person does (the Windows Alt+Shift hotkey), otherwise ask the window directly.
  if ($AltShift) { [H]::AltShift() } else { [H]::RequestLayout($hkl) }
  for ($i = 0; $i -lt 20 -and [H]::CurrentLayout() -ne $hkl; $i++) { Start-Sleep -Milliseconds 25 }
}
function Is-Hebrew([char]$c) { [int]$c -ge 0x0590 -and [int]$c -le 0x05FF }
function Is-Latin([char]$c) { ($c -cmatch '[A-Za-z]') }

function Type-Text([string]$s) {
  foreach ($c in $s.ToCharArray()) {
    Assert-Fg
    if ($c -eq "`n") { [H]::Vk(0x0D) }
    elseif ($c -eq "`b") { [H]::Vk(0x08) }
    elseif ($c -eq [char]0x2190) { [H]::Vk(0x25) }  # ← = Left arrow
    elseif ($c -eq [char]0x21E4) { [H]::Vk(0x24) }  # ⇤ = Home
    elseif ($c -eq [char]0x21F1) { [H]::ShiftHome() }  # ⇱ = Shift+Home
    elseif ($c -eq [char]0x24B6) { [H]::CtrlA() }  # Ⓐ = Ctrl+A
    elseif ($c -eq [char]0x2325) { [H]::Vk(0x12) }  # ⌥ = Alt pressed and released alone
    elseif ($Mode -eq 'unicode') { [H]::Char($c) }
    else {
      if (Is-Hebrew $c) { Use-Layout $hebrew } elseif (Is-Latin $c) { Use-Layout $english }
      if (-not [H]::KeyFor($c, [H]::CurrentLayout())) {
        # neutral character that sits elsewhere on the other layout (e.g. ',' '?') - switch for it
        $other = if ([H]::CurrentLayout() -eq $hebrew) { $english } else { $hebrew }
        Use-Layout $other
        if (-not [H]::KeyFor($c, $other)) { throw "cannot type '$c'" }
      }
    }
    Start-Sleep -Milliseconds $DelayMs
  }
}
function Clear-Box { Assert-Fg; [H]::CtrlA(); Start-Sleep -Milliseconds 80; Assert-Fg; [H]::Vk(0x2E); Start-Sleep -Milliseconds 300 }

# Each expected line is "<alignment>|<text>" as the test page reports it (always L: ZoomBiDi doesn't align lines).
# ^ marks where U+2068 should be inserted: at the start of every line.
# `n = Enter, `b = Backspace, ← = Left arrow, ⇤ = Home, ⇱ = Shift+Home, Ⓐ = Ctrl+A, ⌥ = Alt tap.
$cases = @(
  @{ name = 'hebrew only';                type = 'שלום לכולם';                        expect = 'L|^שלום לכולם' }
  @{ name = 'hebrew then english';        type = 'שלום world';                         expect = 'L|^שלום world' }
  @{ name = 'english then hebrew';        type = 'hello שלום';                         expect = 'L|^hello שלום' }
  @{ name = 'mixed sentence';             type = 'אני משתמש ב-Zoom כל יום';            expect = 'L|^אני משתמש ב-Zoom כל יום' }
  @{ name = 'mixed with punctuation';     type = 'שלום Zoom, מה נשמע? test 123';      expect = 'L|^שלום Zoom, מה נשמע? test 123' }
  @{ name = 'english sentence';           type = 'see you at 10, thanks';              expect = 'L|^see you at 10, thanks' }
  @{ name = 'multi line';                 type = "abc`nשלום`ndef`nעוד שורה";           expect = "L|^abc`nL|^שלום`nL|^def`nL|^עוד שורה" }
  @{ name = 'multi line mixed';           type = "meeting at 10 בבוקר`nהפגישה ב-10 AM"; expect = "L|^meeting at 10 בבוקר`nL|^הפגישה ב-10 AM" }
  @{ name = 'digits first';               type = '12 - שלום';                          expect = 'L|^12 - שלום' }
  @{ name = 'space first';                type = ' שלום';                              expect = 'L|^ שלום' }
  @{ name = 'backspace: only one marker'; type = "ש`bאבג";                             expect = 'L|^אבג' }
  @{ name = 'english erased, hebrew';     type = "ab`b`bשלום";                         expect = 'L|^שלום' }
  @{ name = 'edit mid-line: no marker';   type = "abc←←X";                             expect = 'L|^aXbc' }
  @{ name = 'home on a fixed line';       type = "abc⇤X";                              expect = 'L|X^abc' }
  @{ name = 'home then hebrew';           type = "world⇤שלום ";                        expect = 'L|שלום ^world' }
  @{ name = 'capital after hebrew line';  type = "שלום`nHello there";                  expect = "L|^שלום`nL|^Hello there" }
  @{ name = 'digits, english after heb.'; type = "שלום`n10 am";                        expect = "L|^שלום`nL|^10 am" }
  @{ name = 'select line, retype';        type = "abc⇱X";                              expect = 'L|X' }
  @{ name = 'select all, retype hebrew';  type = "abcⒶש";                              expect = 'L|^ש' }
  @{ name = 'wrong layout, delete, retype'; type = "c`bבדיקה";                         expect = 'L|^בדיקה' }
  @{ name = 'enter picks a mention';      type = "@ab`nשלום";                         expect = "L|^@ab`nL|שלום" }
  @{ name = 'alt tap, then type';         type = "abc⌥d";                              expect = 'L|^abcd' }
  @{ name = 'backspace joins empty line'; type = "שלום`nab`b`b`bX";                 expect = 'L|^שלוםX' }
  @{ name = 'backspace empties the box';  type = "abc`b`b`b`bX";                   expect = 'L|^X' }
  @{ name = 'typo fixed mid-line';        type = "בדיקה x`bone two three בדיקה";        expect = 'L|^בדיקה one two three בדיקה' }
  @{ name = 'burst: line + next line';    burst = "a`nשלום";                            expect = "L|^a`nL|^שלום" }
  @{ name = 'burst: digits then hebrew';  burst = '12 - שלום';                           expect = 'L|^12 - שלום' }
  @{ name = 'burst: capital next line';   burst = "x`nHello";                           expect = "L|^x`nL|^Hello" }
  @{ name = 'burst: three lines';         burst = "שלום`nhi`nעוד";                       expect = "L|^שלום`nL|^hi`nL|^עוד" }
)
if ($Only) { $cases = @($Only | ForEach-Object { $cases[$_] }) }
if (-not [H]::Activate('ZTDF-TEST')) { throw 'test page window not found' }
Start-Sleep -Milliseconds 500
$startLayout = [H]::CurrentLayout()
$fail = 0
try {
  foreach ($c in $cases) {
    Clear-Box
    if ($c.burst) { if ($english -ne [IntPtr]::Zero) { Use-Layout $english }; Assert-Fg; [H]::Burst($c.burst); Start-Sleep -Milliseconds 300 }
    else { Type-Text $c.type }
    Start-Sleep -Milliseconds 400
    $got = ([H]::FgTitle() -replace '^ZTDF-TEST\|', '')
    $want = $c.expect.Replace('^', [string][char]0x2068).Replace("`n", '⏎')
    $show = { param($s) [regex]::Replace($s, "\u2068", "^") }
    if ([string]::Equals($got, $want, [StringComparison]::Ordinal)) { "PASS  {0,-27} {1}" -f $c.name, (& $show $got) }
    else { $fail++; "FAIL  {0,-27} got:  {1}`n      {2,-27} want: {3}" -f $c.name, (& $show $got), '', (& $show $want) }
  }
} finally {
  if ($Mode -eq 'layout') { Use-Layout $startLayout }
}
"{0} of {1} passed ({2} mode)" -f ($cases.Count - $fail), $cases.Count, $Mode
