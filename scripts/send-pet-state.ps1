param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('idle', 'running', 'running-right', 'running-left', 'waving', 'jumping', 'failed', 'waiting', 'review')]
    [string]$State,
    [string]$Source = 'External',
    [string]$Message = '',
    [string]$ThreadId = '',
    [string]$TaskName = '',
    [string]$StartedAt = '',
    [ValidateRange(0, 999)]
    [int]$ActiveTaskCount = 0,
    [switch]$Silent
)

$ErrorActionPreference = 'Stop'
$directory = Join-Path $env:USERPROFILE '.agent-activity'
$path = Join-Path $directory 'claude-pet-events.jsonl'
New-Item -ItemType Directory -Force -Path $directory | Out-Null

$event = [ordered]@{
    timestamp = [DateTimeOffset]::UtcNow.ToString('o')
    state = $State
    source = $Source
    message = $Message
    threadId = $ThreadId
    taskName = $TaskName
    startedAt = $StartedAt
    activeTaskCount = $ActiveTaskCount
    showInSpeechBubble = -not $Silent.IsPresent
}

$line = $event | ConvertTo-Json -Compress
Add-Content -LiteralPath $path -Value $line -Encoding UTF8
