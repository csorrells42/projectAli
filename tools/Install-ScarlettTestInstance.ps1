[CmdletBinding()]
param(
    [string]$InstanceRoot = (Join-Path $env:LOCALAPPDATA 'ScarlettFiles'),
    [string]$WorkspaceRoot = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'ScarlettWorkspace'),
    [int]$BridgePort = 8872,
    [int]$McpPort = 8871,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$aliExecutable = Join-Path $repositoryRoot 'bin\Release\Ali\Ali.exe'
$aliSettingsRoot = Join-Path $env:LOCALAPPDATA 'AliFiles\Settings'
$scarlettSettingsRoot = Join-Path $InstanceRoot 'Settings'
$scarlettDataRoot = Join-Path $InstanceRoot 'Data'
$launcherPath = Join-Path $InstanceRoot 'Launch-Scarlett.ps1'

function Assert-PortAvailable {
    param([Parameter(Mandatory)][int]$Port)

    if ($Port -lt 1024 -or $Port -gt 65535) {
        throw "Port $Port is outside the supported range 1024-65535."
    }

    $listener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Loopback,
        $Port)
    try {
        $listener.Start()
    }
    catch {
        throw "Port $Port is already in use. Scarlett was not launched."
    }
    finally {
        $listener.Stop()
    }
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Value
    )

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $temporaryPath = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $json = $Value | ConvertTo-Json -Depth 100
        [System.IO.File]::WriteAllText(
            $temporaryPath,
            $json,
            [System.Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Copy-SettingIfPresent {
    param([Parameter(Mandatory)][string]$RelativePath)

    $source = Join-Path $aliSettingsRoot $RelativePath
    $destination = Join-Path $scarlettSettingsRoot $RelativePath
    if (-not (Test-Path -LiteralPath $source) -or (Test-Path -LiteralPath $destination)) {
        return
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
}

if (-not (Test-Path -LiteralPath $aliExecutable -PathType Leaf)) {
    throw "The canonical Release executable is missing: $aliExecutable"
}
if (-not (Test-Path -LiteralPath $aliSettingsRoot -PathType Container)) {
    throw "Ali's settings directory is missing: $aliSettingsRoot"
}

$InstanceRoot = [System.IO.Path]::GetFullPath($InstanceRoot)
$WorkspaceRoot = [System.IO.Path]::GetFullPath($WorkspaceRoot)
New-Item -ItemType Directory -Path $scarlettSettingsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $scarlettDataRoot -Force | Out-Null
New-Item -ItemType Directory -Path $WorkspaceRoot -Force | Out-Null

$profilePath = Join-Path $scarlettSettingsRoot 'assistant-profile.json'
if (-not (Test-Path -LiteralPath $profilePath)) {
    Write-JsonFile -Path $profilePath -Value ([ordered]@{
        assistantName = 'Scarlett'
        profileId = "Scarlett-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
        createdAt = [DateTimeOffset]::UtcNow.ToString('O')
    })
}

$workspaceSettingsPath = Join-Path $scarlettSettingsRoot 'workspace-settings.json'
if (-not (Test-Path -LiteralPath $workspaceSettingsPath)) {
    Write-JsonFile -Path $workspaceSettingsPath -Value ([ordered]@{
        workspaceRoot = $WorkspaceRoot
    })
}

$bridgeSettingsPath = Join-Path $scarlettSettingsRoot 'ConversationBridge\conversation-bridge.json'
if (-not (Test-Path -LiteralPath $bridgeSettingsPath)) {
    $bridgeTokenBytes = New-Object byte[] 32
    $randomNumberGenerator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $randomNumberGenerator.GetBytes($bridgeTokenBytes)
        $bridgeToken = -join ($bridgeTokenBytes | ForEach-Object { $_.ToString('X2') })
    }
    finally {
        $randomNumberGenerator.Dispose()
        [Array]::Clear($bridgeTokenBytes, 0, $bridgeTokenBytes.Length)
    }
    Write-JsonFile -Path $bridgeSettingsPath -Value ([ordered]@{
        enabled = $true
        allowPermissionDecisions = $true
        port = $BridgePort
        authenticationToken = $bridgeToken
    })
}

foreach ($relativePath in @(
    'runtime-settings.json',
    'runtime-credentials.dpapi',
    'agent-orchestration-settings.json',
    'attention-settings.json',
    'voice-settings.json',
    'Coding\coding_tool_settings.json',
    'MCP\mcp-clients.json',
    'Permissions\agent-tool-permissions.json',
    '.agent-tool-permissions-initialized',
    'Sources\internet_backends.json',
    'Sources\curated_sources.json'
)) {
    Copy-SettingIfPresent -RelativePath $relativePath
}

$aliMcpServerPath = Join-Path $aliSettingsRoot 'MCP\mcp-server.json'
$scarlettMcpServerPath = Join-Path $scarlettSettingsRoot 'MCP\mcp-server.json'
if ((Test-Path -LiteralPath $aliMcpServerPath) -and -not (Test-Path -LiteralPath $scarlettMcpServerPath)) {
    $mcpSettings = Get-Content -LiteralPath $aliMcpServerPath -Raw | ConvertFrom-Json
    $mcpSettings.port = $McpPort
    Write-JsonFile -Path $scarlettMcpServerPath -Value $mcpSettings
}

$aliVectorSettingsPath = Join-Path $aliSettingsRoot 'Sources\local_vector_library_settings.json'
$scarlettVectorSettingsPath = Join-Path $scarlettSettingsRoot 'Sources\local_vector_library_settings.json'
if ((Test-Path -LiteralPath $aliVectorSettingsPath) -and -not (Test-Path -LiteralPath $scarlettVectorSettingsPath)) {
    $vectorSettings = Get-Content -LiteralPath $aliVectorSettingsPath -Raw | ConvertFrom-Json
    $vectorSettings.useManagedLocalQdrant = $false
    $vectorSettings.autoStartQdrant = $false
    $vectorSettings.qdrantCollectionName = 'scarlett_local_library'
    $vectorSettings.rootDirectory = Join-Path $scarlettDataRoot 'RAG\Library'
    Write-JsonFile -Path $scarlettVectorSettingsPath -Value $vectorSettings
}

$userMemorySettingsPath = Join-Path $scarlettSettingsRoot 'user-memory-settings.json'
if (-not (Test-Path -LiteralPath $userMemorySettingsPath)) {
    Write-JsonFile -Path $userMemorySettingsPath -Value ([ordered]@{
        enabled = $true
        collectionName = 'scarlett_participant_memories'
    })
}

$escapedExecutable = $aliExecutable.Replace("'", "''")
$escapedInstanceRoot = $InstanceRoot.Replace("'", "''")
$launcher = @"
`$ErrorActionPreference = 'Stop'
`$executable = '$escapedExecutable'
`$instanceRoot = '$escapedInstanceRoot'

if (-not (Test-Path -LiteralPath `$executable -PathType Leaf)) {
    throw "Ali executable is missing: `$executable"
}

`$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
`$startInfo.FileName = `$executable
`$startInfo.WorkingDirectory = Split-Path -Parent `$executable
`$startInfo.UseShellExecute = `$false
`$startInfo.EnvironmentVariables['ALI_LOCAL_ROOT'] = `$instanceRoot
[void][System.Diagnostics.Process]::Start(`$startInfo)
"@
[System.IO.File]::WriteAllText(
    $launcherPath,
    $launcher,
    [System.Text.UTF8Encoding]::new($false))

$desktopRoot = [Environment]::GetFolderPath('Desktop')
if (-not [string]::IsNullOrWhiteSpace($env:OneDrive)) {
    $oneDriveDesktop = Join-Path $env:OneDrive 'Desktop'
    if (Test-Path -LiteralPath $oneDriveDesktop -PathType Container) {
        $desktopRoot = $oneDriveDesktop
    }
}
$shortcutPath = Join-Path $desktopRoot 'Scarlett (Test).lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$shortcut.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$launcherPath`""
$shortcut.WorkingDirectory = Split-Path -Parent $aliExecutable
$shortcut.IconLocation = "$aliExecutable,0"
$shortcut.Description = 'Scarlett isolated Project Ali test instance'
$shortcut.Save()

if ($Launch) {
    Assert-PortAvailable -Port $BridgePort
    if ((Get-Content -LiteralPath $scarlettMcpServerPath -Raw | ConvertFrom-Json).enabled) {
        Assert-PortAvailable -Port $McpPort
    }
    & $launcherPath
}

[pscustomobject]@{
    Name = 'Scarlett'
    Executable = $aliExecutable
    InstanceRoot = $InstanceRoot
    WorkspaceRoot = $WorkspaceRoot
    BridgeEndpoint = "http://127.0.0.1:$BridgePort"
    McpEndpoint = "http://127.0.0.1:$McpPort/mcp"
    Shortcut = $shortcutPath
    Launched = [bool]$Launch
}
