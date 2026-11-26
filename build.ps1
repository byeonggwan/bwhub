# Builds BWHub with the C# compiler that ships with Windows and installs it to %LOCALAPPDATA%\BwHub.
#   .\build.ps1              build and install
#   .\build.ps1 -NoInstall   build to bin\ only
#   .\build.ps1 -Snapshots   also render docs\*.png (from this PC's installed widgets)
param([switch]$NoInstall, [switch]$Snapshots)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$out = Join-Path $src 'bin'
$dest = Join-Path $env:LOCALAPPDATA 'BwHub'
New-Item -ItemType Directory -Force $out | Out-Null

$refs = 'WPF\PresentationFramework.dll', 'WPF\PresentationCore.dll', 'WPF\WindowsBase.dll', 'System.Xaml.dll',
        'System.Web.Extensions.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll' | ForEach-Object { "/r:$fw\$_" }
# The app version comes from VERSION; release tags must match it (BWUI DESIGN.md §6.10).
$version = (Get-Content "$src\VERSION").Trim()
Set-Content "$out\AppInfo.g.cs" "namespace Bwui { public static class AppInfo { public const string Version = `"$version`"; } }" -Encoding utf8

$files = @(Get-ChildItem "$src\src\*.cs" | ForEach-Object { $_.FullName }) + @("$out\AppInfo.g.cs") +
         @(Get-ChildItem "$src\BWUI\*.cs" | ForEach-Object { $_.FullName })

& $csc /nologo /codepage:65001 /optimize+ /target:winexe "/win32manifest:$src\BWUI\bwui.manifest" "/out:$out\BwHub.exe" @refs @files
if ($LASTEXITCODE) { throw 'Build failed' }
"Built $out\BwHub.exe"

if ($Snapshots) {
    New-Item -ItemType Directory -Force "$src\docs" | Out-Null
    $jobs = @(@('widget-light', 'TodayCalendar', 'light'), @('widget-dark', 'AiUsageWidget', 'dark'), @('settings-light', 'settings', 'light'))
    foreach ($j in $jobs) {
        Start-Process "$out\BwHub.exe" -ArgumentList '--snapshot', "$src\docs\$($j[0]).png", $j[1], $j[2] -Wait
    }
    "Snapshots written to $src\docs"
}
if ($NoInstall) { return }

Get-Process BwHub -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$out\BwHub.exe" $dest -Force
"Installed to $dest\BwHub.exe"
