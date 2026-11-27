# Accelerated soak test: runs the recorder on a throwaway data folder with a 1-second tick (60x the normal
# 1-minute tick), so N minutes = about N hours of normal ticks, flushes and queries. Every simulated "hour"
# (60 s) it also sends one round of events: pause/resume, suspend/resume, lock/unlock, settings reload,
# flush and time change. Leaks grow with ticks and events, not with wall-clock time, so this replaces a long
# wait for the leak check. It cannot replace real sleep, login autostart or midnight on a real PC.
#
# Reference run: the same test runs at the same time on a reference exe (by default the installed release,
# copied to a temp folder). The recorder looks at real windows and audio sessions, so something happening on
# the PC (a new app or audio device) can raise handles in both processes at once. Growth is therefore judged
# relative to the reference: a step that the reference shows too is the environment, not a leak.
#
# Pass: growth after warm-up (beyond the reference's) within limits, no errors, still running, clean exit.
# Usage: .\tools\soak.ps1 [-Minutes 24] [-Exe <candidate>] [-Reference <exe> | -NoReference]
#        exit code = number of failed checks
# (ASCII only: Windows PowerShell 5.1 reads BOM-less scripts as the ANSI code page.)
param([int]$Minutes = 24, [string]$Exe = "$PSScriptRoot\..\bin\UsageTracker.exe",
      [string]$Reference = (Join-Path $env:LOCALAPPDATA 'Programs\UsageTracker\UsageTracker.exe'), [switch]$NoReference,
      [int]$MaxHandleGrowth = 15, [double]$MaxCommitGrowthMB = 1.5)
$ErrorActionPreference = 'Stop'
Add-Type -Namespace SK -Name U -MemberDefinition '[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern System.IntPtr FindWindow(string c, string t); [DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);'
$work = Join-Path $env:TEMP ('UsageTracker-soak-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $work | Out-Null

function Start-Recorder($label, $exe) {
    $data = Join-Path $work "data-$label"
    New-Item -ItemType Directory -Force $data | Out-Null
    # --idle=20: idle starts after 20 s without input, so idle/return and the 2-second idle poll are exercised too
    $p = Start-Process $exe -ArgumentList "`"--data=$data`" --tick=1 --flush=5 --idle=20" -PassThru
    $title = 'UsageTracker.Recorder|' + [IO.Path]::GetFullPath($data).TrimEnd('\').ToLowerInvariant()
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 50 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 100; $h = [SK.U]::FindWindow('UsageTracker.Recorder', $title) }
    if ($h -eq [IntPtr]::Zero) { throw "recorder window not found ($label)" }
    [pscustomobject]@{ Label = $label; Version = (Get-Item $exe).VersionInfo.ProductVersion; P = $p; H = $h; Data = $data
        Samples = (New-Object Collections.Generic.List[object]) }
}
function Post($r, [uint32]$msg, [int64]$w) { if (-not $r.P.HasExited) { [void][SK.U]::PostMessage($r.H, $msg, [IntPtr]$w, [IntPtr]::Zero) } }
function EventRound($recs) {
    foreach ($pair in @(@(@(0x0111, 20), @(0x0111, 23)), @(@(0x0218, 4), @(0x0218, 0x12)), @(@(0x02B1, 7), @(0x02B1, 8)))) {
        foreach ($r in $recs) { Post $r $pair[0][0] $pair[0][1] }; Start-Sleep 2    # pause / suspend / lock
        foreach ($r in $recs) { Post $r $pair[1][0] $pair[1][1] }; Start-Sleep 2    # resume / resume / unlock
    }
    foreach ($r in $recs) { Post $r 0x0111 60; Post $r 0x0111 50; Post $r 0x001E 0 }   # reload settings, flush, time change
}
function Sample($r, $min) {
    $r.P.Refresh()
    $s = [pscustomobject]@{ Min = $min; Handles = $r.P.HandleCount; Threads = $r.P.Threads.Count
        CommitMB = [math]::Round($r.P.PrivateMemorySize64 / 1MB, 2); WsMB = [math]::Round($r.P.WorkingSet64 / 1MB, 2); CpuSec = [math]::Round($r.P.TotalProcessorTime.TotalSeconds, 2) }
    $r.Samples.Add($s); $s
}

$recs = @(Start-Recorder 'candidate' (Resolve-Path $Exe).Path)
if (-not $NoReference) {
    if (Test-Path $Reference) {
        $refDir = Join-Path $work 'reference-exe'; New-Item -ItemType Directory -Force $refDir | Out-Null
        Copy-Item "$Reference*" $refDir   # exe + .config; a copy so the installed file is never locked
        $recs += Start-Recorder 'reference' (Join-Path $refDir (Split-Path $Reference -Leaf))
    } else { "Reference exe not found ($Reference): judging without it" }
}
"Soak: {0} min (about {0} h of 1-minute ticks). {1}. Work folder {2}" -f $Minutes, (($recs | ForEach-Object { "$($_.Label) v$($_.Version)" }) -join ', '), $work

$t0 = Get-Date; $nextEvent = $t0.AddSeconds(30); $nextSample = $t0; $n = 0
while (((Get-Date) - $t0).TotalMinutes -lt $Minutes) {
    if (@($recs | Where-Object { $_.P.HasExited }).Count -gt 0) { break }
    $now = Get-Date
    if ($now -ge $nextEvent) { EventRound $recs; $nextEvent = $nextEvent.AddSeconds(60) }
    if ($now -ge $nextSample) {
        $min = [math]::Round(($now - $t0).TotalMinutes, 1)
        $line = "{0,5} min" -f $min
        foreach ($r in $recs) { $s = Sample $r $min; $line += "  | {0}: handles {1,4} commit {2,6:N2} MB" -f $r.Label, $s.Handles, $s.CommitMB }
        if ($n % 4 -eq 0) { $line }
        $n++; $nextSample = $nextSample.AddSeconds(30)
    }
    Start-Sleep -Milliseconds 500
}
foreach ($r in $recs) {
    $r | Add-Member Crashed $r.P.HasExited
    if (-not $r.Crashed) { Post $r 0x0010 0; $r | Add-Member Clean $r.P.WaitForExit(5000) } else { $r | Add-Member Clean $false }
    $r.Samples | Export-Csv (Join-Path $work "soak-$($r.Label).csv") -NoTypeInformation
}

# growth: warm-up point (25% in, at least 2 min) vs the average of the last 4 samples
$warm = [math]::Max(2, $Minutes * 0.25)
function Growth($r) {
    $after = @($r.Samples | Where-Object { $_.Min -ge $warm })
    if ($after.Count -lt 6) { return $null }
    $base = $after[0]; $tail = @($after | Select-Object -Last 4)
    $last = $r.Samples[$r.Samples.Count - 1]
    [pscustomobject]@{ BaseH = $base.Handles; Dh = ($tail | Measure-Object Handles -Average).Average - $base.Handles
        BaseC = $base.CommitMB; Dc = ($tail | Measure-Object CommitMB -Average).Average - $base.CommitMB
        Threads = "$($base.Threads) -> $($last.Threads)"; Cpu = $last.CpuSec / ($last.Min * 60) * 100 }
}
$fail = 0
$g = @{}; foreach ($r in $recs) { $g[$r.Label] = Growth $r }
if ($g['candidate'] -eq $null) { "Too few samples after warm-up"; exit 99 }
''
foreach ($r in $recs) {
    $x = $g[$r.Label]
    "{0} v{1}: handles {2} {3:+0.0;-0.0}, commit {4:N2} MB {5:+0.00;-0.00}, threads {6}, CPU at 60x {7:N3} % (normal tick ~{8:N4} %)" -f $r.Label, $r.Version, $x.BaseH, $x.Dh, $x.BaseC, $x.Dc, $x.Threads, $x.Cpu, ($x.Cpu / 60)
}
$c = $g['candidate']; $ref = $g['reference']
$dh = $c.Dh; $dc = $c.Dc; $how = 'absolute'
if ($ref -ne $null) { $dh = $c.Dh - [math]::Max(0, $ref.Dh); $dc = $c.Dc - [math]::Max(0, $ref.Dc); $how = 'beyond reference' }
"Handle growth ($how): {0:N1} (limit {1})  {2}" -f $dh, $MaxHandleGrowth, $(if ($dh -le $MaxHandleGrowth) { 'PASS' } else { $fail++; 'FAIL' })
"Commit growth ($how): {0:N2} MB (limit {1})  {2}" -f $dc, $MaxCommitGrowthMB, $(if ($dc -le $MaxCommitGrowthMB) { 'PASS' } else { $fail++; 'FAIL' })
foreach ($r in $recs) {
    $ok = (-not $r.Crashed) -and $r.Clean
    if ($r.Label -eq 'candidate' -and -not $ok) { $fail++ }
    "{0}: ran to the end {1}, clean exit {2}  {3}" -f $r.Label, (-not $r.Crashed), $r.Clean, $(if ($ok) { 'PASS' } elseif ($r.Label -eq 'candidate') { 'FAIL' } else { 'reference problem' })
}
$err = Join-Path $recs[0].Data 'error.log'
if (Test-Path $err) { $fail++; 'candidate error.log: FAIL'; Get-Content $err -Encoding UTF8 } else { 'candidate error.log: none  PASS' }
"Failed checks: $fail   (samples: $work)"
exit $fail
