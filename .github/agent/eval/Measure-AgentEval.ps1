# Scores an agent-eval.yml run. Each case replays a merged pull request: the agent gets the
# problem it solved, starting from the commit before it. For each case this reads the
# implement run's artifact (eval-<id>-agent-change, with its review and fix round) and compares the
# files the agent changed with the files of the human pull request.
#
# Writes eval-report.md and eval-report.json to -OutputDir and the report to the job summary.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $CasesPath,

    # Directory holding the downloaded artifacts, one folder per artifact.
    [Parameter(Mandatory)]
    [string] $ArtifactsDir,

    [Parameter(Mandatory)]
    [string] $OutputDir,

    # Case ids that ran (comma-separated); empty means every case.
    [string] $CaseIds,

    [string] $Repository = $env:GITHUB_REPOSITORY
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Json {
    param([string] $Path)
    if (Test-Path -LiteralPath $Path) { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    return $null
}

$wanted = @($CaseIds -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$cases = @(Get-Content -LiteralPath $CasesPath -Raw | ConvertFrom-Json | Where-Object { $wanted.Count -eq 0 -or $wanted -contains $_.id })
New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null

$rows = foreach ($case in $cases) {
    $changeDir = Join-Path $ArtifactsDir "eval-$($case.id)-agent-change"
    $reviewDir = Join-Path $changeDir 'review'
    $change = Read-Json (Join-Path $changeDir 'change.json')
    $checks = Read-Json (Join-Path $changeDir 'checks.json')
    $metrics = Read-Json (Join-Path $changeDir 'metrics.json')
    $evidence = Read-Json (Join-Path $changeDir 'evidence.json')
    $review = Read-Json (Join-Path $reviewDir 'review.json')
    $checksBefore = Read-Json (Join-Path $changeDir 'before-fix/checks.json')
    $fixRequest = Read-Json (Join-Path $changeDir 'fix/fix-request.json')
    $fixReport = Read-Json (Join-Path $changeDir 'fix/fix-report.json')

    $humanFiles = @(& gh api "repos/$Repository/pulls/$($case.pr)/files" --paginate --jq '.[].filename')
    $agentFiles = @(if ($change) { $change.Files })
    $common = @($agentFiles | Where-Object { $humanFiles -contains $_ })
    $union = @($agentFiles + $humanFiles | Sort-Object -Unique)
    $overlap = if ($union.Count -gt 0) { [Math]::Round($common.Count / $union.Count, 2) } else { 0 }

    $checkStatus = { param($Name) if ($checks) { $c = @($checks.checks | Where-Object { $_.name -eq $Name -or $_.name -like "$Name*" }); if ($c.Count -eq 0) { 'n/a' } elseif (@($c | Where-Object status -eq 'failed').Count) { 'failed' } elseif (@($c | Where-Object status -eq 'passed').Count) { 'passed' } else { 'skipped' } } else { 'n/a' } }
    [pscustomobject]@{
        id = $case.id
        pr = $case.pr
        changed = [bool] ($change -and $change.HasChanges)
        build = & $checkStatus 'build'
        tests = & $checkStatus 'tests'
        formatting = & $checkStatus 'formatting'
        translations = & $checkStatus 'translations'
        review = if ($review) { $review.verdict } else { 'n/a' }
        reviewFindings = if ($review) { @($review.findings).Count } else { $null }
        failedChecksBeforeFix = if ($checksBefore) { @($checksBefore.checks | Where-Object status -eq 'failed').Count } elseif ($checks) { @($checks.checks | Where-Object status -eq 'failed').Count } else { $null }
        fixRequested = if ($fixRequest) { @($fixRequest.items).Count } else { 0 }
        fixed = if ($fixReport -and $fixReport.PSObject.Properties['items']) { @($fixReport.items | Where-Object status -eq 'fixed').Count } else { 0 }
        unresolvedMajor = if ($review) {
            $fixedIds = if ($fixReport -and $fixReport.PSObject.Properties['items']) { @($fixReport.items | Where-Object status -eq 'fixed' | ForEach-Object { [string] $_.id }) } else { @() }
            @($review.findings | Where-Object { ([string] $_.severity) -in @('blocker', 'major') -and $fixedIds -notcontains [string] $_.id }).Count
        } else { $null }
        agentFiles = $agentFiles.Count
        humanFiles = $humanFiles.Count
        fileOverlap = $overlap
        screenshots = if ($evidence) { $evidence.screenshots } else { 0 }
        evidenceWarnings = if ($evidence) { @($evidence.warnings).Count } else { $null }
        model = if ($metrics) { $metrics.model } else { '' }
        minutes = if ($metrics) { [Math]::Round($metrics.durationSeconds / 60, 1) } else { $null }
        aiCredits = if ($metrics) { $metrics.aiCredits } else { $null }
        mcpCalls = if ($metrics) { $metrics.mcpCalls } else { $null }
    }
}
$rows = @($rows)

$passed = { param($Value) $Value -in @('passed', 'skipped', 'n/a') }
$score = @($rows | Where-Object { $_.changed -and (& $passed $_.build) -and (& $passed $_.tests) -and (& $passed $_.formatting) -and (& $passed $_.translations) -and $_.unresolvedMajor -eq 0 }).Count

$markdown = @(
    '## Agent eval'
    ''
    "$score of $($rows.Count) case(s) ended with a change that passed every check and had no blocker or major review finding left unfixed after the fix round."
    ''
    '| Case | Changed | Build | Tests | Format | Translations | Review (findings) | Fix round (fixed / asked; failed checks before) | Files (agent / human, overlap) | Screenshots (warnings) | Model | Minutes | AI credits | MCP calls |'
    '| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |'
    foreach ($row in $rows) {
        "| [#$($row.pr)](https://github.com/$Repository/pull/$($row.pr)) | $($row.changed) | $($row.build) | $($row.tests) | $($row.formatting) | $($row.translations) | $($row.review) ($($row.reviewFindings)) | $($row.fixed) / $($row.fixRequested); $($row.failedChecksBeforeFix) | $($row.agentFiles) / $($row.humanFiles), $($row.fileOverlap) | $($row.screenshots) ($($row.evidenceWarnings)) | $($row.model) | $($row.minutes) | $($row.aiCredits) | $($row.mcpCalls) |"
    }
    ''
    'File overlap is the share of files changed by either the agent or the human pull request that both changed (1 is the same set of files).'
) -join "`n"

$markdown | Set-Content -LiteralPath (Join-Path $OutputDir 'eval-report.md') -Encoding utf8NoBOM
[pscustomobject]@{ score = $score; cases = $rows } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDir 'eval-report.json') -Encoding utf8NoBOM
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    $markdown | Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding utf8
}
Write-Host $markdown
