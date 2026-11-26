# Judges the recorder's own resource log (health.tsv) against the targets:
#   memory <= 6MB (working set, an upper bound of Task Manager "Memory"), CPU < 0.1% average,
#   disk writes <= 20/hour, no errors. Sleep/shutdown gaps are excluded: only intervals between two rows
#   of the same process with no suspend/stop in between are counted.
# Usage: .\tools\health-report.ps1 [-Data <dir>] [-Since 'yyyy-MM-dd HH:mm']
param([string]$Data = (Join-Path $env:LOCALAPPDATA 'UsageTracker'), [string]$Since = '')
$file = Join-Path $Data 'health.tsv'
if (-not (Test-Path $file)) { "No health.tsv yet."; return }
$rows = Import-Csv $file -Delimiter "`t" | ForEach-Object {
    [pscustomobject]@{ Time = [datetime]$_.time; Event = $_.event; Version = $_.version; Pid = [int]$_.pid
        Ws = [double]$_.wsMB; Commit = [double]$_.commitMB; Cpu = [double]$_.cpuSec; Writes = [double]$_.writeOps; Handles = [int]$_.handles } }
if ($Since) { $rows = $rows | Where-Object { $_.Time -ge [datetime]$Since } }
$rows = @($rows | Sort-Object Time)
if ($rows.Count -lt 2) { "Not enough rows yet ($($rows.Count))."; return }

$awake = 0.0; $cpu = 0.0; $writes = 0.0
for ($i = 1; $i -lt $rows.Count; $i++) {
    $a = $rows[$i - 1]; $b = $rows[$i]
    if ($a.Pid -ne $b.Pid -or $a.Event -in 'suspend', 'stop', 'logoff') { continue }   # gap: sleep, shutdown, restart
    $sec = ($b.Time - $a.Time).TotalSeconds
    if ($sec -le 0) { continue }
    $awake += $sec; $cpu += ($b.Cpu - $a.Cpu); $writes += ($b.Writes - $a.Writes)
}
$hours = $awake / 3600
$cpuPct = if ($awake -gt 0) { $cpu / $awake * 100 } else { 0 }
$wph = if ($hours -gt 0) { $writes / $hours } else { 0 }
$wsMax = ($rows | Measure-Object Ws -Maximum).Maximum
$starts = @($rows | Where-Object Event -eq 'start')
$unexpected = 0
for ($i = 1; $i -lt $rows.Count; $i++) { if ($rows[$i].Event -eq 'start' -and $rows[$i - 1].Event -notin 'stop', 'logoff') { $unexpected++ } }
$span = ($rows[-1].Time - $rows[0].Time).TotalHours

"Rows: $($rows.Count)  span {0:N1} h  awake (measured) {1:N1} h  versions: {2}" -f $span, $hours, (($rows.Version | Select-Object -Unique) -join ',')
"Starts: $($starts.Count)  unexpected restarts (no stop before start = crash or kill): $unexpected"
"Memory (working set, upper bound): max {0:N2} MB   target <= 6 MB -> {1}" -f $wsMax, $(if ($wsMax -le 6) { 'PASS' } else { 'CHECK (Task Manager value is lower)' })
"CPU average (awake):              {0:N4} %          target < 0.1 % -> {1}" -f $cpuPct, $(if ($cpuPct -lt 0.1) { 'PASS' } else { 'FAIL' })
"Disk writes (awake):              {0:N1} per hour     target <= 20   -> {1}" -f $wph, $(if ($wph -le 20) { 'PASS' } else { 'FAIL' })
"--- per process (leak check: commit/handles should level off)"
$rows | Group-Object Pid | ForEach-Object {
    $g = @($_.Group); $f = $g[0]; $l = $g[-1]
    "  pid {0} v{1}: {2:MM-dd HH:mm} -> {3:MM-dd HH:mm}  commit {4:N1} -> {5:N1} MB  handles {6} -> {7}  rows {8}" -f $f.Pid, $f.Version, $f.Time, $l.Time, $f.Commit, $l.Commit, $f.Handles, $l.Handles, $g.Count }
$err = Join-Path $Data 'error.log'
$errs = 0
if (Test-Path $err) { $from = $rows[0].Time; $errs = @(Get-Content $err -Encoding UTF8 | Where-Object { $_ -match '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) \[(?!recover)' -and [datetime]$Matches[1] -ge $from }).Count }
"Errors in error.log since first row: $errs"
