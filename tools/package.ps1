# Makes the release files from bin\: dist\UsageTracker-v<ver>.zip and .zip.sha256
# The in-app updater (Updater.cs) needs both assets on the GitHub release.
# Usage: .\tools\package.ps1 [-NoBuild]   then   gh release create v<ver> dist\UsageTracker-v<ver>.zip dist\UsageTracker-v<ver>.zip.sha256 ...
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
if (-not $NoBuild) { & "$root\build.ps1" | Out-Host }
$ver = (Get-Content "$root\VERSION" -Raw).Trim()
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist "UsageTracker-v$ver.zip"
Remove-Item $zip, "$zip.sha256" -ErrorAction SilentlyContinue
Compress-Archive -Path "$root\bin\UsageTracker.exe", "$root\bin\UsageTracker.exe.config", "$root\README.md" -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  UsageTracker-v$ver.zip`n", (New-Object Text.UTF8Encoding $false))
Write-Host "Packaged $zip"
Write-Host "SHA-256 $hash"
