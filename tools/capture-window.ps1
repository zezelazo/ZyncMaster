# Captures ONLY the SyncMaster.App window region (not the whole desktop) to a PNG, for
# visual verification of the embedded UI. Finds the window via the running process.
# Usage: powershell -NoProfile -File capture-window.ps1 <output.png>
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinApi {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@
$out = $args[0]
$proc = Get-Process -Name "SyncMaster.App" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Output "NO_WINDOW_HANDLE"; exit 1 }
$h = $proc.MainWindowHandle
Write-Output ("TITLE=" + $proc.MainWindowTitle)
[void][WinApi]::ShowWindow($h, 5)
# Force the window above everything (HWND_TOPMOST) so nothing occludes the screen grab.
[void][WinApi]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x43)  # NOSIZE|NOMOVE|SHOWWINDOW
[void][WinApi]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 900
$r = New-Object WinApi+RECT
[void][WinApi]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top
if ($w -le 0 -or $ht -le 0) { Write-Output "BAD_RECT ${w}x${ht}"; exit 1 }
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $ht)))
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output ("SAVED ${w}x${ht} -> $out")
