[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath($PublishRoot)
$manifestPath = Join-Path $root 'runtime-assets.json'

foreach ($required in @('Ali.exe', 'Ali.dll', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'runtime-assets.json', 'THIRD-PARTY-RUNTIME-ASSETS.json')) {
    $path = Join-Path $root $required
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Published Ali bundle is missing: $path"
    }
}

$nonEnglishSatelliteDirectories = @(
    @('cs', 'de', 'es', 'fr', 'it', 'ja', 'ko', 'pl', 'pt-BR', 'ru', 'tr') |
        Where-Object { Test-Path -LiteralPath (Join-Path $root $_) -PathType Container }
)
if ($nonEnglishSatelliteDirectories.Count -gt 0) {
    throw "Published Ali bundle contains non-English satellite resources: $($nonEnglishSatelliteDirectories -join ', ')"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
foreach ($entry in @($manifest.requiredFiles)) {
    $path = Join-Path $root ([string]$entry.path -replace '/', '\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Published Ali bundle is missing required runtime asset: $path"
    }
    $actualSize = (Get-Item -LiteralPath $path).Length
    if ($actualSize -ne [long]$entry.size) {
        throw "Published runtime asset has the wrong size: $path"
    }
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $actualHash.Equals([string]$entry.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Published runtime asset checksum failed: $path"
    }
}

$python = Join-Path $root 'runtime\python\python.exe'
& $python -c "import mediapipe, faster_whisper, kittentts, piper, mem0, qdrant_client; print('Published Python imports verified')"
if ($LASTEXITCODE -ne 0) { throw "Published Python runtime smoke test failed (exit $LASTEXITCODE)." }

$ffmpeg = Join-Path $root 'dependencies\ffmpeg\win-x64\ffmpeg.exe'
$ffmpegOutput = @(& $ffmpeg -version 2>&1)
$ffmpegExitCode = $LASTEXITCODE
$ffmpegOutput | Select-Object -First 1
if ($ffmpegExitCode -ne 0) { throw "Published FFmpeg smoke test failed (exit $ffmpegExitCode)." }

Write-Host "Published Ali bundle passed file, checksum, Python import, and FFmpeg smoke tests: $root"
