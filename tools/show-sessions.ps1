# Prints recorded segments in time order with app names, plus totals per kind and app.
# Usage: .\tools\show-sessions.ps1 [-Data testdata] [-Since '2026-11-25 13:00:00']
param([string]$Data = "$PSScriptRoot\..\testdata", [string]$Since = '')
$names = @{}
$info = Join-Path $Data 'appinfo.tsv'
if (Test-Path $info) {
    foreach ($l in Get-Content $info -Encoding UTF8) { $p = $l -split "`t"; if ($p.Count -ge 2 -and $p[0] -ne 'V') { $names[$p[0]] = $p[1] } }
}
$kinds = @{ A = 'active'; R = 'running'; P = 'pc-on'; U = 'pc-used' }
$rows = foreach ($f in Get-ChildItem $Data -Filter 'sessions-*.tsv') {
    foreach ($l in Get-Content $f.FullName -Encoding UTF8) {
        $p = $l -split "`t"
        if ($p.Count -eq 4 -and $kinds.ContainsKey($p[0])) {
            $s = [datetime]$p[1]; $e = [datetime]$p[2]
            if ($Since -and $s -lt [datetime]$Since -and $e -lt [datetime]$Since) { continue }
            $n = if ($p[3] -eq '-') { '' } elseif ($names.ContainsKey($p[3])) { $names[$p[3]] } else { $p[3] }
            [pscustomobject]@{ Kind = $kinds[$p[0]]; Start = $s; End = $e; Sec = [int]($e - $s).TotalSeconds; App = $n }
        }
    }
}
"== Timeline (active / pc-used / pc-on)"
$rows | Where-Object { $_.Kind -ne 'running' } | Sort-Object Start, Kind |
    Format-Table Kind, @{n='Start';e={$_.Start.ToString('HH:mm:ss')}}, @{n='End';e={$_.End.ToString('HH:mm:ss')}}, Sec, App -AutoSize | Out-String -Width 200
"== Running segments"
$rows | Where-Object { $_.Kind -eq 'running' } | Sort-Object App, Start |
    Format-Table App, @{n='Start';e={$_.Start.ToString('HH:mm:ss')}}, @{n='End';e={$_.End.ToString('HH:mm:ss')}}, Sec -AutoSize | Out-String -Width 200
"== Totals (seconds)"
$rows | Group-Object Kind, App | ForEach-Object { [pscustomobject]@{ Kind = $_.Group[0].Kind; App = $_.Group[0].App; Sec = ($_.Group | Measure-Object Sec -Sum).Sum } } |
    Sort-Object Kind, @{e='Sec';Descending=$true} | Format-Table -AutoSize | Out-String -Width 200
