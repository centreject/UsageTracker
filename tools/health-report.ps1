# Judges the recorder's own resource log (health.tsv) against the targets, per version:
#   memory <= 6MB (working set, an upper bound of Task Manager "Memory"), CPU < 0.1% average,
#   disk writes <= 20/hour, no errors. Sleep/shutdown gaps are excluded: only intervals between two rows
#   of the same process with no suspend/stop in between are counted.
# The log is written all the time in normal use, so no dedicated measuring period is needed:
# after installing a version, just use the PC and run this later.
# Usage: .\tools\health-report.ps1 [-Data <dir>] [-Since 'yyyy-MM-dd HH:mm'] [-Version 0.9.0]
param([string]$Data = (Join-Path $env:LOCALAPPDATA 'UsageTracker'), [string]$Since = '', [string]$Version = '')
$file = Join-Path $Data 'health.tsv'
if (-not (Test-Path $file)) { "No health.tsv yet."; return }
$all = Import-Csv $file -Delimiter "`t" | ForEach-Object {
    [pscustomobject]@{ Time = [datetime]$_.time; Event = $_.event; Version = $_.version; Pid = [int]$_.pid
        Ws = [double]$_.wsMB; Commit = [double]$_.commitMB; Cpu = [double]$_.cpuSec; Writes = [double]$_.writeOps; Handles = [int]$_.handles } }
if ($Since) { $all = $all | Where-Object { $_.Time -ge [datetime]$Since } }
if ($Version) { $all = $all | Where-Object { $_.Version -eq $Version } }
$all = @($all | Sort-Object Time)
if ($all.Count -lt 2) { "Not enough rows yet ($($all.Count))."; return }
$err = Join-Path $Data 'error.log'
$errLines = @()
if (Test-Path $err) { $errLines = @(Get-Content $err -Encoding UTF8) }

function Report($rows, $label) {
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
    # a start not preceded (in this version's rows) by stop/logoff of the previous process = crash, kill or lost session
    $unexpected = 0
    for ($i = 1; $i -lt $rows.Count; $i++) { if ($rows[$i].Event -eq 'start' -and $rows[$i - 1].Event -notin 'stop', 'logoff') { $unexpected++ } }
    $span = ($rows[-1].Time - $rows[0].Time).TotalHours
    $from = $rows[0].Time; $to = $rows[-1].Time.AddHours(1)
    $errs = @($errLines | Where-Object { $_ -match '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) \[(?!recover)' -and [datetime]$Matches[1] -ge $from -and [datetime]$Matches[1] -le $to }).Count

    "=== $label   {0:MM-dd HH:mm} -> {1:MM-dd HH:mm}" -f $rows[0].Time, $rows[-1].Time
    "Rows: $($rows.Count)  span {0:N1} h  awake (measured) {1:N1} h  starts: {2}  unexpected restarts: {3}" -f $span, $hours, @($rows | Where-Object Event -eq 'start').Count, $unexpected
    "Memory (working set, upper bound): max {0:N2} MB   target <= 6 MB -> {1}" -f $wsMax, $(if ($wsMax -le 6) { 'PASS' } else { 'CHECK (Task Manager value is lower)' })
    "CPU average (awake):              {0:N4} %          target < 0.1 % -> {1}" -f $cpuPct, $(if ($cpuPct -lt 0.1) { 'PASS' } else { 'FAIL' })
    "Disk writes (awake):              {0:N1} per hour     target <= 20   -> {1}" -f $wph, $(if ($wph -le 20) { 'PASS' } else { 'FAIL' })
    "Errors in error.log (not recover): $errs"
    "Sleep / resume events: {0} / {1}" -f @($rows | Where-Object Event -eq 'suspend').Count, @($rows | Where-Object Event -eq 'resume').Count
    "--- per process (leak check: commit/handles should level off)"
    $rows | Group-Object Pid | ForEach-Object {
        $g = @($_.Group); $f = $g[0]; $l = $g[-1]
        "  pid {0}: {1:MM-dd HH:mm} -> {2:MM-dd HH:mm}  commit {3:N1} -> {4:N1} MB  handles {5} -> {6}  rows {7}  last event {8}" -f $f.Pid, $f.Time, $l.Time, $f.Commit, $l.Commit, $f.Handles, $l.Handles, $g.Count, $l.Event }
    ''
}

$all | Group-Object Version | ForEach-Object { Report @($_.Group) ("v" + $_.Name) }
