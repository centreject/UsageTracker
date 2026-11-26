# Runs the recorder against a throwaway data folder with short intervals and samples memory/CPU.
# Usage: .\tools\test-run.ps1 [-Seconds 150] [-Kill]   (-Kill: end with Stop-Process to test crash recovery)
param([int]$Seconds = 150, [switch]$Kill, [string]$Data = "$PSScriptRoot\..\testdata", [string]$Extra = '--tick=5 --flush=20 --idle=20')
$exe = Resolve-Path "$PSScriptRoot\..\bin\UsageTracker.exe"
New-Item -ItemType Directory -Force $Data | Out-Null
$p = Start-Process $exe -ArgumentList "--data=`"$Data`" $Extra" -PassThru
Start-Sleep 3
$cpu0 = (Get-Process -Id $p.Id).TotalProcessorTime; $t0 = Get-Date
$peak = 0
while (((Get-Date) - $t0).TotalSeconds -lt $Seconds) {
    Start-Sleep 10
    $w = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($p.Id)"
    if ($w.WorkingSetPrivate -gt $peak) { $peak = $w.WorkingSetPrivate }
    "{0,4:N0}s privateWS={1:N2}MB" -f ((Get-Date) - $t0).TotalSeconds, ($w.WorkingSetPrivate / 1MB)
}
$g = Get-Process -Id $p.Id
$cpu1 = $g.TotalProcessorTime; $el = ((Get-Date) - $t0).TotalSeconds
"CPU {0:N0}ms / {1:N0}s = {2:N4}%  peakPrivateWS={3:N2}MB  privateBytes={4:N1}MB" -f ($cpu1 - $cpu0).TotalMilliseconds, $el, (($cpu1 - $cpu0).TotalMilliseconds / ($el * 10)), ($peak / 1MB), ($g.PrivateMemorySize64 / 1MB)

if ($Kill) { Stop-Process -Id $p.Id; "killed" }
else {
    Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern System.IntPtr FindWindow(string c, string t); [DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);'
    $h = [W.U]::FindWindow('UsageTracker.Recorder', 'UsageTracker.Recorder|' + [IO.Path]::GetFullPath($Data).TrimEnd('\').ToLowerInvariant())   # only our own data folder's recorder
    [void][W.U]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    $p.WaitForExit(5000) | Out-Null
    "closed gracefully: $($p.HasExited)"
}
