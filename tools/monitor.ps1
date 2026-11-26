# Samples the running recorder every few minutes and appends to a CSV, for the 24h resource check.
# Columns: time, pid, private working set (Task Manager "Memory"), working set, private bytes,
#          total CPU seconds, write/read operation counts, handles, threads.
# Usage (detached): Start-Process powershell -WindowStyle Hidden -ArgumentList '-NoProfile -File tools\monitor.ps1 -Hours 24'
param([double]$Hours = 24, [int]$IntervalSec = 300, [string]$Out = (Join-Path $env:LOCALAPPDATA 'UsageTracker\monitor.csv'))
Add-Type -Namespace UTM -Name U -MemberDefinition '[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern System.IntPtr FindWindow(string c, string t); [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);'
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
if (-not (Test-Path $Out)) { 'time,pid,privateWsMB,wsMB,privateBytesMB,cpuSec,writeOps,readOps,handles,threads' | Set-Content $Out -Encoding ascii }
$end = (Get-Date).AddHours($Hours)
while ((Get-Date) -lt $end) {
    $h = [UTM.U]::FindWindow('UsageTracker.Recorder', 'UsageTracker.Recorder|' + (Join-Path $env:LOCALAPPDATA 'UsageTracker').ToLowerInvariant())
    $rpid = 0
    if ($h -ne [IntPtr]::Zero) { [void][UTM.U]::GetWindowThreadProcessId($h, [ref]$rpid) }
    $t = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    if ($rpid -ne 0) {
        $perf = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$rpid" -ErrorAction SilentlyContinue
        $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$rpid" -ErrorAction SilentlyContinue
        $p = Get-Process -Id $rpid -ErrorAction SilentlyContinue
        if ($p -and $proc) {
            '{0},{1},{2:F3},{3:F3},{4:F2},{5:F3},{6},{7},{8},{9}' -f $t, $rpid, ($perf.WorkingSetPrivate / 1MB), ($p.WorkingSet64 / 1MB),
                ($p.PrivateMemorySize64 / 1MB), $p.TotalProcessorTime.TotalSeconds, $proc.WriteOperationCount, $proc.ReadOperationCount,
                $p.HandleCount, $p.Threads.Count | Add-Content $Out -Encoding ascii
        }
    } else { "$t,0,,,,,,,," | Add-Content $Out -Encoding ascii }
    Start-Sleep $IntervalSec
}
