[CmdletBinding()]
param(
    [string]$ToolchainRoot,
    [string]$OfflineCache,
    [switch]$VerifyOnly,
    [switch]$SkipArduinoCores
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$manifestPath = Join-Path $repoRoot 'coding-toolchains.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Coding toolchain manifest is missing: $manifestPath"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$ToolchainRoot = if ([string]::IsNullOrWhiteSpace($ToolchainRoot)) {
    Join-Path $env:LOCALAPPDATA 'Ali\Toolchains'
} else {
    [IO.Path]::GetFullPath($ToolchainRoot)
}
$cacheRoot = if ([string]::IsNullOrWhiteSpace($OfflineCache)) {
    Join-Path $repoRoot 'artifacts\coding-toolchains-cache'
} else {
    [IO.Path]::GetFullPath($OfflineCache)
}
$offline = -not [string]::IsNullOrWhiteSpace($OfflineCache)

function Assert-ChildPath([string]$candidate, [string]$parent) {
    $full = [IO.Path]::GetFullPath($candidate).TrimEnd('\')
    $root = [IO.Path]::GetFullPath($parent).TrimEnd('\') + '\'
    if (-not ($full + '\').StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing toolchain operation outside '$root': $full"
    }
    return $full
}

function Get-Sha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-Archive($asset, [string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Coding toolchain archive is missing: $path"
    }
    $length = (Get-Item -LiteralPath $path).Length
    if ($length -ne [long]$asset.size) {
        throw "Coding toolchain archive size failed for $($asset.id): expected $($asset.size), found $length"
    }
    $hash = Get-Sha256 $path
    if (-not $hash.Equals([string]$asset.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Coding toolchain archive checksum failed for $($asset.id): $hash"
    }
}

function Get-Archive($asset) {
    New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
    $path = Join-Path $cacheRoot ([string]$asset.fileName)
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        Assert-Archive $asset $path
        return $path
    }
    if ($offline) {
        throw "Offline coding toolchain cache is missing $($asset.fileName): $path"
    }
    $partial = "$path.partial"
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    try {
        Write-Host "Downloading $($asset.id) $($asset.version)..."
        Invoke-WebRequest -UseBasicParsing -Uri ([string]$asset.url) -OutFile $partial
        Assert-Archive $asset $partial
        Move-Item -LiteralPath $partial -Destination $path -Force
    } finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    }
    return $path
}

function Test-AssetMarker($asset, [string]$destination) {
    $marker = Join-Path $destination '.ali-toolchain-asset.json'
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { return $false }
    try {
        $data = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        return ([string]$data.id).Equals([string]$asset.id, [StringComparison]::OrdinalIgnoreCase) `
            -and ([string]$data.sha256).Equals([string]$asset.sha256, [StringComparison]::OrdinalIgnoreCase)
    } catch { return $false }
}

function Install-Asset($asset, [string]$archive) {
    $destination = Assert-ChildPath (Join-Path $ToolchainRoot ([string]$asset.destination)) $ToolchainRoot
    if (Test-AssetMarker $asset $destination) {
        Write-Host "$($asset.id) $($asset.version) already staged."
        return
    }

    $workingParent = Join-Path $ToolchainRoot ('.restore-' + [Guid]::NewGuid().ToString('N'))
    $workingParent = Assert-ChildPath $workingParent $ToolchainRoot
    New-Item -ItemType Directory -Force -Path $workingParent | Out-Null
    try {
        $expanded = Join-Path $workingParent 'expanded'
        New-Item -ItemType Directory -Force -Path $expanded | Out-Null
        switch ([string]$asset.kind) {
            'zip' {
                Expand-Archive -LiteralPath $archive -DestinationPath $expanded -Force
                $source = $expanded
            }
            'sfx' {
                & $archive -y "-o$expanded"
                if ($LASTEXITCODE -ne 0) { throw "MSYS2 self-extractor failed with exit $LASTEXITCODE." }
                $source = Join-Path $expanded ([string]$asset.destination)
                if (-not (Test-Path -LiteralPath $source -PathType Container)) {
                    throw "MSYS2 archive did not contain the expected '$($asset.destination)' folder."
                }
            }
            default { throw "Unsupported coding toolchain asset kind: $($asset.kind)" }
        }

        if (Test-Path -LiteralPath $destination) {
            Remove-Item -LiteralPath $destination -Recurse -Force
        }
        Move-Item -LiteralPath $source -Destination $destination
        [pscustomobject]@{
            id = [string]$asset.id
            version = [string]$asset.version
            sha256 = [string]$asset.sha256
            source = [string]$asset.url
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination '.ali-toolchain-asset.json') -Encoding UTF8
    } finally {
        if (Test-Path -LiteralPath $workingParent) {
            Remove-Item -LiteralPath $workingParent -Recurse -Force
        }
    }
}

function Assert-InstalledToolchains {
    $required = @(
        (Join-Path $ToolchainRoot 'arduino-cli\arduino-cli.exe'),
        (Join-Path $ToolchainRoot 'arduino-ide\Arduino IDE.exe'),
        (Join-Path $ToolchainRoot 'msys64\usr\bin\bash.exe'),
        (Join-Path $ToolchainRoot 'msys64\ucrt64\bin\gcc.exe'),
        (Join-Path $ToolchainRoot 'msys64\ucrt64\bin\g++.exe'),
        (Join-Path $ToolchainRoot 'msys64\ucrt64\bin\gdb.exe')
    )
    foreach ($path in $required) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required coding toolchain executable is missing: $path"
        }
    }
}

New-Item -ItemType Directory -Force -Path $ToolchainRoot | Out-Null
foreach ($asset in @($manifest.assets)) {
    $archive = Get-Archive $asset
    if (-not $VerifyOnly) { Install-Asset $asset $archive }
}

if (-not $VerifyOnly) {
    $localeRoot = Join-Path $ToolchainRoot 'arduino-ide\locales'
    if (Test-Path -LiteralPath $localeRoot -PathType Container) {
        Get-ChildItem -LiteralPath $localeRoot -File -Filter '*.pak' |
            Where-Object { $_.Name -notin @('en-US.pak', 'en-GB.pak') } |
            Remove-Item -Force
    }

    $bash = Join-Path $ToolchainRoot 'msys64\usr\bin\bash.exe'
    & $bash -lc 'pacman -Syu --noconfirm'
    if ($LASTEXITCODE -ne 0) { throw "MSYS2 base update failed with exit $LASTEXITCODE." }
    & $bash -lc 'pacman -Syu --noconfirm'
    if ($LASTEXITCODE -ne 0) { throw "MSYS2 final update failed with exit $LASTEXITCODE." }
    $packageCommand = 'pacman -S --needed --noconfirm ' + (@($manifest.msys2Packages) -join ' ')
    & $bash -lc $packageCommand
    if ($LASTEXITCODE -ne 0) { throw "MSYS2 GCC package installation failed with exit $LASTEXITCODE." }

    $arduinoConfig = Join-Path $env:LOCALAPPDATA 'Arduino15\arduino-cli.yaml'
    $arduinoData = Join-Path $env:LOCALAPPDATA 'Arduino15'
    $sketchbook = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)) 'Arduino'
    New-Item -ItemType Directory -Force -Path $arduinoData, $sketchbook | Out-Null
    $yaml = @(
        'board_manager:',
        '  additional_urls: []',
        'directories:',
        "  data: '$($arduinoData.Replace("'", "''"))'",
        "  downloads: '$((Join-Path $arduinoData 'staging').Replace("'", "''"))'",
        "  user: '$($sketchbook.Replace("'", "''"))'",
        'library:',
        '  enable_unsafe_install: false',
        'locale: en_US'
    )
    $yaml | Set-Content -LiteralPath $arduinoConfig -Encoding UTF8

    $arduino = Join-Path $ToolchainRoot 'arduino-cli\arduino-cli.exe'
    & $arduino --config-file $arduinoConfig core update-index
    if ($LASTEXITCODE -ne 0) { throw "Arduino board index update failed with exit $LASTEXITCODE." }
    if (-not $SkipArduinoCores) {
        foreach ($core in @($manifest.arduinoCores)) {
            & $arduino --config-file $arduinoConfig core install ([string]$core)
            if ($LASTEXITCODE -ne 0) { throw "Arduino core install failed for $core with exit $LASTEXITCODE." }
        }
    }
}

Assert-InstalledToolchains
$arduinoExe = Join-Path $ToolchainRoot 'arduino-cli\arduino-cli.exe'
$gccExe = Join-Path $ToolchainRoot 'msys64\ucrt64\bin\gcc.exe'
$gxxExe = Join-Path $ToolchainRoot 'msys64\ucrt64\bin\g++.exe'
$lock = [ordered]@{
    generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    root = $ToolchainRoot
    arduinoCli = ((& $arduinoExe version) -join ' ').Trim()
    gcc = ((& $gccExe --version | Select-Object -First 1) -join ' ').Trim()
    gxx = ((& $gxxExe --version | Select-Object -First 1) -join ' ').Trim()
    assets = @($manifest.assets | Select-Object id, version, sha256, url)
    msys2Packages = @($manifest.msys2Packages)
    arduinoCores = @($manifest.arduinoCores)
}
$lock | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ToolchainRoot 'coding-toolchains.lock.json') -Encoding UTF8
Write-Host "Ali coding toolchains verified: $ToolchainRoot"
Write-Host $lock.arduinoCli
Write-Host $lock.gcc
