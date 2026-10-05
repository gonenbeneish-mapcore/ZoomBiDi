# Types test sentences (Hebrew, English, mixed) into the test page in Edge and checks where the marker landed.
#   -Mode layout  : real key presses translated by the Hebrew / English keyboard layouts (default, like a person typing)
#   -Mode unicode : characters injected directly (no keyboard layout involved)
# Aborts if the foreground window is not the test page, so keystrokes never go anywhere else.
param([ValidateSet('layout', 'unicode')][string]$Mode = 'layout', [int]$DelayMs = 70, [int[]]$Only)

[Console]::OutputEncoding = [Text.Encoding]::UTF8

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
  [H]::RequestLayout($hkl)
  for ($i = 0; $i -lt 20 -and [H]::CurrentLayout() -ne $hkl; $i++) { Start-Sleep -Milliseconds 25 }
}
function Is-Hebrew([char]$c) { [int]$c -ge 0x0590 -and [int]$c -le 0x05FF }
function Is-Latin([char]$c) { ($c -cmatch '[A-Za-z]') }

function Type-Text([string]$s) {
  foreach ($c in $s.ToCharArray()) {
    Assert-Fg
    if ($c -eq "`n") { [H]::Vk(0x0D) }
    elseif ($c -eq "`b") { [H]::Vk(0x08) }
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

# ^ in "expect" marks where U+2067 should be inserted. `n = Enter, `b = Backspace.
$cases = @(
  @{ name = 'hebrew only';                type = 'שלום לכולם';                        expect = '^שלום לכולם' }
  @{ name = 'hebrew then english';        type = 'שלום world';                         expect = '^שלום world' }
  @{ name = 'english then hebrew';        type = 'hello שלום';                         expect = 'hello שלום' }
  @{ name = 'mixed sentence';             type = 'אני משתמש ב-Zoom כל יום';            expect = '^אני משתמש ב-Zoom כל יום' }
  @{ name = 'mixed with punctuation';     type = 'שלום Zoom, מה נשמע? test 123';      expect = '^שלום Zoom, מה נשמע? test 123' }
  @{ name = 'english sentence';           type = 'see you at 10, thanks';              expect = 'see you at 10, thanks' }
  @{ name = 'multi line';                 type = "abc`nשלום`ndef`nעוד שורה";           expect = "abc`n^שלום`ndef`n^עוד שורה" }
  @{ name = 'multi line mixed';           type = "meeting at 10 בבוקר`nהפגישה ב-10 AM"; expect = "meeting at 10 בבוקר`n^הפגישה ב-10 AM" }
  @{ name = 'digits before hebrew';       type = '12 - שלום';                          expect = '12 - ^שלום' }
  @{ name = 'backspace: only one marker'; type = "ש`bאבג";                             expect = '^אבג' }
  @{ name = 'english erased, hebrew';     type = "ab`b`bשלום";                         expect = '^שלום' }
)

if ($Only) { $cases = @($Only | ForEach-Object { $cases[$_] }) }
if (-not [H]::Activate('ZTDF-TEST')) { throw 'test page window not found' }
Start-Sleep -Milliseconds 500
$startLayout = [H]::CurrentLayout()
$fail = 0
try {
  foreach ($c in $cases) {
    Clear-Box
    Type-Text $c.type
    Start-Sleep -Milliseconds 400
    $got = ([H]::FgTitle() -replace '^ZTDF-TEST\|', '')
    $want = $c.expect.Replace('^', [string][char]0x2067).Replace("`n", '⏎')
    $show = { param($s) [regex]::Replace($s, "\u2067", "[RLI]") }
    if ([string]::Equals($got, $want, [StringComparison]::Ordinal)) { "PASS  {0,-27} {1}" -f $c.name, (& $show $got) }
    else { $fail++; "FAIL  {0,-27} got:  {1}`n      {2,-27} want: {3}" -f $c.name, (& $show $got), '', (& $show $want) }
  }
} finally {
  if ($Mode -eq 'layout') { Use-Layout $startLayout }
}
"{0} of {1} passed ({2} mode)" -f ($cases.Count - $fail), $cases.Count, $Mode
