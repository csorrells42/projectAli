[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('Status', 'Send', 'Health', 'ApproveOnce', 'ApproveArguments', 'ApproveTool', 'Deny')]
    [string]$Action = 'Status',

    [Parameter(Position = 1)]
    [string]$Text
)

$ErrorActionPreference = 'Stop'
$aliRoot = if ([string]::IsNullOrWhiteSpace($env:ALI_LOCAL_ROOT)) {
    Join-Path $env:LOCALAPPDATA 'AliFiles'
} else {
    [System.IO.Path]::GetFullPath($env:ALI_LOCAL_ROOT.Trim())
}
$settingsPath = Join-Path $aliRoot 'Settings\ConversationBridge\conversation-bridge.json'
if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    throw "Ali conversation bridge settings do not exist yet: $settingsPath. Start the current Ali build once, then enable the bridge under Settings > Agents."
}

$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
if (-not $settings.enabled) {
    throw 'Ali live conversation bridge is off. Enable it under Settings > Agents, then select Save and apply.'
}

$endpoint = "http://127.0.0.1:$($settings.port)"
if ($Action -eq 'Health') {
    Invoke-RestMethod -Method Get -Uri "$endpoint/health" |
        ConvertTo-Json -Depth 20
    exit 0
}

$headers = @{ Authorization = "Bearer $($settings.authenticationToken)" }
if ($Action -eq 'Status') {
    Invoke-RestMethod -Method Get -Uri "$endpoint/v1/session" -Headers $headers |
        ConvertTo-Json -Depth 20
    exit 0
}

if ($Action -in @('ApproveOnce', 'ApproveArguments', 'ApproveTool', 'Deny')) {
    $session = Invoke-RestMethod -Method Get -Uri "$endpoint/v1/session" -Headers $headers
    $requestId = if ([string]::IsNullOrWhiteSpace($Text)) {
        $session.waitingForUserApproval.requestId
    } else {
        $Text.Trim()
    }
    if ([string]::IsNullOrWhiteSpace($requestId)) {
        throw 'Ali is not currently waiting for a permission decision.'
    }

    $decision = switch ($Action) {
        'ApproveOnce' { 'allow-once' }
        'ApproveArguments' { 'allow-arguments' }
        'ApproveTool' { 'allow-tool' }
        'Deny' { 'deny' }
    }
    $approvalBody = @{
        requestId = $requestId
        decision = $decision
    } | ConvertTo-Json -Compress
    Invoke-RestMethod -Method Post -Uri "$endpoint/v1/approvals" -Headers $headers -ContentType 'application/json' -Body $approvalBody |
        ConvertTo-Json -Depth 20
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Text)) {
    throw 'Send requires non-empty text. Example: .\tools\TalkToAli.ps1 Send "Hello Ali"'
}

$body = @{ text = $Text.Trim() } | ConvertTo-Json -Compress
Invoke-RestMethod -Method Post -Uri "$endpoint/v1/turns" -Headers $headers -ContentType 'application/json' -Body $body |
    ConvertTo-Json -Depth 20
