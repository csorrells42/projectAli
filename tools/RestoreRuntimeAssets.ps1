[CmdletBinding()]
param(
    [switch]$VerifyOnly,
    [switch]$Fast,
    [string]$OfflineCache
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath = Join-Path $repoRoot 'runtime-assets.json'
$restoreCommand = 'powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\RestoreRuntimeAssets.ps1'

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Runtime asset manifest is missing: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$stageRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ($manifest.stageRoot -replace '/', '\')))
$stageParent = Split-Path -Parent $stageRoot
$cacheRoot = if ([string]::IsNullOrWhiteSpace($OfflineCache)) {
    Join-Path $repoRoot 'artifacts\runtime-assets-cache'
} else {
    [IO.Path]::GetFullPath($OfflineCache)
}
$offline = -not [string]::IsNullOrWhiteSpace($OfflineCache)

function Get-RepoPath([string]$relativePath) {
    return [IO.Path]::GetFullPath((Join-Path $repoRoot ($relativePath -replace '/', '\')))
}

function Get-StagePath([string]$root, [string]$relativePath) {
    return [IO.Path]::GetFullPath((Join-Path $root ($relativePath -replace '/', '\')))
}

function Assert-PathInside([string]$candidate, [string]$allowedRoot) {
    $normalizedCandidate = [IO.Path]::GetFullPath($candidate)
    $normalizedRoot = [IO.Path]::GetFullPath($allowedRoot).TrimEnd('\') + '\'
    if (-not $normalizedCandidate.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside '$normalizedRoot': $normalizedCandidate"
    }
}

function Get-Sha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-File([string]$path, [long]$size, [string]$sha256, [bool]$skipHash) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required runtime asset is missing: $path`nRun: $restoreCommand"
    }

    $actualSize = (Get-Item -LiteralPath $path).Length
    if ($actualSize -ne $size) {
        throw "Runtime asset has the wrong size: $path (expected $size, found $actualSize)`nRun: $restoreCommand"
    }

    if (-not $skipHash) {
        $actualHash = Get-Sha256 $path
        if (-not $actualHash.Equals($sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Runtime asset checksum failed: $path`nExpected: $sha256`nActual:   $actualHash`nRun: $restoreCommand"
        }
    }
}

function Assert-RuntimeAssets([string]$root, [bool]$fastValidation) {
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Ali runtime assets have not been restored to '$root'.`nRun: $restoreCommand"
    }

    foreach ($entry in @($manifest.requiredFiles)) {
        $path = Get-StagePath $root $entry.path
        Assert-File $path ([long]$entry.size) ([string]$entry.sha256) $fastValidation
    }

    foreach ($marker in @($manifest.packageMarkers)) {
        $path = Get-StagePath $root ([string]$marker)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Pinned Python package marker is missing: $path`nRun: $restoreCommand"
        }
    }

    foreach ($entry in @($manifest.repositoryFiles)) {
        if ($entry.PSObject.Properties['optional'] -and $entry.optional -eq $true) { continue }
        $path = Get-StagePath $root ([string]$entry.destination)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Staged runtime helper is missing: $path`nRun: $restoreCommand"
        }
    }
}

function Normalize-StagePermissions([string]$root) {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { return }

    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $grant = "*$currentSid`:(OI)(CI)(F)"
    & icacls.exe $root /inheritance:e /grant:r $grant /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not normalize runtime staging permissions (icacls exit $LASTEXITCODE)."
    }
}

if ($VerifyOnly) {
    Assert-RuntimeAssets $stageRoot ([bool]$Fast)
    $mode = if ($Fast) { 'existence and size' } else { 'full checksum' }
    Write-Host "Ali runtime assets verified ($mode): $stageRoot"
    exit 0
}

New-Item -ItemType Directory -Force -Path $stageParent | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $cacheRoot 'downloads') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $cacheRoot 'wheels') | Out-Null

function Get-CachedAsset($asset) {
    $destination = Join-Path (Join-Path $cacheRoot 'downloads') ([string]$asset.fileName)
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        try {
            Assert-File $destination ([long]$asset.size) ([string]$asset.sha256) $false
            return $destination
        } catch {
            if ($offline) { throw }
            Remove-Item -LiteralPath $destination -Force
        }
    }

    if ($offline) {
        throw "Offline runtime cache is missing '$($asset.fileName)': $destination"
    }

    $partial = "$destination.partial"
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    Write-Host "Downloading $($asset.id) $($asset.version)..."
    try {
        Invoke-WebRequest -UseBasicParsing -Uri ([string]$asset.url) -OutFile $partial
        Assert-File $partial ([long]$asset.size) ([string]$asset.sha256) $false
        Move-Item -LiteralPath $partial -Destination $destination -Force
    } finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    }
    return $destination
}

function Copy-Mapping([string]$extractRoot, $mapping, [string]$workingStage) {
    $source = if ([string]$mapping.from -eq '.') {
        $extractRoot
    } else {
        Get-StagePath $extractRoot ([string]$mapping.from)
    }
    $destination = Get-StagePath $workingStage ([string]$mapping.to)

    if (Test-Path -LiteralPath $source -PathType Container) {
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $destination -Recurse -Force
    } elseif (Test-Path -LiteralPath $source -PathType Leaf) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
    } else {
        throw "Archive mapping source is missing for $($mapping.from): $source"
    }
}

function Expand-RuntimeAsset($asset, [string]$archive, [string]$workingStage, [string]$workingRoot) {
    switch ([string]$asset.kind) {
        'file' {
            $destination = Get-StagePath $workingStage ([string]$asset.destination)
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $archive -Destination $destination -Force
        }
        'pipWheel' { return }
        'zip' {
            $extractRoot = Join-Path $workingRoot ("extract-" + $asset.id)
            New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
            Expand-Archive -LiteralPath $archive -DestinationPath $extractRoot -Force
            foreach ($mapping in @($asset.mappings)) {
                Copy-Mapping $extractRoot $mapping $workingStage
            }
        }
        'tar.bz2' {
            $extractRoot = Join-Path $workingRoot ("extract-" + $asset.id)
            New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
            & tar.exe -xjf $archive -C $extractRoot
            if ($LASTEXITCODE -ne 0) {
                throw "tar failed while extracting $archive (exit $LASTEXITCODE)"
            }
            foreach ($mapping in @($asset.mappings)) {
                Copy-Mapping $extractRoot $mapping $workingStage
            }
        }
        default { throw "Unsupported runtime asset kind '$($asset.kind)' for $($asset.id)." }
    }
}

$workingRoot = Join-Path $stageParent ('.restore-' + [Guid]::NewGuid().ToString('N'))
$workingStage = Join-Path $workingRoot 'win-x64'
Assert-PathInside $workingRoot $stageParent
New-Item -ItemType Directory -Force -Path $workingStage | Out-Null

try {
    $resolvedAssets = @{}
    foreach ($asset in @($manifest.assets)) {
        $resolvedAssets[[string]$asset.id] = Get-CachedAsset $asset
    }

    foreach ($asset in @($manifest.assets)) {
        Expand-RuntimeAsset $asset $resolvedAssets[[string]$asset.id] $workingStage $workingRoot
    }

    $pythonRoot = Get-StagePath $workingStage 'runtime/python'
    $pythonExe = Join-Path $pythonRoot 'python.exe'
    $pthFile = Join-Path $pythonRoot 'python312._pth'
    @(
        'python312.zip',
        '.',
        'Lib\site-packages',
        '..\python-packages',
        '..\tts-packages',
        '..\whisper-packages',
        'import site'
    ) | Set-Content -LiteralPath $pthFile -Encoding Ascii

$sitePackages = Join-Path $pythonRoot 'Lib\site-packages'
    New-Item -ItemType Directory -Force -Path $sitePackages | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory(
        [string]$resolvedAssets['pip-bootstrap-wheel'],
        $sitePackages)

    $wheelCache = Join-Path $cacheRoot 'wheels'
    foreach ($group in @($manifest.packageGroups)) {
        $requirements = Get-RepoPath ([string]$group.requirements)
        $destination = Get-StagePath $workingStage ([string]$group.destination)
        New-Item -ItemType Directory -Force -Path $destination | Out-Null

        if (-not $offline) {
            Write-Host "Caching pinned $($group.id) wheels..."
            & $pythonExe -m pip download --disable-pip-version-check --only-binary=:all: --no-deps --requirement $requirements --dest $wheelCache
            if ($LASTEXITCODE -ne 0) { throw "pip download failed for $($group.id) (exit $LASTEXITCODE)" }
        }

        Write-Host "Installing pinned $($group.id) wheels..."
        & $pythonExe -m pip install --disable-pip-version-check --no-index --find-links $wheelCache --only-binary=:all: --no-deps --no-compile --requirement $requirements --target $destination
        if ($LASTEXITCODE -ne 0) { throw "pip install failed for $($group.id) (exit $LASTEXITCODE)" }
    }

    foreach ($entry in @($manifest.repositoryFiles)) {
        $source = Get-RepoPath ([string]$entry.source)
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            if ($entry.PSObject.Properties['optional'] -and $entry.optional -eq $true) { continue }
            throw "Repository runtime helper is missing: $source"
        }
        $destination = Get-StagePath $workingStage ([string]$entry.destination)
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }

    $whisperRef = Get-StagePath $workingStage 'lib/voice/whisper/models--Systran--faster-whisper-small.en/refs/main'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $whisperRef) | Out-Null
    Set-Content -LiteralPath $whisperRef -Value 'd1d751a5f8271d482d14ca55d9e2deeebbae577f' -NoNewline -Encoding Ascii

    $licenseInventory = @($manifest.assets | Select-Object id, version, license, licenseUrl, url, sha256)
    $licenseInventory | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $workingStage 'THIRD-PARTY-RUNTIME-ASSETS.json') -Encoding UTF8
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $workingStage 'runtime-assets.json') -Force

    Normalize-StagePermissions $workingStage
    Assert-RuntimeAssets $workingStage $false

    & $pythonExe -c "import mediapipe, faster_whisper, kittentts, piper; print('Portable Python imports verified')"
    if ($LASTEXITCODE -ne 0) { throw "Portable Python import smoke test failed (exit $LASTEXITCODE)" }

    $backup = "$stageRoot.previous"
    Assert-PathInside $backup $stageParent
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
    if (Test-Path -LiteralPath $stageRoot) { Move-Item -LiteralPath $stageRoot -Destination $backup }
    Move-Item -LiteralPath $workingStage -Destination $stageRoot
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }

    Write-Host "Ali runtime assets restored and fully verified: $stageRoot"
} catch {
    Write-Error $_
    throw
} finally {
    if (Test-Path -LiteralPath $workingRoot) {
        Assert-PathInside $workingRoot $stageParent
        Remove-Item -LiteralPath $workingRoot -Recurse -Force
    }
}
