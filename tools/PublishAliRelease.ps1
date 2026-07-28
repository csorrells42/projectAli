[CmdletBinding()]
param(
    [string]$DesktopPath,
    [switch]$VerifyOnly,
    [switch]$KeepIntermediates
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$publishRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'bin\Release\Ali')).TrimEnd('\')
$publishExecutable = Join-Path $publishRoot 'Ali.exe'

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Parent
    )

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not ($fullPath + '\').StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing path outside '$Parent': $fullPath"
    }

    return $fullPath
}

if (-not $VerifyOnly) {
    $safePublishRoot = Assert-ChildPath -Path $publishRoot -Parent $repoRoot
    if (Test-Path -LiteralPath $safePublishRoot) {
        Remove-Item -LiteralPath $safePublishRoot -Recurse -Force
    }

    & dotnet publish (Join-Path $repoRoot 'src\Ali.csproj') `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -o $safePublishRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Ali Release publish failed with exit code $LASTEXITCODE."
    }
}

& powershell -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $PSScriptRoot 'TestPublishedBundle.ps1') `
    -PublishRoot $publishRoot
if ($LASTEXITCODE -ne 0) {
    throw "Ali Release bundle validation failed with exit code $LASTEXITCODE."
}

if ([string]::IsNullOrWhiteSpace($DesktopPath)) {
    $oneDriveDesktop = Join-Path $env:USERPROFILE 'OneDrive\Desktop'
    $DesktopPath = if (Test-Path -LiteralPath $oneDriveDesktop -PathType Container) {
        $oneDriveDesktop
    }
    else {
        [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
    }
}

$desktopRoot = [IO.Path]::GetFullPath($DesktopPath)
if (-not (Test-Path -LiteralPath $desktopRoot -PathType Container)) {
    throw "Desktop folder does not exist: $desktopRoot"
}
if (-not (Test-Path -LiteralPath $publishExecutable -PathType Leaf)) {
    throw "Published Ali executable is missing: $publishExecutable"
}

$shortcutPath = Join-Path $desktopRoot 'Ali Dev Run.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $publishExecutable
$shortcut.WorkingDirectory = $publishRoot
$shortcut.IconLocation = "$publishExecutable,0"
$shortcut.Description = 'Launch the current ready-to-demo Ali Release bundle'
$shortcut.Save()

$verifiedShortcut = $shell.CreateShortcut($shortcutPath)
if (-not $verifiedShortcut.TargetPath.Equals($publishExecutable, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Ali desktop shortcut target verification failed: $($verifiedShortcut.TargetPath)"
}

if (-not $KeepIntermediates -and -not $VerifyOnly) {
    $intermediateTargets = [System.Collections.Generic.List[string]]::new()
    @(
        (Join-Path $repoRoot 'src\bin'),
        (Join-Path $repoRoot 'src\obj'),
        (Join-Path $repoRoot 'tests\Ali.Framework.Tests\bin'),
        (Join-Path $repoRoot 'tests\Ali.Framework.Tests\obj'),
        (Join-Path $repoRoot 'tools\Ali.Modules\Automation\bin'),
        (Join-Path $repoRoot 'tools\Ali.Modules\Automation\obj')
    ) | ForEach-Object { $intermediateTargets.Add($_) }

    Get-ChildItem -LiteralPath (Join-Path $repoRoot 'external\modules') -Directory | ForEach-Object {
        $intermediateTargets.Add((Join-Path $_.FullName 'bin'))
        $intermediateTargets.Add((Join-Path $_.FullName 'obj'))
    }

    foreach ($target in $intermediateTargets | Sort-Object -Unique) {
        if (-not (Test-Path -LiteralPath $target)) {
            continue
        }

        $safeTarget = Assert-ChildPath -Path $target -Parent $repoRoot
        Remove-Item -LiteralPath $safeTarget -Recurse -Force
    }
}

Write-Host "Ali Release is ready to zip: $publishRoot"
Write-Host "Desktop shortcut: $shortcutPath"
