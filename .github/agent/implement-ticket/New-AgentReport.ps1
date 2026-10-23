# Builds the report on an implement run from its artifacts, for the pull request, the job
# summary and comments: the automated checks (before and after the fix round), the screenshot
# evidence, the review with what the fix round did about each finding, and the run's metrics
# (models, Copilot usage, what the agents ran, and how long each phase took).
#
# Writes report.md (the report alone) and body.md (the agent's description followed by the
# report) to -OutputDir, adds both to the job summary, and sets the review-verdict and
# checks-passed outputs.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ChangeDir,

    # Defaults to the review folder of -ChangeDir, where the desktop job writes it.
    [string] $ReviewDir,

    [Parameter(Mandatory)]
    [string] $OutputDir,

    [string] $Repository = $env:GITHUB_REPOSITORY,

    [string] $RunId = $env:GITHUB_RUN_ID,

    [string] $RunAttempt = $env:GITHUB_RUN_ATTEMPT,

    # runner.name of the implement job, to find its step timings.
    [string] $ImplementRunner
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null

function Read-Json {
    param([string] $Path)
    if ($Path -and (Test-Path -LiteralPath $Path)) { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    return $null
}

function Read-Text {
    param([string] $Path)
    if ($Path -and (Test-Path -LiteralPath $Path)) { return (Get-Content -LiteralPath $Path -Raw).Trim() }
    return ''
}

function Format-Duration {
    param([double] $Seconds)
    if ($Seconds -lt 60) { return "$([int] $Seconds)s" }
    return '{0}m{1:00}s' -f [int][Math]::Floor($Seconds / 60), [int]($Seconds % 60)
}

if (-not $ReviewDir) { $ReviewDir = Join-Path $ChangeDir 'review' }
$fixDir = Join-Path $ChangeDir 'fix'
$checks = Read-Json (Join-Path $ChangeDir 'checks.json')
$checksBefore = Read-Json (Join-Path $ChangeDir 'before-fix/checks.json')
$fixRequest = Read-Json (Join-Path $fixDir 'fix-request.json')
$fixReport = Read-Json (Join-Path $fixDir 'fix-report.json')
$fixer = Read-Json (Join-Path $fixDir 'metrics.json')
$agent = Read-Json (Join-Path $ChangeDir 'metrics.json')
$change = Read-Json (Join-Path $ChangeDir 'change.json')
$review = if ($ReviewDir) { Read-Json (Join-Path $ReviewDir 'review.json') } else { $null }
$reviewer = if ($ReviewDir) { Read-Json (Join-Path $ReviewDir 'metrics.json') } else { $null }
$reviewText = if ($ReviewDir) { Read-Text (Join-Path $ReviewDir 'review.md') } else { '' }

# Phase timings of the implement job, from the run's job list.
$phases = @()
if ($ImplementRunner -and $RunId) {
    $jobs = & gh api "repos/$Repository/actions/runs/$RunId/attempts/$RunAttempt/jobs" --paginate --jq '.jobs[]' 2>$null |
        ForEach-Object { $_ | ConvertFrom-Json }
    $job = @($jobs | Where-Object { $_.runner_name -eq $ImplementRunner -and $_.name -like '*Implement on a live desktop*' }) | Select-Object -First 1
    if ($job) {
        $groups = [ordered]@{
            'Setup (checkout, .NET, desktop session)' = @('Checkout repository', 'Download the request', 'Prepare the ticket', 'Install .NET', 'Restore NuGet packages', 'Start desktop session')
            'Base build' = @('Build UniGetUI')
            'Agent' = @('Install Node.js', 'Install GitHub Copilot CLI', 'Run the agent')
            'Checks and evidence' = @('Check the change', 'Prepare the screenshots')
            'Review' = @('Review the change', 'Choose what to fix')
            'Fix round' = @('Fix the problems found')
            'Checks after the fixes' = @('Check the change after the fixes')
        }
        foreach ($group in $groups.GetEnumerator()) {
            $seconds = 0
            foreach ($step in @($job.steps | Where-Object { $group.Value -contains $_.name -and $_.started_at -and $_.completed_at })) {
                $seconds += ([datetime] $step.completed_at - [datetime] $step.started_at).TotalSeconds
            }
            if ($seconds -gt 0) { $phases += [pscustomobject]@{ Phase = $group.Key; Seconds = $seconds } }
        }
    }
}

$verdict = if ($review) { [string] $review.verdict } else { '' }
$checksPassed = if ($checks) { [bool] $checks.passed } else { $false }

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('## Automated checks')
$lines.Add('')
$checksMd = Read-Text (Join-Path $ChangeDir 'checks.md')
$lines.Add($(if ($checksMd) { $checksMd } else { 'The checks did not run.' }))
if ($checksBefore) {
    $failedBefore = @($checksBefore.checks | Where-Object status -eq 'failed' | ForEach-Object name)
    $lines.Add('')
    $lines.Add($(if ($failedBefore.Count) { "These are the checks after the fix round. Before it, $($failedBefore.Count) failed: $($failedBefore -join ', ')." } else { 'These are the checks after the fix round; all passed before it too.' }))
}
$evidenceMd = Read-Text (Join-Path $ChangeDir 'evidence.md')
if ($evidenceMd) {
    $lines.Add('')
    $lines.Add('## Screenshot evidence')
    $lines.Add('')
    $lines.Add($evidenceMd)
}
$lines.Add('')
$reviewModel = if ($reviewer) { $reviewer.model } else { 'unknown model' }
$lines.Add("## Review by a second agent ($reviewModel)")
$lines.Add('')
$lines.Add($(if ($reviewText) { $reviewText } else { 'The review did not run.' }))

# What the single fix round did about each problem it was given, and what it was not given.
$lines.Add('')
$lines.Add('## Fix round')
$lines.Add('')
$requested = @(if ($fixRequest) { $fixRequest.items })
if ($requested.Count -eq 0) {
    $lines.Add('Nothing to fix automatically: no check failed and no finding was marked implementable.')
}
else {
    $outcomes = @{}
    if ($fixReport -and $fixReport.PSObject.Properties['items']) {
        foreach ($item in @($fixReport.items)) { $outcomes[[string] $item.id] = $item }
    }
    $lines.Add('| Problem | Severity | Outcome |')
    $lines.Add('| --- | --- | --- |')
    foreach ($item in $requested) {
        $outcome = $outcomes[[string] $item.id]
        $status = if (-not $fixer) { 'fix round did not run' } elseif ($outcome) { "$(if ($outcome.status -eq 'fixed') { '✅ fixed' } else { '❌ not fixed' }): $($outcome.note)" } else { 'not reported' }
        $lines.Add("| $($item.id): $(($item.title -replace '\|', '\|')) | $($item.severity) | $(($status -replace '\r?\n', ' ' -replace '\|', '\|')) |")
    }
}
$left = @(if ($review) { $review.findings | Where-Object { $id = [string] $_.id; -not @($requested | Where-Object { $_.id -eq $id }).Count } })
if ($left.Count -gt 0) {
    $lines.Add('')
    $lines.Add("Left for the maintainer (not implementable, or nits): $(($left | ForEach-Object { "$($_.id) ($($_.severity))" }) -join ', ').")
}
$lines.Add('')
$lines.Add('## Run metrics')
$lines.Add('')
$lines.Add('| | Implementer | Reviewer | Fixer |')
$lines.Add('| --- | --- | --- | --- |')
$row = {
    param([string] $Label, [scriptblock] $Value)
    $a = if ($agent) { & $Value $agent } else { '' }
    $r = if ($reviewer) { & $Value $reviewer } else { '' }
    $f = if ($fixer) { & $Value $fixer } else { '' }
    $lines.Add("| $Label | $a | $r | $f |")
}
& $row 'Model' { param($m) "$($m.model)$(if ($m.requestedModel -and $m.requestedModel -ne $m.model) { " (asked: $($m.requestedModel))" })" }
& $row 'Duration' { param($m) Format-Duration $m.durationSeconds }
& $row 'Copilot AI credits' { param($m) $m.aiCredits }
& $row 'Tool calls (failed)' { param($m) "$($m.toolCalls) ($($m.failedToolCalls))" }
& $row 'Avalonia docs MCP calls' { param($m) "$($m.mcpCalls)$(if ($m.mcpTools) { ': ' + (($m.mcpTools | ForEach-Object { $_ -replace '^avalonia-docs-', '' }) -join ', ') })" }
if ($agent) {
    $lines.Add('')
    $lines.Add("Implementer: $($agent.builds) builds, $($agent.testRuns) test runs, $($agent.formatterRuns) formatter runs, $($agent.publishes) publishes, $($agent.waits) waits for the UI, $($agent.sleeps) sleeps, $($agent.clicks) clicks, $($agent.screenshots) screenshots.")
}
if ($fixer) {
    $lines.Add("Fixer: $($fixer.builds) builds, $($fixer.testRuns) test runs, $($fixer.formatterRuns) formatter runs, $($fixer.waits) waits for the UI, $($fixer.clicks) clicks, $($fixer.screenshots) screenshots.")
}
if ($change) {
    $lines.Add("Change: $(@($change.Files).Count) file(s), $(@($change.Screenshots).Count) screenshot(s).")
}
if ($phases.Count -gt 0) {
    $lines.Add('')
    $lines.Add('| Phase | Time |')
    $lines.Add('| --- | --- |')
    foreach ($phase in $phases) { $lines.Add("| $($phase.Phase) | $(Format-Duration $phase.Seconds) |") }
}

$report = $lines -join "`n"
$report | Set-Content -LiteralPath (Join-Path $OutputDir 'report.md') -Encoding utf8NoBOM
Write-Host $report
$description = Read-Text (Join-Path $ChangeDir 'pr.md')
"$description`n`n$report" | Set-Content -LiteralPath (Join-Path $OutputDir 'body.md') -Encoding utf8NoBOM

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    @($report, '', '## Pull request description', '', $description) -join "`n" |
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding utf8
}
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    @(
        "review-verdict=$verdict"
        "fixed=$(if ($fixReport -and $fixReport.PSObject.Properties['items']) { @($fixReport.items | Where-Object status -eq 'fixed').Count } else { 0 })"
        "checks-passed=$($checksPassed.ToString().ToLowerInvariant())"
    ) | Add-Content -Path $env:GITHUB_OUTPUT -Encoding utf8
}
