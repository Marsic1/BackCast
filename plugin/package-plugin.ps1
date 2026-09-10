# Packages the BackCast OBS plugin into the standard layout that plugin
# managers (StreamUP etc.) and manual installs expect:
#
#   backcast-projector-<version>-windows-x64.zip
#     backcast-projector/
#       bin/64bit/backcast-projector.dll
#       data/locale/en-US.ini
#
# Usage:  powershell -File tools/package-plugin.ps1 [-OutDir releases]
# The plugin must already be built (cmake --build --preset windows-x64).

param(
    [string]$OutDir = "releases"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $root "plugin\build_x64\Release\backcast-projector.dll"
$locale = Join-Path $root "plugin\data\locale\en-US.ini"

if (-not (Test-Path $dll)) {
    Write-Error "Plugin DLL not found at $dll - build it first:`n  cd plugin; cmake --preset windows-x64; cmake --build --preset windows-x64"
}

# version from buildspec.json
$spec = Get-Content (Join-Path $root "plugin\buildspec.json") -Raw | ConvertFrom-Json
$version = $spec.version
$zipName = "backcast-projector-$version-windows-x64.zip"

$stage = Join-Path $env:TEMP "backcast-package"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$pkg = Join-Path $stage "backcast-projector"
New-Item -ItemType Directory -Force -Path (Join-Path $pkg "bin\64bit") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $pkg "data\locale") | Out-Null
Copy-Item $dll (Join-Path $pkg "bin\64bit\")
Copy-Item $locale (Join-Path $pkg "data\locale\")

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
$zipPath = Join-Path $OutDir $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath }
Compress-Archive -Path (Join-Path $stage "backcast-projector") -DestinationPath $zipPath
Remove-Item $stage -Recurse -Force

Write-Host "Packaged: $zipPath"
Write-Host ""
Write-Host "Manual install (standard OBS, no admin needed):"
Write-Host "  extract, copy the 'backcast-projector' folder into C:\ProgramData\obs-studio\plugins\"
Write-Host "  (OBS loads: ProgramData\obs-studio\plugins\<name>\bin\64bit\<name>.dll)"
Write-Host "Manual install (portable OBS):"
Write-Host "  copy bin\64bit\backcast-projector.dll  ->  <OBS root>\obs-plugins\64bit\"
Write-Host "  copy data\locale\en-US.ini             ->  <OBS root>\data\obs-plugins\backcast-projector\locale\"
Write-Host "Plugin managers (StreamUP etc.): the zip uses the standard layout they expect."
