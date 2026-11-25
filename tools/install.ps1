# Installs (or updates) UsageTracker for the current user and starts it.
#   - copies bin\UsageTracker.exe(.config) to %LOCALAPPDATA%\Programs\UsageTracker
#   - asks a running recorder to close first (it saves its data on close)
#   - starts the installed exe; it registers itself for auto start on first run
# Usage: .\tools\install.ps1 [-NoBuild]
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
if (-not $NoBuild) { & "$root\build.ps1" | Out-Host }
$dest = Join-Path $env:LOCALAPPDATA 'Programs\UsageTracker'
New-Item -ItemType Directory -Force $dest | Out-Null

Add-Type -Namespace UTI -Name U -MemberDefinition '[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern System.IntPtr FindWindow(string c, string t); [DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);'
$h = [UTI.U]::FindWindow('UsageTracker.Recorder', [NullString]::Value)
if ($h -ne [IntPtr]::Zero) {
    [void][UTI.U]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE: save and exit
    for ($i = 0; $i -lt 50 -and [UTI.U]::FindWindow('UsageTracker.Recorder', [NullString]::Value) -ne [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 100 }
    Write-Host 'Closed the running recorder.'
}
Copy-Item "$root\bin\UsageTracker.exe", "$root\bin\UsageTracker.exe.config" $dest -Force
$exe = Join-Path $dest 'UsageTracker.exe'
Start-Process $exe
Write-Host "Installed and started: $exe (v$((Get-Item $exe).VersionInfo.ProductVersion))"
