# Builds the full BackCast release: plugin, exe (with the plugin embedded),
# and the plugin-only zip — and verifies the exe actually embeds the DLL that
# was just built (a stale exe silently installs an old plugin).
#
# Usage:  powershell -File make-release.ps1 [-SkipBuild]
#         (add -SkipBuild to only verify/repackage what's already built)

param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$pluginDll = Join-Path $root "plugin\build_x64\Release\backcast-projector.dll"
$appDll = Join-Path $root "src\Backcast\bin\Release\net8.0-windows\win-x64\Backcast.dll"
$publishExe = Join-Path $root "src\Backcast\bin\Release\net8.0-windows\win-x64\publish\Backcast.exe"

function Step($msg) { Write-Host "`n== $msg" -ForegroundColor Cyan }

if (-not $SkipBuild) {
    Step "Build the OBS plugin"
    Push-Location (Join-Path $root "plugin")
    cmake --build --preset windows-x64 --config Release
    if ($LASTEXITCODE -ne 0) { throw "plugin build failed" }
    Pop-Location

    Step "Publish the app (embeds the plugin DLL as a resource)"
    Push-Location (Join-Path $root "src\Backcast")
    dotnet publish -c Release -r win-x64 --self-contained
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    Pop-Location
}

if (-not (Test-Path $pluginDll)) { throw "plugin DLL not found: $pluginDll" }
if (-not (Test-Path $appDll)) { throw "app assembly not found: $appDll (build first, or drop -SkipBuild)" }
if (-not (Test-Path $publishExe)) { throw "publish exe not found: $publishExe" }

Step "Verify the exe embeds the plugin DLL that was just built"
$md5 = [System.Security.Cryptography.MD5]::Create()
$pluginHash = ($md5.ComputeHash([System.IO.File]::ReadAllBytes($pluginDll)) | ForEach-Object { $_.ToString("x2") }) -join ""
$asm = [System.Reflection.Assembly]::LoadFile($appDll)
$stream = $asm.GetManifestResourceStream("Backcast.assets.backcast-projector.dll")
if ($null -eq $stream) { throw "embedded resource 'Backcast.assets.backcast-projector.dll' missing from the app assembly" }
$ms = New-Object System.IO.MemoryStream
$stream.CopyTo($ms)
$embeddedHash = ($md5.ComputeHash($ms.ToArray()) | ForEach-Object { $_.ToString("x2") }) -join ""
Write-Host "plugin:  $pluginHash"
Write-Host "embedded: $embeddedHash"
if ($embeddedHash -ne $pluginHash) {
    throw "STALE EXE: the embedded plugin does not match the freshly built one - re-run without -SkipBuild"
}
Write-Host "embedded plugin matches the build" -ForegroundColor Green

Step "Package the plugin zip"
& (Join-Path $root "plugin\package-plugin.ps1")

Step "Done"
Write-Host "exe:     $publishExe"
Write-Host "zip:     $root\releases\backcast-projector-*-windows-x64.zip"
Write-Host "upload both to the GitHub release (gh release upload <tag> ...)"
