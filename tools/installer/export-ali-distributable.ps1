param(
    [string]$DesktopRoot = (Join-Path $env:USERPROFILE "OneDrive\Desktop")
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$propsPath = Join-Path $repoRoot "Directory.Build.props"
[xml]$props = Get-Content -LiteralPath $propsPath
$version = $props.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Could not read Version from $propsPath"
}

$setupSource = Join-Path $repoRoot "outputs\installer\setup-publish"
$voicePackSource = Join-Path $repoRoot "outputs\installer\voice-pack\Ali.VoicePack.zip"
$voicePatchSource = Join-Path $repoRoot "outputs\installer\voice-pack\Ali.VoicePatch.zip"
$setupDestination = Join-Path $DesktopRoot "Ali Distributable\setup-publish"
$patchDestination = Join-Path $DesktopRoot "Ali_Updates\Patch_$version"

if (!(Test-Path (Join-Path $setupSource "Ali.Setup.exe"))) {
    throw "Setup output was not found. Run build-ali-setup.ps1 first."
}

if (!(Test-Path $voicePackSource)) {
    throw "Voice pack output was not found. Run build-ali-setup.ps1 with -BuildVoicePack."
}

if (!(Test-Path $voicePatchSource)) {
    throw "Voice patch output was not found. Run build-ali-setup.ps1 with -BuildVoicePatch."
}

New-Item -ItemType Directory -Force -Path (Split-Path $setupDestination) | Out-Null
if (Test-Path $setupDestination) {
    Remove-Item -LiteralPath $setupDestination -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $setupDestination | Out-Null
Copy-Item -Path (Join-Path $setupSource "*") -Destination $setupDestination -Recurse -Force
Copy-Item -LiteralPath $voicePackSource -Destination (Join-Path $setupDestination "Ali.VoicePack.zip") -Force
Copy-Item -LiteralPath $voicePatchSource -Destination (Join-Path $setupDestination "Ali.VoicePatch.zip") -Force

New-Item -ItemType Directory -Force -Path $patchDestination | Out-Null
Copy-Item -LiteralPath (Join-Path $setupSource "Ali.Setup.exe") -Destination (Join-Path $patchDestination "Ali.Setup.exe") -Force
Copy-Item -LiteralPath $voicePatchSource -Destination (Join-Path $patchDestination "Ali.VoicePatch.zip") -Force

$runScript = @"
@echo off
setlocal
cd /d "%~dp0"
echo Ali $version repair patch
echo.
echo This repairs the installed Ali app, voice Python runtime, voice bridge scripts, voice settings, and starter Sources ^& Topics.
echo It preserves user chats, memories, settings, and installed voice models.
echo.
Ali.Setup.exe --install-voice-resources --voice-resources .\Ali.VoicePatch.zip --repair
echo.
if errorlevel 1 (
  echo Patch failed. Please send the output above back to Chris.
) else (
  echo Patch complete. Restart Ali and test voice plus Sources ^& Topics.
)
echo.
pause
"@
Set-Content -LiteralPath (Join-Path $patchDestination "Run Ali Patch.cmd") -Value $runScript -Encoding ASCII

$readme = @"
Ali $version Repair Patch

Copy this whole folder to the target computer, then double-click:

Run Ali Patch.cmd

This patch is intentionally small. It includes:
- Ali.Setup.exe
- Ali.VoicePatch.zip
- Run Ali Patch.cmd

It does not include the full multi-GB Ali.VoicePack.zip.
It preserves user data, chats, memories, app settings, and already-installed voice models.
"@
Set-Content -LiteralPath (Join-Path $patchDestination "README.txt") -Value $readme -Encoding ASCII

Write-Host "Full fresh install folder:"
Write-Host $setupDestination
Write-Host
Write-Host "Small repair patch folder:"
Write-Host $patchDestination
