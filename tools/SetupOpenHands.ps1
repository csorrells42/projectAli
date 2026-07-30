param(
    [string]$Distribution = 'Ubuntu',
    [string]$OpenHandsVersion = '1.16.0'
)

$ErrorActionPreference = 'Stop'
$wsl = Join-Path $env:WINDIR 'System32\wsl.exe'
if (-not (Test-Path -LiteralPath $wsl -PathType Leaf)) {
    throw 'wsl.exe is unavailable. Enable Windows Subsystem for Linux before installing OpenHands.'
}

$distributions = @(& $wsl --list --quiet 2>$null) | ForEach-Object { $_.Trim([char]0).Trim() } | Where-Object { $_ }
if ($distributions -notcontains $Distribution) {
    Write-Host "WSL distribution '$Distribution' is not installed."
    Write-Host "Starting the official Windows installation command. A restart may be required."
    & $wsl --install -d $Distribution
    exit $LASTEXITCODE
}

$install = @'
set -eu
python3.12 -m venv "$HOME/.local/share/ali-openhands"
"$HOME/.local/share/ali-openhands/bin/python" -m pip install --disable-pip-version-check --upgrade "openhands==__OPENHANDS_VERSION__"
mkdir -p "$HOME/.local/bin"
ln -sf "$HOME/.local/share/ali-openhands/bin/openhands" "$HOME/.local/bin/openhands"
"$HOME/.local/bin/openhands" --version
'@.Replace('__OPENHANDS_VERSION__', $OpenHandsVersion)

& $wsl -d $Distribution --exec bash -lc $install
if ($LASTEXITCODE -ne 0) {
    throw "OpenHands setup failed in WSL distribution '$Distribution' with exit code $LASTEXITCODE. Ubuntu may need the python3.12-venv package."
}

Write-Host "OpenHands $OpenHandsVersion is ready in WSL distribution '$Distribution'."
