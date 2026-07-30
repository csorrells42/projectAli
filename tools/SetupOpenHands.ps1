param(
    [string]$Distribution = 'Ubuntu-24.04',
    [string]$OpenHandsVersion = '1.16.0'
)

$ErrorActionPreference = 'Stop'
$wsl = Join-Path $env:WINDIR 'System32\wsl.exe'
if (-not (Test-Path -LiteralPath $wsl -PathType Leaf)) {
    throw 'wsl.exe is unavailable. Enable Windows Subsystem for Linux before installing OpenHands.'
}

$null = & $wsl --status 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Windows Subsystem for Linux is not enabled.'
    Write-Host 'Starting the official Windows feature installation. Administrator approval and a restart may be required.'
    & $wsl --install
    exit $LASTEXITCODE
}

$distributions = @(& $wsl --list --quiet 2>$null) | ForEach-Object { $_.Trim([char]0).Trim() } | Where-Object { $_ }
if ($distributions -notcontains $Distribution) {
    Write-Host "WSL distribution '$Distribution' is not installed."
    Write-Host "Starting the official Windows installation command. A restart may be required."
    & $wsl --install -d $Distribution
    exit $LASTEXITCODE
}

$wslConfigTemplate = Join-Path $PSScriptRoot 'wslconfig.openhands'
if (-not (Test-Path -LiteralPath $wslConfigTemplate -PathType Leaf)) {
    throw "The tracked WSL networking template is missing: $wslConfigTemplate"
}

$wslConfig = Join-Path $env:USERPROFILE '.wslconfig'
$restartWsl = $false
if (-not (Test-Path -LiteralPath $wslConfig -PathType Leaf)) {
    Copy-Item -LiteralPath $wslConfigTemplate -Destination $wslConfig
    $restartWsl = $true
    Write-Host "Installed Ali's WSL mirrored-networking configuration at '$wslConfig'."
}
else {
    $configuration = Get-Content -LiteralPath $wslConfig -Raw
    $wsl2Section = [regex]::Match(
        $configuration,
        '(?ims)^\s*\[wsl2\]\s*$.*?(?=^\s*\[|\z)')
    $hasMirroredNetworking = $wsl2Section.Success -and $wsl2Section.Value -match '(?im)^\s*networkingMode\s*=\s*mirrored\s*$'
    if (-not $hasMirroredNetworking) {
        throw "Existing WSL configuration '$wslConfig' was not changed. Add 'networkingMode=mirrored' under its [wsl2] section, run 'wsl --shutdown', then rerun this setup."
    }
}

if ($restartWsl) {
    & $wsl --shutdown
    if ($LASTEXITCODE -ne 0) {
        throw "Ali installed '$wslConfig', but WSL could not be restarted. Run 'wsl --shutdown', then rerun this setup."
    }
}

$venvPackageReady = & $wsl -d $Distribution --exec sh -lc "dpkg-query -W -f='`$`{Status`}' python3.12-venv 2>/dev/null | grep -q 'install ok installed'"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Installing the Python virtual-environment prerequisite in '$Distribution'."
    & $wsl -d $Distribution --user root --exec sh -lc 'apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y python3.12-venv'
    if ($LASTEXITCODE -ne 0) {
        throw "Could not install python3.12-venv in WSL distribution '$Distribution'."
    }
}

$install = @'
set -eu
python3.12 -m venv "$HOME/.local/share/ali-openhands-bootstrap"
"$HOME/.local/share/ali-openhands-bootstrap/bin/python" -m pip install --disable-pip-version-check --upgrade uv
mkdir -p "$HOME/.local/bin"
UV_TOOL_DIR="$HOME/.local/share/ali-openhands-tools" \
UV_TOOL_BIN_DIR="$HOME/.local/bin" \
    "$HOME/.local/share/ali-openhands-bootstrap/bin/uv" tool install --force "openhands==__OPENHANDS_VERSION__" --python 3.12
"$HOME/.local/bin/openhands" --version
'@.Replace('__OPENHANDS_VERSION__', $OpenHandsVersion)

& $wsl -d $Distribution --exec bash -lc $install
if ($LASTEXITCODE -ne 0) {
    throw "OpenHands setup failed in WSL distribution '$Distribution' with exit code $LASTEXITCODE."
}

Write-Host "OpenHands $OpenHandsVersion is ready in WSL distribution '$Distribution'."
