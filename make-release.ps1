# Builds the full BackCast release: plugin, exe, and the plugin-only zip —
# and verifies the zip contains the DLL that was just built.
#
# The exe no longer embeds the plugin: it downloads the zip from
# github.com/Marsic1/BackCast/releases/latest at install time, so it can
# never be stale. What CAN be stale is the zip on the GitHub release —
# hence the hash check below.

# Usage:  powershell -File make-release.ps1 [-SkipBuild]
#         (add -SkipBuild to only verify/repackage what's already built)

param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$pluginDll = Join-Path $root "plugin\build_x64\Release\backcast-projector.dll"
$publishExe = Join-Path $root "src\Backcast\bin\Release\net8.0-windows\win-x64\publish\Backcast.exe"
$zip = Join-Path $root "releases\backcast-plugin-windows-x64.zip"

function Step($msg) { Write-Host "`n== $msg" -ForegroundColor Cyan }

if (-not $SkipBuild) {
    Step "Build the OBS plugin"
    Push-Location (Join-Path $root "plugin")
    # reconfigure first: the version from buildspec.json is only read at
    # configure time, so a version bump alone would build a stale stamp
    cmake --preset windows-x64
    if ($LASTEXITCODE -ne 0) { throw "plugin configure failed" }
    cmake --build --preset windows-x64 --config Release
    if ($LASTEXITCODE -ne 0) { throw "plugin build failed" }
    Pop-Location

    Step "Publish the app"
    Push-Location (Join-Path $root "src\Backcast")
    dotnet publish -c Release -r win-x64 --self-contained
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    Pop-Location
}

if (-not (Test-Path $pluginDll)) { throw "plugin DLL not found: $pluginDll" }
if (-not (Test-Path $publishExe)) { throw "publish exe not found: $publishExe (build first, or drop -SkipBuild)" }

Step "Package the plugin zip"
& (Join-Path $root "plugin\package-plugin.ps1")
if (-not (Test-Path $zip)) { throw "zip not found after packaging: $zip" }

Step "Verify the zip contains the plugin DLL that was just built"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$md5 = [System.Security.Cryptography.MD5]::Create()
$pluginHash = ($md5.ComputeHash([System.IO.File]::ReadAllBytes($pluginDll)) | ForEach-Object { $_.ToString("x2") }) -join ""
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entry = $archive.Entries | Where-Object { $_.FullName.Replace('\', '/').EndsWith("bin/64bit/backcast-projector.dll") } | Select-Object -First 1
    if ($null -eq $entry) { throw "the zip has no backcast-projector.dll entry" }
    $ms = New-Object System.IO.MemoryStream
    $entry.Open().CopyTo($ms)
    $zipHash = ($md5.ComputeHash($ms.ToArray()) | ForEach-Object { $_.ToString("x2") }) -join ""
}
finally { $archive.Dispose() }
Write-Host "plugin: $pluginHash"
Write-Host "zip:    $zipHash"
if ($zipHash -ne $pluginHash) {
    throw "STALE ZIP: the packaged plugin does not match the freshly built one - re-run without -SkipBuild"
}
Write-Host "zip contains the freshly built plugin" -ForegroundColor Green

Step "Done"
Write-Host "exe:     $publishExe"
Write-Host "zip:     $zip"
Write-Host "upload both to the GitHub release (gh release upload <tag> Backcast.exe#Backcast.exe backcast-plugin-windows-x64.zip --clobber)"
Write-Host "remove any old versioned plugin zips from the release so /latest/download/ always resolves"
