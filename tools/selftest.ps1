# Runs the built-in self test (fake clock/windows/input, real file writing and reading) and prints the report.
# Usage: .\tools\selftest.ps1     exit code = number of failures
$exe = Resolve-Path "$PSScriptRoot\..\bin\UsageTracker.exe"
$report = Join-Path $env:TEMP 'UsageTracker-selftest.txt'
Remove-Item $report -ErrorAction SilentlyContinue
$p = Start-Process $exe -ArgumentList "--selftest=`"$report`"" -PassThru -Wait
Get-Content $report -Encoding UTF8
exit $p.ExitCode
