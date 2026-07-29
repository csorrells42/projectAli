[CmdletBinding()]
param(
    [ValidateSet('Status', 'InstallNotepadPlusPlus')]
    [string]$Action = 'Status',
    [string]$ApplicationRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = [IO.Path]::GetFullPath($ApplicationRoot)
$manifestPath = Join-Path $root 'editor-integrations.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Editor integration manifest is missing: $manifestPath"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

function Find-NotepadPlusPlus {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'Notepad++\notepad++.exe'),
        $(if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'Notepad++\notepad++.exe' }),
        (Join-Path $env:LOCALAPPDATA 'Programs\Notepad++\notepad++.exe')
    )
    return $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
}

function Get-BackupRoot {
    if (Test-Path -LiteralPath 'D:\' -PathType Container) {
        return 'D:\AliBackups\NotepadPlusPlus'
    }
    return Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'AliBackups\NotepadPlusPlus'
}

function Backup-NotepadPlusPlusConfig {
    $source = Join-Path $env:APPDATA 'Notepad++'
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { return $null }
    $destination = Join-Path (Get-BackupRoot) (Get-Date -Format 'yyyyMMdd-HHmmss')
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Copy-Item -LiteralPath $source -Destination (Join-Path $destination 'AppData') -Recurse -Force
    return $destination
}

function Get-OfficialCatalog {
    try {
        $response = Invoke-RestMethod -Uri ([string]$manifest.notepadPlusPlus.catalogUrl) -TimeoutSec 30
        return @($response.'npp-plugins')
    }
    catch {
        Write-Warning "The official Notepad++ catalog is unavailable. Ali will use the pinned checksum-verified fallback packages. $($_.Exception.Message)"
        return @()
    }
}

function Resolve-PluginPackage([object]$plugin, [object[]]$catalog) {
    $official = $catalog | Where-Object { $_.'folder-name' -eq [string]$plugin.folder } | Select-Object -First 1
    if ($official) {
        return [pscustomobject]@{
            Folder = [string]$plugin.folder
            DisplayName = [string]$plugin.displayName
            Version = [string]$official.version
            Url = [string]$official.repository
            Sha256 = ([string]$official.id).ToLowerInvariant()
            Source = 'official live catalog'
        }
    }
    return [pscustomobject]@{
        Folder = [string]$plugin.folder
        DisplayName = [string]$plugin.displayName
        Version = [string]$plugin.fallbackVersion
        Url = [string]$plugin.fallbackUrl
        Sha256 = ([string]$plugin.fallbackSha256).ToLowerInvariant()
        Source = 'pinned fallback'
    }
}

function Show-Status {
    $notepad = Find-NotepadPlusPlus
    if (-not $notepad) {
        Write-Host 'Notepad++: not installed'
        return
    }
    $pluginRoot = Join-Path (Split-Path -Parent $notepad) 'plugins'
    $installed = @($manifest.notepadPlusPlus.plugins | Where-Object {
        Test-Path -LiteralPath (Join-Path $pluginRoot "$($_.folder)\$($_.folder).dll") -PathType Leaf
    }).Count
    $version = (Get-Item -LiteralPath $notepad).VersionInfo.ProductVersion
    Write-Host "Notepad++: $version"
    Write-Host "Toolkit: $installed/$(@($manifest.notepadPlusPlus.plugins).Count) plugins installed"
    Write-Host "Running: $([bool](Get-Process -Name 'notepad++' -ErrorAction SilentlyContinue))"
}

if ($Action -eq 'Status') {
    Show-Status
    exit 0
}

$notepadPath = Find-NotepadPlusPlus
if (-not $notepadPath) { throw 'Notepad++ is not installed in a supported per-machine or per-user location.' }
if (Get-Process -Name 'notepad++' -ErrorAction SilentlyContinue) {
    throw 'Notepad++ is running. Save your work and close it before installing or repairing the toolkit.'
}

$backup = Backup-NotepadPlusPlusConfig
$catalog = @(Get-OfficialCatalog)
$pluginRoot = Join-Path (Split-Path -Parent $notepadPath) 'plugins'
$staging = Join-Path ([IO.Path]::GetTempPath()) ("Ali-NotepadPlusPlus-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
$installed = [Collections.Generic.List[string]]::new()
try {
    foreach ($plugin in @($manifest.notepadPlusPlus.plugins)) {
        $package = Resolve-PluginPackage $plugin $catalog
        $zip = Join-Path $staging ($package.Folder + '.zip')
        $expanded = Join-Path $staging ($package.Folder + '-expanded')
        Invoke-WebRequest -UseBasicParsing -Uri $package.Url -OutFile $zip -TimeoutSec 120
        $actualHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne $package.Sha256) {
            throw "Checksum failed for $($package.DisplayName). Expected $($package.Sha256), received $actualHash."
        }
        Expand-Archive -LiteralPath $zip -DestinationPath $expanded -Force
        $dll = Get-ChildItem -LiteralPath $expanded -Filter ($package.Folder + '.dll') -File -Recurse | Select-Object -First 1
        if (-not $dll) { throw "The $($package.DisplayName) package does not contain $($package.Folder).dll." }
        $sourceRoot = $dll.Directory.FullName
        $destination = Join-Path $pluginRoot $package.Folder
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        Copy-Item -Path (Join-Path $sourceRoot '*') -Destination $destination -Recurse -Force
        $installed.Add("$($package.DisplayName) $($package.Version) [$($package.Source)]")
    }
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}

$receiptRoot = Join-Path $env:LOCALAPPDATA 'Ali\Logs'
New-Item -ItemType Directory -Force -Path $receiptRoot | Out-Null
$receipt = Join-Path $receiptRoot 'editor-integration-last-install.txt'
$backupDisplay = if ($backup) { $backup } else { 'no user configuration existed' }
@(
    "Completed: $([DateTimeOffset]::Now.ToString('O'))"
    "Notepad++: $notepadPath"
    "Backup: $backupDisplay"
    'Installed:'
    ($installed | ForEach-Object { "  $_" })
) | Set-Content -LiteralPath $receipt -Encoding UTF8

Write-Host "Notepad++ toolkit installed successfully. Configuration backup: $backupDisplay"
Write-Host "Receipt: $receipt"
Write-Host 'Start Notepad++ to load the upgraded toolkit.'
