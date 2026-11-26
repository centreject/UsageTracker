# Automated event test: sends pause/resume, suspend/resume, time-change and lock/unlock messages
# to a recorder running on a throwaway data folder, then prints the recorded segments.
# Usage: .\tools\test-events.ps1
param([string]$Data = "$PSScriptRoot\..\testdata-events")
$exe = Resolve-Path "$PSScriptRoot\..\bin\UsageTracker.exe"
Remove-Item -Recurse -Force $Data -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $Data | Out-Null
Add-Type -Namespace TE -Name U -MemberDefinition '[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern System.IntPtr FindWindow(string c, string t); [DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);'

$p = Start-Process $exe -ArgumentList "--data=`"$Data`" --tick=2 --flush=60 --idle=0" -PassThru
Start-Sleep 3
$h = [TE.U]::FindWindow('UsageTracker.Recorder', 'UsageTracker.Recorder|' + [IO.Path]::GetFullPath($Data).TrimEnd('\').ToLowerInvariant())
if ($h -eq [IntPtr]::Zero) { throw 'recorder window not found' }
function Send-Msg($name, [uint32]$msg, [int64]$w) {
    "{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $name
    [void][TE.U]::PostMessage($h, $msg, [IntPtr]$w, [IntPtr]::Zero)
}
Start-Sleep 5;  Send-Msg 'pause 15min (WM_COMMAND 20)'       0x0111 20
Start-Sleep 6;  Send-Msg 'resume (WM_COMMAND 23)'            0x0111 23
Start-Sleep 5;  Send-Msg 'suspend (PBT_APMSUSPEND)'          0x0218 4
Start-Sleep 6;  Send-Msg 'resume (PBT_APMRESUMEAUTOMATIC)'   0x0218 0x12
Start-Sleep 5;  Send-Msg 'lock (WTS_SESSION_LOCK)'           0x02B1 7
Start-Sleep 6;  Send-Msg 'unlock (WTS_SESSION_UNLOCK)'       0x02B1 8
Start-Sleep 5;  Send-Msg 'time change (WM_TIMECHANGE)'       0x001E 0
Start-Sleep 5;  Send-Msg 'close (WM_CLOSE)'                  0x0010 0
$p.WaitForExit(5000) | Out-Null
"exited: $($p.HasExited)"
& "$PSScriptRoot\show-sessions.ps1" -Data $Data
if (Test-Path "$Data\error.log") { '--- error.log'; Get-Content "$Data\error.log" -Encoding UTF8 }
