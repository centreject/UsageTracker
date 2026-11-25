# Captures a top-level window (by title) to a PNG for visual checks.
# Usage: .\tools\snap.ps1 -Title '사용 시간 기록' -Out shot.png
param([string]$Title, [string]$Out)
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace SN -Name U -MemberDefinition @'
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern System.IntPtr FindWindow(string c, string t);
[DllImport("user32.dll")] public static extern bool PrintWindow(System.IntPtr h, System.IntPtr hdc, uint flags);
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out RECT r);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
'@
[void][SN.U]::SetProcessDPIAware()   # real pixel coordinates on scaled displays
$h = [SN.U]::FindWindow([NullString]::Value, $Title)
if ($h -eq [IntPtr]::Zero) { throw "window '$Title' not found" }
$r = New-Object SN.U+RECT; [void][SN.U]::GetWindowRect($h, [ref]$r)
$bmp = New-Object Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
$g = [Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
[void][SN.U]::PrintWindow($h, $hdc, 2)   # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save($Out, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
"saved $Out"
