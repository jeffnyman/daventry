param(
    [string]$Exe,
    [string[]]$ExeArgs,
    [string]$Script,      # steps separated by ';' : "wait:ms" | "keys:<text with {ENTER} {F5} {F7} {ESC} {SPACE} {UP} {DOWN} {LEFT} {RIGHT} {TAB}>" | "shot:<png>" | "click:x,y" (window client coords, logical 320x200 scaled)
    [int]$StartupMs = 6000
)
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
  [DllImport("user32.dll")] public static extern short VkKeyScan(char ch);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }
  public static void Key(ushort scan, bool extended, bool shift) { Key(scan, extended, shift, false, false); }
  public static void Key(ushort scan, bool extended, bool shift, bool ctrl, bool alt) {
    var list = new System.Collections.Generic.List<INPUT>();
    uint ext = extended ? 1u : 0u;
    if (ctrl) { var s = new INPUT(); s.type = 1; s.u.ki.wScan = 0x1D; s.u.ki.dwFlags = 8; list.Add(s); }
    if (alt) { var s = new INPUT(); s.type = 1; s.u.ki.wScan = 0x38; s.u.ki.dwFlags = 8; list.Add(s); }
    if (shift) { var s = new INPUT(); s.type = 1; s.u.ki.wScan = 0x2A; s.u.ki.dwFlags = 8; list.Add(s); }
    var d = new INPUT(); d.type = 1; d.u.ki.wScan = scan; d.u.ki.dwFlags = 8 | ext; list.Add(d);
    var u = new INPUT(); u.type = 1; u.u.ki.wScan = scan; u.u.ki.dwFlags = 8 | 2 | ext; list.Add(u);
    if (shift) { var s = new INPUT(); s.type = 1; s.u.ki.wScan = 0x2A; s.u.ki.dwFlags = 8 | 2; list.Add(s); }
    if (alt) { var s = new INPUT(); s.type = 1; s.u.ki.wScan = 0x38; s.u.ki.dwFlags = 8 | 2; list.Add(s); }
    if (ctrl) { var s = new INPUT(); s.type = 1; s.u.ki.wScan = 0x1D; s.u.ki.dwFlags = 8 | 2; list.Add(s); }
    SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(INPUT)));
  }
  public static void Click() {
    var d = new INPUT(); d.type = 0; d.u.mi.dwFlags = 2;
    var u = new INPUT(); u.type = 0; u.u.mi.dwFlags = 4;
    SendInput(1, new INPUT[] { d }, Marshal.SizeOf(typeof(INPUT)));
    System.Threading.Thread.Sleep(120);
    SendInput(1, new INPUT[] { u }, Marshal.SizeOf(typeof(INPUT)));
  }
}
"@
[void][Win]::SetProcessDPIAware()
$special = @{ 'ENTER' = @(0x1C, $false); 'F5' = @(0x3F, $false); 'F7' = @(0x41, $false); 'F1' = @(0x3B, $false); 'F2' = @(0x3C, $false); 'F3' = @(0x3D, $false); 'F4' = @(0x3E, $false); 'F6' = @(0x40, $false); 'F8' = @(0x42, $false); 'F9' = @(0x43, $false); 'F10' = @(0x44, $false); 'F11' = @(0x57, $false); 'F12' = @(0x58, $false); 'ESC' = @(0x01, $false); 'SPACE' = @(0x39, $false); 'TAB' = @(0x0F, $false); 'UP' = @(0x48, $true); 'DOWN' = @(0x50, $true); 'LEFT' = @(0x4B, $true); 'RIGHT' = @(0x4D, $true); 'BS' = @(0x0E, $false) }

function Send-Text([string]$text) {
    $i = 0
    while ($i -lt $text.Length) {
        $c = $text[$i]
        if ($c -eq '{') {
            $end = $text.IndexOf('}', $i)
            $name = $text.Substring($i + 1, $end - $i - 1)
            if ($name.Contains('+')) {
                # a chord such as CTRL+ALT+d: modifiers, then one key (a special name or a character)
                $parts = $name.Split('+')
                $ctrl = $parts -contains 'CTRL'; $alt = $parts -contains 'ALT'; $sh = $parts -contains 'SHIFT'
                $last = $parts[$parts.Length - 1]
                if ($special.ContainsKey($last)) { $def = $special[$last]; $scan = [uint16]$def[0]; $ext = [bool]$def[1] }
                else { $vk = [Win]::VkKeyScan([char]$last[0]); $scan = [uint16][Win]::MapVirtualKey([uint32]($vk -band 0xFF), 0); $ext = $false }
                [Win]::Key($scan, $ext, $sh, $ctrl, $alt)
            } else {
                $def = $special[$name]
                [Win]::Key([uint16]$def[0], [bool]$def[1], $false)
            }
            $i = $end + 1
        } else {
            $vk = [Win]::VkKeyScan($c)
            $shift = (($vk -shr 8) -band 1) -ne 0
            $scan = [Win]::MapVirtualKey([uint32]($vk -band 0xFF), 0)
            [Win]::Key([uint16]$scan, $false, $shift)
            $i++
        }
        Start-Sleep -Milliseconds 60
    }
}

$p = Start-Process -FilePath $Exe -ArgumentList $ExeArgs -PassThru
Start-Sleep -Milliseconds $StartupMs
$p.Refresh()
$h = $p.MainWindowHandle
if ($h -eq 0) { Write-Output "no window" }
[void][Win]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 300
foreach ($step in $Script.Split(';')) {
    $kind, $arg = $step.Split(':', 2)
    switch ($kind) {
        'wait' { Start-Sleep -Milliseconds ([int]$arg) }
        'keys' { [void][Win]::SetForegroundWindow($h); Send-Text $arg; Write-Output "sent $arg" }
        'click' {
            $x, $y = $arg.Split(',') | ForEach-Object { [int]$_ }
            $cr = New-Object Win+RECT
            [void][Win]::GetClientRect($h, [ref]$cr)
            $pt = New-Object Win+POINT
            $pt.X = [int]($x * ($cr.R - $cr.L) / 320); $pt.Y = [int]($y * ($cr.B - $cr.T) / 200)
            [void][Win]::ClientToScreen($h, [ref]$pt)
            [void][Win]::SetForegroundWindow($h)
            [void][Win]::SetCursorPos($pt.X, $pt.Y)
            Start-Sleep -Milliseconds 150
            [Win]::Click()
            Write-Output "clicked $x,$y -> $($pt.X),$($pt.Y)"
        }
        'shot' {
            $p.Refresh()
            $r = New-Object Win+RECT
            [void][Win]::GetWindowRect($p.MainWindowHandle, [ref]$r)
            $w = $r.R - $r.L; $hh = $r.B - $r.T
            $bmp = New-Object System.Drawing.Bitmap $w, $hh
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $hh))
            $small = New-Object System.Drawing.Bitmap $bmp, ([int]($w / 2)), ([int]($hh / 2))
            $small.Save($arg, [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Output "shot ${w}x${hh} -> $arg"
        }
    }
}
if (-not $p.HasExited) { $p.Kill() }
Write-Output "done"
