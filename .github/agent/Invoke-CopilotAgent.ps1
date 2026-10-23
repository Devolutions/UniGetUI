# Runs one GitHub Copilot CLI agent session for the agent workflows (spec, implement, review)
# and records what it did.
#
# Copilot runs with JSON output: every event is saved to events.jsonl in -OutputDir, and a
# short line per tool call and message is printed for the job log. When it ends, metrics.json
# holds the model that answered, Copilot usage, duration, and counts of what the agent ran
# (builds, tests, formatter runs, publishes, screenshots, clicks, MCP calls).
#
# -Models lists the models to try in order; one Copilot does not offer is skipped, as is one in
# -AvoidModels (so a reviewer can use a different model from the implementer). "auto" lets
# Copilot pick.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Prompt,

    [Parameter(Mandatory)]
    [string] $OutputDir,

    [string[]] $Models = @('auto'),

    [string[]] $AvoidModels = @(),

    # MCP server configuration files to add (Copilot CLI's --additional-mcp-config).
    [string[]] $McpConfig = @(),

    # Environment variables Copilot hides from the agent's shell.
    [string[]] $SecretEnvVars = @('COPILOT_GITHUB_TOKEN', 'GITHUB_TOKEN', 'GH_TOKEN', 'GITHUB_ENV', 'GITHUB_OUTPUT', 'GITHUB_PATH')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null
$eventsPath = Join-Path $OutputDir 'events.jsonl'
$metricsPath = Join-Path $OutputDir 'metrics.json'

function Format-Short {
    param([string] $Text, [int] $Length = 160)

    if ([string]::IsNullOrWhiteSpace($Text)) { return '' }
    $line = ($Text -replace '\s+', ' ').Trim()
    if ($line.Length -gt $Length) { $line = $line.Substring(0, $Length - 1) + '…' }
    return $line
}

# Prints one log line for the events a reader of the job log cares about.
function Write-EventLine {
    param($Event)

    $data = if ($Event.PSObject.Properties['data']) { $Event.data } else { $null }
    switch ($Event.type) {
        'session.auto_mode_resolved' { Write-Host "Copilot picked model $($data.chosenModel)" }
        'tool.execution_start' {
            $arguments = $data.arguments
            $what = @(
                if ($arguments -and $arguments.PSObject.Properties['description']) { $arguments.description }
                if ($arguments -and $arguments.PSObject.Properties['command']) { $arguments.command }
                elseif ($arguments -and $arguments.PSObject.Properties['path']) { $arguments.path }
            ) | Where-Object { $_ } | Select-Object -First 2
            Write-Host "● $($data.toolName): $(Format-Short ($what -join ' | '))"
        }
        'tool.execution_complete' {
            if ($data.PSObject.Properties['success'] -and -not $data.success) {
                $result = if ($data.PSObject.Properties['result'] -and $data.result -and $data.result.PSObject.Properties['content']) { $data.result.content } else { '' }
                Write-Host "  ✗ failed: $(Format-Short $result 240)"
            }
        }
        'assistant.message' {
            if (-not [string]::IsNullOrWhiteSpace($data.content)) {
                Write-Host "» $(Format-Short $data.content 400)"
            }
        }
    }
}

# Reads events.jsonl into the metrics the workflow reports.
function Get-AgentMetrics {
    param([string] $Path, [string] $RequestedModel, [double] $WallSeconds, [int] $ExitCode)

    $modelCalls = @{}
    $toolCalls = [System.Collections.Generic.List[object]]::new()
    $failedTools = 0
    $usage = $null
    $sessionMs = $null
    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $event = $line | ConvertFrom-Json -Depth 64 } catch { continue }
        $data = if ($event.PSObject.Properties['data']) { $event.data } else { $null }
        switch ($event.type) {
            'model.call_start' { $modelCalls[$data.model] = 1 + [int] $modelCalls[$data.model] }
            'tool.execution_start' { $toolCalls.Add($data) }
            'tool.execution_complete' { if ($data.PSObject.Properties['success'] -and -not $data.success) { $failedTools++ } }
            'session.usage_checkpoint' { $usage = $data }
            'result' {
                if ($event.PSObject.Properties['usage'] -and $event.usage.PSObject.Properties['sessionDurationMs']) {
                    $sessionMs = $event.usage.sessionDurationMs
                }
            }
        }
    }

    $commands = @($toolCalls | ForEach-Object {
            if ($_.arguments -and $_.arguments.PSObject.Properties['command']) { [string] $_.arguments.command }
        })
    $countCommands = { param($Pattern) @($commands | Where-Object { $_ -match $Pattern }).Count }
    $mcpCalls = @($toolCalls | Where-Object { $_.toolName -like 'avalonia-docs*' -or ($_.PSObject.Properties['mcpServerName'] -and $_.mcpServerName) })
    $model = ($modelCalls.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1).Key

    return [ordered]@{
        requestedModel = $RequestedModel
        model = $model
        models = @($modelCalls.Keys)
        exitCode = $ExitCode
        durationSeconds = [int] [Math]::Round($(if ($sessionMs) { $sessionMs / 1000 } else { $WallSeconds }), 0)
        premiumRequests = if ($usage -and $usage.PSObject.Properties['totalPremiumRequests']) { $usage.totalPremiumRequests } else { $null }
        aiCredits = if ($usage -and $usage.PSObject.Properties['totalNanoAiu']) { [Math]::Round($usage.totalNanoAiu / 1e9, 2) } else { $null }
        toolCalls = $toolCalls.Count
        failedToolCalls = $failedTools
        builds = & $countCommands 'Start-UniGetUIDev\.ps1|dotnet build'
        testRuns = & $countCommands 'dotnet test'
        formatterRuns = & $countCommands 'dotnet format'
        publishes = & $countCommands 'dotnet publish'
        waits = & $countCommands 'Wait-UniGetUIIdle'
        sleeps = & $countCommands 'Start-Sleep'
        clicks = [int] (@($commands | ForEach-Object { [regex]::Matches($_, 'Invoke-DesktopClick').Count }) | Measure-Object -Sum).Sum
        screenshots = [int] (@($commands | ForEach-Object { [regex]::Matches($_, 'Save-DesktopScreenshot|Wait-UniGetUIIdle\.ps1[^\r\n|;]*-Screenshot').Count }) | Measure-Object -Sum).Sum
        mcpCalls = $mcpCalls.Count
        mcpTools = @($mcpCalls | ForEach-Object { $_.toolName } | Sort-Object -Unique)
    }
}

$exitCode = 1
$usedModel = $null
$started = Get-Date
foreach ($model in $Models) {
    if ($model -ne 'auto' -and $AvoidModels -contains $model) {
        Write-Host "Skipping model ${model}: the run asks for a different one"
        continue
    }

    $arguments = @(
        '--prompt', $Prompt,
        '--model', $model,
        '--output-format', 'json',
        '--allow-all-tools',
        '--allow-all-paths',
        '--disable-builtin-mcps',
        '--no-color',
        '--share', (Join-Path $OutputDir 'transcript.md'),
        '--log-dir', (Join-Path $OutputDir 'logs'),
        '--secret-env-vars'
    ) + $SecretEnvVars
    foreach ($config in $McpConfig) {
        $arguments += @('--additional-mcp-config', "@$config")
    }

    Write-Host "Starting Copilot CLI with model $model"
    $stderrPath = Join-Path $OutputDir "copilot-$model.stderr.txt"
    $writer = [System.IO.StreamWriter]::new($eventsPath, $false, [System.Text.UTF8Encoding]::new($false))
    try {
        & copilot @arguments 2> $stderrPath | ForEach-Object {
            $writer.WriteLine($_)
            $writer.Flush()
            try { Write-EventLine ($_ | ConvertFrom-Json -Depth 64) } catch { Write-Host $_ }
        }
        $exitCode = $LASTEXITCODE
    }
    finally {
        $writer.Dispose()
    }

    $stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Raw } else { '' }
    if ($exitCode -ne 0 -and $stderr -match 'is not available') {
        Write-Host "Model $model is not available to this token; trying the next one"
        continue
    }
    if (-not [string]::IsNullOrWhiteSpace($stderr)) {
        Write-Host $stderr.Trim()
    }
    $usedModel = $model
    break
}

if ($null -eq $usedModel) {
    throw "None of the models could run: $($Models -join ', ') (avoiding: $($AvoidModels -join ', '))"
}

$metrics = Get-AgentMetrics -Path $eventsPath -RequestedModel $usedModel -WallSeconds ((Get-Date) - $started).TotalSeconds -ExitCode $exitCode
$metrics | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $metricsPath -Encoding utf8NoBOM
Write-Host "Model $($metrics.model), $($metrics.durationSeconds)s, $($metrics.aiCredits) AI credits, $($metrics.toolCalls) tool calls ($($metrics.mcpCalls) MCP)"

if ($exitCode -ne 0) {
    throw "Copilot CLI exited with code $exitCode"
}
