[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath($PublishRoot)
$manifestPath = Join-Path $root 'runtime-assets.json'

foreach ($required in @('Ali.exe', 'Ali.dll', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'runtime-assets.json', 'THIRD-PARTY-RUNTIME-ASSETS.json', 'coding-toolchains.json', 'tools\RestoreCodingToolchains.ps1', 'editor-integrations.json', 'tools\ConfigureEditorIntegrations.ps1', 'tools\SetupOpenHands.ps1', 'tools\wslconfig.openhands', 'Modules\Coding\Agents\Tools\ali_openhands_launcher.py', 'Modules\Coding\Agents\Tools\ali_aider_launcher.py', 'docs\EDITOR-INTEGRATIONS.md', 'docs\AgentFrameworkArchitecture.md', 'docs\EXTERNAL-CODING-AGENTS.md', 'skills\software-engineering\SKILL.md', 'skills\evidence-research\SKILL.md', 'skills\office-artifacts\SKILL.md', 'skills\engineering-shop-floor\SKILL.md')) {
    $path = Join-Path $root $required
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Published Ali bundle is missing: $path"
    }
}

$codingManifest = Get-Content -LiteralPath (Join-Path $root 'coding-toolchains.json') -Raw | ConvertFrom-Json
if (@($codingManifest.assets).Count -lt 3) {
    throw 'Published coding toolchain manifest is incomplete.'
}

foreach ($roslynFile in @(
    'Microsoft.Build.Locator.dll',
    'Microsoft.CodeAnalysis.CSharp.dll',
    'Microsoft.CodeAnalysis.CSharp.Features.dll',
    'Microsoft.CodeAnalysis.CSharp.Workspaces.dll',
    'Microsoft.CodeAnalysis.Workspaces.MSBuild.dll',
    'BuildHost-netcore\Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll'
)) {
    $path = Join-Path $root $roslynFile
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Published Ali bundle is missing Roslyn coding intelligence: $path"
    }
}

$redistributedMsBuild = @(
    Get-ChildItem -LiteralPath $root -Filter 'Microsoft.Build*.dll' -File -Recurse |
        Where-Object { $_.Name -ne 'Microsoft.Build.Locator.dll' }
)
if ($redistributedMsBuild.Count -gt 0) {
    throw "Published Ali bundle must resolve MSBuild from the installed SDK, not redistribute it: $($redistributedMsBuild.FullName -join ', ')"
}

$nonEnglishSatelliteDirectories = @(
    @('cs', 'de', 'es', 'fr', 'it', 'ja', 'ko', 'pl', 'pt-BR', 'ru', 'tr', 'zh-Hans', 'zh-Hant') |
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
$fastEmbedModel = Join-Path $root 'runtime\fastembed-cache\models--Qdrant--bm25\snapshots\e499a1f8d6bec960aab5533a0941bf914e70faf9'
& $python -c "import os,sys; os.environ['HF_HUB_OFFLINE']='1'; import mediapipe,faster_whisper,kittentts,piper,mem0,qdrant_client,spacy,en_core_web_sm; from fastembed import SparseTextEmbedding; nlp=spacy.load('en_core_web_sm'); assert nlp('machines running reliably')[1].lemma_ == 'run'; vectors=list(SparseTextEmbedding(model_name='Qdrant/bm25',specific_model_path=sys.argv[1],local_files_only=True).embed(['hydraulic pressure alarm 4177'])); assert vectors and len(vectors[0].indices)>0; print('Published Python and offline hybrid retrieval verified')" $fastEmbedModel
if ($LASTEXITCODE -ne 0) { throw "Published Python runtime smoke test failed (exit $LASTEXITCODE)." }

$ffmpeg = Join-Path $root 'dependencies\ffmpeg\win-x64\ffmpeg.exe'
$ffmpegOutput = @(& $ffmpeg -version 2>&1)
$ffmpegExitCode = $LASTEXITCODE
$ffmpegOutput | Select-Object -First 1
if ($ffmpegExitCode -ne 0) { throw "Published FFmpeg smoke test failed (exit $ffmpegExitCode)." }

Write-Host "Published Ali bundle passed file, checksum, Python import, and FFmpeg smoke tests: $root"
