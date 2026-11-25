# Creates a demo data folder with ~2 weeks of plausible usage, for looking at the UI.
# Usage: .\tools\make-demo-data.ps1 [-Data testdata-demo] [-Days 14]
param([string]$Data = "$PSScriptRoot\..\testdata-demo", [int]$Days = 14)
Remove-Item -Recurse -Force $Data -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $Data | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
$apps = @(
    [pscustomobject]@{ id = 'chrome.exe';      name = 'Google Chrome';  path = 'C:\Program Files\Google\Chrome\Application\chrome.exe'; w = 30 },
    [pscustomobject]@{ id = 'kakaotalk.exe';   name = 'KakaoTalk';      path = 'C:\Program Files\Kakao\KakaoTalk\KakaoTalk.exe'; w = 12 },
    [pscustomobject]@{ id = 'antigravity.exe'; name = 'Antigravity';    path = "$env:LOCALAPPDATA\Programs\antigravity\Antigravity.exe"; w = 25 },
    [pscustomobject]@{ id = 'claude.exe';      name = 'Claude';         path = 'C:\Program Files\WindowsApps\Claude\app\claude.exe'; w = 14 },
    [pscustomobject]@{ id = 'notepad.exe';     name = 'Windows 메모장'; path = 'C:\Windows\notepad.exe'; w = 6 },
    [pscustomobject]@{ id = 'explorer.exe';    name = 'Windows 탐색기'; path = 'C:\Windows\explorer.exe'; w = 7 },
    [pscustomobject]@{ id = '::desktop';       name = '바탕화면';       path = ''; w = 3 },
    [pscustomobject]@{ id = 'systemsettings.exe'; name = '설정';        path = 'C:\Windows\ImmersiveControlPanel\SystemSettings.exe'; w = 3 }
)
$info = "V`t1`r`n" + (($apps | ForEach-Object { "$($_.id)`t$($_.name)`t$($_.path)`t2026-11-01 09:00:00" }) -join "`r`n") + "`r`n"
[IO.File]::WriteAllText((Join-Path $Data 'appinfo.tsv'), $info, $utf8)

$rnd = New-Object Random 7
$fmt = 'yyyy-MM-dd HH:mm:ss'
$byFile = @{}
function Add-Line($t, $line) { $k = Join-Path $Data ('sessions-' + $t.ToString('yyyy-MM') + '.tsv'); if (-not $byFile[$k]) { $byFile[$k] = New-Object Text.StringBuilder "V`t1`r`n" }; [void]$byFile[$k].Append($line + "`r`n") }
function Pick { $total = ($apps | Measure-Object w -Sum).Sum; $r = $rnd.Next($total); foreach ($a in $apps) { if ($r -lt $a.w) { return $a }; $r -= $a.w }; $apps[0] }

$today = (Get-Date).Date
for ($d = $Days - 1; $d -ge 0; $d--) {
    $day = $today.AddDays(-$d)
    $start = $day.AddHours(9).AddMinutes($rnd.Next(60))
    $end = if ($d -eq 0) { (Get-Date).AddMinutes(-1) } else { $day.AddHours(18 + $rnd.Next(5)).AddMinutes($rnd.Next(60)) }
    if ($end -le $start) { continue }
    Add-Line $day ("P`t" + $start.ToString($fmt) + "`t" + $end.ToString($fmt) + "`t-")
    foreach ($a in $apps | Where-Object { $_.id -notin @('explorer.exe', '::desktop', 'notepad.exe') }) {
        Add-Line $day ("R`t" + $start.AddMinutes($rnd.Next(30)).ToString($fmt) + "`t" + $end.ToString($fmt) + "`t" + $a.id)
    }
    $t = $start
    $usedStart = $t
    while ($t -lt $end) {
        if ($rnd.Next(25) -eq 0) {   # lunch / away break
            Add-Line $day ("U`t" + $usedStart.ToString($fmt) + "`t" + $t.ToString($fmt) + "`t-")
            $t = $t.AddMinutes(20 + $rnd.Next(50)); $usedStart = $t; continue
        }
        $a = Pick
        $e = $t.AddSeconds(30 + $rnd.Next(900)); if ($e -gt $end) { $e = $end }
        Add-Line $day ("A`t" + $t.ToString($fmt) + "`t" + $e.ToString($fmt) + "`t" + $a.id)
        $t = $e
    }
    Add-Line $day ("U`t" + $usedStart.ToString($fmt) + "`t" + $end.ToString($fmt) + "`t-")
}
foreach ($k in $byFile.Keys) { [IO.File]::WriteAllText($k, $byFile[$k].ToString(), $utf8) }
"demo data: $Data ($Days days)"
