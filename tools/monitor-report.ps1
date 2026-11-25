# Summarizes monitor.csv against the targets: memory <= 6MB, CPU < 0.1% average, disk writes <= 20/hour.
# Usage: .\tools\monitor-report.ps1 [-Csv path]
param([string]$Csv = (Join-Path $env:LOCALAPPDATA 'UsageTracker\monitor.csv'))
$rows = Import-Csv $Csv | Where-Object { $_.pid -ne '0' -and $_.privateWsMB -ne '' }
if ($rows.Count -lt 2) { "Not enough samples yet ($($rows.Count))."; return }
$byPid = $rows | Group-Object pid | Sort-Object Count -Descending | Select-Object -First 1
$r = $byPid.Group
$first = $r[0]; $last = $r[-1]
$hours = ([datetime]$last.time - [datetime]$first.time).TotalHours
$cpuPct = ([double]$last.cpuSec - [double]$first.cpuSec) / ($hours * 3600) * 100
$writesPerHour = ([double]$last.writeOps - [double]$first.writeOps) / $hours
$mem = $r | ForEach-Object { [double]$_.privateWsMB }
$memStats = $mem | Measure-Object -Average -Maximum
$pb = $r | ForEach-Object { [double]$_.privateBytesMB } | Measure-Object -Minimum -Maximum
$handles = $r | ForEach-Object { [int]$_.handles } | Measure-Object -Minimum -Maximum
$restarts = ($rows | Select-Object -ExpandProperty pid -Unique).Count - 1
"Samples: $($r.Count) over {0:N1} h (pid $($first.pid)), recorder restarts seen: $restarts" -f $hours
"Memory (Task Manager): avg {0:N2} MB, max {1:N2} MB   target <= 6 MB   -> {2}" -f $memStats.Average, $memStats.Maximum, $(if ($memStats.Maximum -le 6) { 'PASS' } else { 'FAIL' })
"CPU average:           {0:N4} %                        target < 0.1 %   -> {1}" -f $cpuPct, $(if ($cpuPct -lt 0.1) { 'PASS' } else { 'FAIL' })
"Disk writes:           {0:N1} per hour                 target <= 20     -> {1}" -f $writesPerHour, $(if ($writesPerHour -le 20) { 'PASS' } else { 'FAIL' })
"Private bytes:         {0:N1} -> {1:N1} MB (growth = possible leak)" -f $pb.Minimum, $pb.Maximum
"Handles:               {0} -> {1}" -f $handles.Minimum, $handles.Maximum
$err = Join-Path (Split-Path $Csv) 'error.log'
"Errors in error.log:   $(if (Test-Path $err) { (Get-Content $err).Count } else { 0 })"
