param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$PackageSource = "https://api.nuget.org/v3/index.json",
    [switch]$BuildVoicePack,
    [ValidateSet("Piper", "Full")]
    [string]$VoicePackMode = "Piper"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$publishRoot = Join-Path $repoRoot "outputs\installer"
$appPublish = Join-Path $publishRoot "app-publish"
$setupPublish = Join-Path $publishRoot "setup-publish"
$payloadZip = Join-Path $repoRoot "src\Ali.App.Installer\Payload\ali-payload.zip"
$installerProject = Join-Path $repoRoot "src\Ali.App.Installer\Ali.App.Installer.csproj"
$appProject = Join-Path $repoRoot "src\Ali.App.Wpf\Ali.App.Wpf.csproj"
$vsixProject = Join-Path $repoRoot "src\Ali.App.VisualStudioExtension\Ali.App.VisualStudioExtension.csproj"
$vsixOutput = Join-Path $repoRoot "src\Ali.App.VisualStudioExtension\bin\$Configuration\net472\Ali.App.VisualStudioExtension.vsix"
$vsixPayloadDirectory = Join-Path $appPublish "extras\visualstudio"
$voiceRoot = Join-Path $repoRoot "lib\voice"
$voicePackStage = Join-Path $publishRoot "voice-pack-stage"
$voicePackOutputDirectory = Join-Path $publishRoot "voice-pack"
$voicePackOutput = Join-Path $voicePackOutputDirectory "Ali.VoicePack.zip"

function Invoke-DotNet {
    dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE"
    }
}

New-Item -ItemType Directory -Force -Path (Split-Path $payloadZip) | Out-Null
New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

if (Test-Path $appPublish) {
    Remove-Item -LiteralPath $appPublish -Recurse -Force
}

if (Test-Path $setupPublish) {
    Remove-Item -LiteralPath $setupPublish -Recurse -Force
}

Invoke-DotNet publish $appProject `
    -c $Configuration `
    -r $Runtime `
    --source $PackageSource `
    -p:SelfContained=true `
    -p:PublishTrimmed=false `
    -o $appPublish

Invoke-DotNet build $vsixProject `
    -c $Configuration `
    --source $PackageSource

if (!(Test-Path $vsixOutput)) {
    throw "VSIX package was not found after build: $vsixOutput"
}

New-Item -ItemType Directory -Force -Path $vsixPayloadDirectory | Out-Null
Copy-Item -LiteralPath $vsixOutput -Destination (Join-Path $vsixPayloadDirectory "Ali.App.VisualStudioExtension.vsix") -Force

if (Test-Path $payloadZip) {
    Remove-Item -LiteralPath $payloadZip -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $appPublish,
    $payloadZip,
    [System.IO.Compression.CompressionLevel]::Fastest,
    $false)

Invoke-DotNet publish $installerProject `
    -c $Configuration `
    -r $Runtime `
    --source $PackageSource `
    -p:SelfContained=true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:EnableCompressionInSingleFile=true `
    -o $setupPublish

if ($BuildVoicePack) {
    if (!(Test-Path $voiceRoot)) {
        throw "Voice resource root was not found: $voiceRoot"
    }

    if (Test-Path $voicePackStage) {
        Remove-Item -LiteralPath $voicePackStage -Recurse -Force
    }

    if (Test-Path $voicePackOutput) {
        Remove-Item -LiteralPath $voicePackOutput -Force
    }

    $stageVoiceRoot = Join-Path $voicePackStage "lib\voice"
    New-Item -ItemType Directory -Force -Path $stageVoiceRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $voicePackOutputDirectory | Out-Null

    if ($VoicePackMode -eq "Full") {
        Copy-Item -LiteralPath $voiceRoot -Destination (Join-Path $voicePackStage "lib") -Recurse -Force
    }
    else {
        foreach ($relativePath in @("piper", "python-venv", "README.md")) {
            $source = Join-Path $voiceRoot $relativePath
            if (Test-Path $source) {
                Copy-Item -LiteralPath $source -Destination (Join-Path $stageVoiceRoot $relativePath) -Recurse -Force
            }
        }
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $voicePackStage,
        $voicePackOutput,
        [System.IO.Compression.CompressionLevel]::Fastest,
        $false)
}

Write-Host "Ali setup executable:"
Get-ChildItem -Path $setupPublish -Filter "Ali.Setup.exe" | Select-Object -ExpandProperty FullName

if ($BuildVoicePack) {
    Write-Host "Ali voice pack sidecar:"
    Get-Item -LiteralPath $voicePackOutput | Select-Object -ExpandProperty FullName
}
