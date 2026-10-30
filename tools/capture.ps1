# Saves a PNG of the QPX1200SPController window. Starts the app from -Exe if it is not running.
# Usage: pwsh -NoProfile -File tools/capture.ps1 -Out shot.png -Exe <path to exe> [-Wait 8] [-Keep]
# -Keep leaves the app running; otherwise it is closed after the screenshot.
param([string]$Out, [string]$Exe, [int]$Wait = 6, [switch]$Keep)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
# $env:QPX1200SP_PID picks one instance when several run (e.g. one in the Visual Studio debugger).
$p = Get-Process QPX1200SPController -ErrorAction SilentlyContinue | Where-Object { -not $env:QPX1200SP_PID -or $_.Id -eq [int]$env:QPX1200SP_PID } | Select-Object -First 1
if (-not $p) { $p = Start-Process $Exe -PassThru; Start-Sleep -Seconds $Wait } else { Start-Sleep -Seconds 1 }
$p.Refresh()
if ($p.HasExited) { "EXITED code $($p.ExitCode)"; exit 1 }
# Find the main window by its title: while a plot's hover popup is open, MainWindowHandle may be the popup.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$win = $A::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
  (New-Object System.Windows.Automation.PropertyCondition ($A::ProcessIdProperty), $p.Id)) |
  Where-Object { $_.Current.Name -like 'QPX1200SP*' } | Select-Object -First 1
$h = if ($win) { [IntPtr]$win.Current.NativeWindowHandle } else { $p.MainWindowHandle }
$r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
$g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
[W]::PrintWindow($h, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc); $g.Dispose(); $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); [IO.File]::WriteAllBytes($Out, $ms.ToArray()); $bmp.Dispose()
"saved $($r.R - $r.L)x$($r.B - $r.T)"
if (-not $Keep) { $p.CloseMainWindow() | Out-Null; Start-Sleep 1; if (-not $p.HasExited) { $p.Kill() } }
