# UsageTracker build script. Uses only the C# compiler built into .NET Framework 4.8.
# Usage: .\build.ps1   -> bin\UsageTracker.exe
# (ASCII only: Windows PowerShell 5.1 reads BOM-less scripts as the ANSI code page.)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$info = Join-Path $out 'AssemblyInfo.g.cs'
@"
[assembly: System.Reflection.AssemblyTitle("UsageTracker")]
[assembly: System.Reflection.AssemblyProduct("UsageTracker")]
[assembly: System.Reflection.AssemblyVersion("$version.0")]
[assembly: System.Reflection.AssemblyFileVersion("$version.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("$version")]
"@ | Set-Content -Encoding UTF8 $info

$sources = @(Get-ChildItem (Join-Path $root 'src') -Filter *.cs -Recurse | ForEach-Object FullName) + $info
$manifest = Join-Path $root 'src\app.manifest'
& $csc /nologo /target:winexe /optimize+ /codepage:65001 /platform:x64 /warn:4 `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll `
    /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    "/win32manifest:$manifest" "/win32icon:$(Join-Path $root 'src\app.ico')" "/out:$(Join-Path $out 'UsageTracker.exe')" $sources
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Copy-Item (Join-Path $root 'src\app.config') (Join-Path $out 'UsageTracker.exe.config') -Force
Write-Host "Built $out\UsageTracker.exe (v$version)"
