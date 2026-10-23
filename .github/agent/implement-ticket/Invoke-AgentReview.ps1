# Has a second agent, on a different model, review the implementing agent's change where it
# lies: in the working tree of the desktop job, on top of -BaseSha. The reviewer gets the
# change as a patch with the ticket, spec, description, check results and screenshots, and
# writes review.md and review.json (findings with an implementable flag, see
# REVIEW_PROMPT.md) to -OutputDir.
#
# The reviewer must not change the working tree. Its change is snapshotted first and put back
# if the review touched it, so a fix round only ever starts from the implementer's change.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BaseSha,

    # The implementer's output folder (checks.md, evidence.md, metrics.json).
    [Parameter(Mandatory)]
    [string] $ChangeDir,

    [Parameter(Mandatory)]
    [string] $OutputDir,

    [string[]] $Models = @('claude-sonnet-5.5', 'claude-opus-5.5', 'gpt-6-luna', 'auto')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The top of the working tree: git clean and git apply only reach paths below the directory
# they run in.
$repoRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$inputDir = Join-Path $OutputDir 'input'
New-Item -Path $inputDir -ItemType Directory -Force | Out-Null

function Get-WorkingChange {
    param([string] $Path)

    & git -C $repoRoot add --all
    & git -C $repoRoot diff --cached --binary "--output=$Path" $BaseSha
    & git -C $repoRoot reset --quiet
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

$snapshot = Join-Path $inputDir 'change.patch'
$before = Get-WorkingChange $snapshot

foreach ($file in @($env:AGENT_TICKET, $env:AGENT_SPEC, $env:AGENT_FEEDBACK, $env:AGENT_PR_BODY,
        (Join-Path $ChangeDir 'checks.md'), (Join-Path $ChangeDir 'evidence.md'))) {
    if ($file -and (Test-Path -LiteralPath $file)) { Copy-Item -LiteralPath $file -Destination $inputDir }
}
if ($env:AGENT_SCREENSHOTS -and (Test-Path -LiteralPath $env:AGENT_SCREENSHOTS)) {
    Copy-Item -LiteralPath $env:AGENT_SCREENSHOTS -Destination (Join-Path $inputDir 'screenshots') -Recurse
}

$implementer = Get-Content -LiteralPath (Join-Path $ChangeDir 'metrics.json') -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
$avoid = if ($implementer) { @($implementer.models) } else { @() }

$env:AGENT_REVIEW_INPUT = $inputDir
$env:AGENT_REVIEW_OUTPUT = $OutputDir
try {
    & (Join-Path $PSScriptRoot '..\Invoke-CopilotAgent.ps1') `
        -Prompt 'Read .github/agent/implement-ticket/REVIEW_PROMPT.md and follow it.' `
        -OutputDir $OutputDir `
        -Models $Models `
        -AvoidModels $avoid `
        -McpConfig (Join-Path $PSScriptRoot 'mcp-config.json')
}
finally {
    $after = Get-WorkingChange (Join-Path $OutputDir 'after-review.patch')
    if ($after -ne $before) {
        Write-Host '::warning::The reviewer changed the working tree; putting back the implementer''s change'
        & git -C $repoRoot reset --quiet --hard HEAD
        & git -C $repoRoot clean -fd --quiet
        & git -C $repoRoot apply --index $snapshot
        if ($LASTEXITCODE -ne 0) { throw 'Could not put back the implementer''s change after the review' }
        & git -C $repoRoot reset --quiet
    }
    Remove-Item -LiteralPath (Join-Path $OutputDir 'after-review.patch') -ErrorAction SilentlyContinue
}

# A review the next step can rely on: valid JSON with ids, or no findings and a warning.
$reviewPath = Join-Path $OutputDir 'review.json'
$review = $null
try { $review = Get-Content -LiteralPath $reviewPath -Raw | ConvertFrom-Json } catch { }
if ($null -eq $review -or -not $review.PSObject.Properties['findings']) {
    Write-Host '::warning::The reviewer wrote no usable review.json; no review finding will be fixed'
    $review = [pscustomobject]@{ verdict = 'unknown'; findings = @() }
}
$index = 1
foreach ($finding in @($review.findings)) {
    if (-not $finding.PSObject.Properties['id'] -or -not $finding.id) {
        $finding | Add-Member -NotePropertyName id -NotePropertyValue "R$index" -Force
    }
    if (-not $finding.PSObject.Properties['implementable']) {
        $finding | Add-Member -NotePropertyName implementable -NotePropertyValue $false -Force
    }
    $index++
}
$review | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reviewPath -Encoding utf8NoBOM
Write-Host "Review: $($review.verdict), $(@($review.findings).Count) finding(s), $(@($review.findings | Where-Object implementable).Count) implementable"
