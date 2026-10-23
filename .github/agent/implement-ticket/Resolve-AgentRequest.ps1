# Turns whatever started the agent workflow into one request for the jobs after it:
#   workflow_dispatch / workflow_call  the run's inputs
#   issues (labeled)                   agent:spec writes a spec, agent:implement implements
#   issue_comment                      "/agent spec" or "/agent implement [guidance]" on an
#                                      issue; "/agent <feedback>" on an agent pull request
#                                      iterates on it
# Only maintainers (write access) can start a run from a label or comment. This runs in a job
# without an agent; it acknowledges the request on the issue or pull request, and writes the
# ticket, the approved spec, the feedback and any previous change to -OutputDir for the agent.
#
# Outputs (GITHUB_OUTPUT): proceed, mode (spec|implement), issue, pr, base-sha, head-branch,
# create-pr. With -DryRun nothing is written to GitHub.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $OutputDir,

    [string] $Repository = $env:GITHUB_REPOSITORY,

    [string] $EventName = $env:GITHUB_EVENT_NAME,

    [string] $EventPath = $env:GITHUB_EVENT_PATH,

    [string] $DefaultSha = $env:GITHUB_SHA,

    [string] $RunUrl = "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID",

    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$SpecLabel = 'agent:spec'
$ImplementLabel = 'agent:implement'
$SpecBegin = '<!-- agent-spec-begin -->'
$SpecEnd = '<!-- agent-spec-end -->'

function Invoke-Gh {
    param([Parameter(ValueFromRemainingArguments)] [string[]] $Arguments)

    $output = & gh @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
    return $output
}

function Get-Input {
    param([string] $Name)
    $value = [Environment]::GetEnvironmentVariable("IN_$Name")
    if ($null -eq $value) { return '' }
    return $value.Trim()
}

function Set-Outputs {
    param([System.Collections.IDictionary] $Values)

    $lines = foreach ($key in $Values.Keys) { "$key=$($Values[$key])" }
    $lines | ForEach-Object { Write-Host "  $_" }
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        $lines | Add-Content -Path $env:GITHUB_OUTPUT -Encoding utf8
    }
}

function Stop-Request {
    param([string] $Reason)

    Write-Host "Not starting the agent: $Reason"
    Set-Outputs @{ proceed = 'false'; reason = $Reason }
    exit 0
}

function Test-Maintainer {
    param([string] $Login)

    if ([string]::IsNullOrWhiteSpace($Login)) { return $false }
    $permission = (Invoke-Gh api "repos/$Repository/collaborators/$Login/permission" --jq .permission) -as [string]
    return $permission -in @('admin', 'maintain', 'write')
}

function Write-GitHubComment {
    param([string] $Number, [string] $Body)

    if ($DryRun) { Write-Host "[dry run] comment on #${Number}: $Body"; return }
    $Body | Invoke-Gh issue comment $Number --repo $Repository --body-file - | Out-Null
}

# The issue as the agent's ticket. Comments from bots (this workflow's own specs and status
# comments) are left out; the approved spec is passed separately.
function Get-IssueTicket {
    param([string] $Number)

    $issue = Invoke-Gh issue view $Number --repo $Repository --json 'number,title,body,comments' | ConvertFrom-Json
    $text = [System.Text.StringBuilder]::new()
    [void] $text.AppendLine("# $($issue.title)").AppendLine().AppendLine($issue.body)
    foreach ($comment in @($issue.comments)) {
        if ($comment.author.login -like '*[[]bot]' -or $comment.author.login -eq 'github-actions') { continue }
        [void] $text.AppendLine().AppendLine("## Comment by $($comment.author.login)").AppendLine().AppendLine($comment.body)
    }
    return $text.ToString()
}

# The most recent spec this workflow posted on the issue, if any.
function Get-IssueSpec {
    param([string] $Number)

    $comments = Invoke-Gh api "repos/$Repository/issues/$Number/comments" --paginate | ConvertFrom-Json
    $spec = @($comments | Where-Object {
            $_.user.type -eq 'Bot' -and $_.body.Contains($SpecBegin) -and $_.body.Contains($SpecEnd)
        }) | Select-Object -Last 1
    if ($null -eq $spec) { return $null }
    $start = $spec.body.IndexOf($SpecBegin) + $SpecBegin.Length
    return $spec.body.Substring($start, $spec.body.IndexOf($SpecEnd) - $start).Trim()
}

function Save-RunArtifact {
    param([string] $RunId, [string] $Name, [string] $Destination)

    New-Item -Path $Destination -ItemType Directory -Force | Out-Null
    Invoke-Gh run download $RunId --repo $Repository --name $Name --dir $Destination | Out-Null
}

New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null
$event = if ($EventPath -and (Test-Path -LiteralPath $EventPath)) { Get-Content -LiteralPath $EventPath -Raw | ConvertFrom-Json -Depth 32 } else { $null }

$mode = ''
$issue = ''
$pr = ''
$guidance = ''
$feedback = ''
$createPr = 'true'
$commentId = ''
$ackOn = ''

switch ($EventName) {
    'issues' {
        $label = [string] $event.label.name
        $mode = switch ($label) { $SpecLabel { 'spec' } $ImplementLabel { 'implement' } default { '' } }
        if (-not $mode) { Stop-Request "label '$label' is not an agent label" }
        $issue = [string] $event.issue.number
        if (-not (Test-Maintainer $event.sender.login)) { Stop-Request "$($event.sender.login) does not have write access" }
        # The label is a one-shot trigger; removing it lets a maintainer add it again later.
        if (-not $DryRun) { Invoke-Gh issue edit $issue --repo $Repository --remove-label $label | Out-Null }
        $ackOn = $issue
    }
    'issue_comment' {
        $body = [string] $event.comment.body
        $firstLine, $rest = $body -split "`r?`n", 2
        if ($firstLine -notmatch '^\s*/agent(\s+(?<args>.*))?$') { Stop-Request 'the comment is not an /agent command' }
        $commandArgs = $Matches['args']
        if ($event.comment.user.type -eq 'Bot') { Stop-Request 'the comment is from a bot' }
        if ($event.comment.author_association -notin @('OWNER', 'MEMBER', 'COLLABORATOR') -or -not (Test-Maintainer $event.comment.user.login)) {
            Stop-Request "$($event.comment.user.login) does not have write access"
        }
        $commentId = [string] $event.comment.id
        $text = (@($commandArgs, $rest) | Where-Object { $_ }) -join "`n"

        if ($event.issue.PSObject.Properties['pull_request'] -and $event.issue.pull_request) {
            $mode = 'implement'
            $pr = [string] $event.issue.number
            $feedback = $text.Trim()
            if (-not $feedback) { Stop-Request 'the /agent comment on the pull request has no feedback' }
        }
        else {
            $issue = [string] $event.issue.number
            if ($text -match '^\s*spec\b(?<more>[\s\S]*)$') { $mode = 'spec'; $guidance = $Matches['more'].Trim() }
            elseif ($text -match '^\s*implement\b(?<more>[\s\S]*)$') { $mode = 'implement'; $guidance = $Matches['more'].Trim() }
            else {
                Write-GitHubComment $issue "Use ``/agent spec`` to have the agent propose a spec for this issue, then ``/agent implement`` to implement the approved spec (optionally followed by extra guidance)."
                Stop-Request 'unknown /agent command'
            }
        }
        $ackOn = if ($pr) { $pr } else { $issue }
    }
    { $_ -in @('workflow_dispatch', 'workflow_call') } {
        $mode = (Get-Input 'MODE'), 'implement' | Where-Object { $_ } | Select-Object -First 1
        $issue = Get-Input 'ISSUE'
        $pr = Get-Input 'PR'
        $guidance = Get-Input 'TICKET'
        $feedback = Get-Input 'FEEDBACK'
        $createPr = if ((Get-Input 'CREATE_PR') -eq 'false') { 'false' } else { 'true' }
    }
    default { Stop-Request "event $EventName is not handled" }
}

# Base commit, and for an iteration the branch the result goes on.
$baseSha = (Get-Input 'BASE_SHA'), $DefaultSha | Where-Object { $_ } | Select-Object -First 1
$headBranch = ''
$ticket = ''
if ($pr) {
    $pull = Invoke-Gh pr view $pr --repo $Repository --json 'number,title,body,headRefName,headRefOid,headRepositoryOwner,state' | ConvertFrom-Json
    if ($pull.state -ne 'OPEN') { Stop-Request "pull request #$pr is not open" }
    if ($pull.headRepositoryOwner.login -ne $Repository.Split('/')[0] -or -not $pull.headRefName.StartsWith('agent/')) {
        Stop-Request "pull request #$pr is not an agent pull request in this repository"
    }
    $baseSha = $pull.headRefOid
    $headBranch = $pull.headRefName
    $ticket = "# $($pull.title)`n`n$($pull.body)"
    $linked = [regex]::Match([string] $pull.body, '(?m)^Refs #(?<n>\d+)')
    if ($linked.Success) { $issue = $linked.Groups['n'].Value }
}
elseif ($issue) {
    $ticket = Get-IssueTicket $issue
}
if ($guidance) {
    $ticket = if ($ticket) { "$ticket`n`n## Additional guidance from the maintainer who started this run`n`n$guidance" } else { $guidance }
}
if ([string]::IsNullOrWhiteSpace($ticket)) {
    throw 'Give an issue number, a pull request number or a ticket text'
}
$ticket | Set-Content -LiteralPath (Join-Path $OutputDir 'ticket.md') -Encoding utf8NoBOM

# The approved spec, from a spec run or from the issue.
$specRunId = Get-Input 'SPEC_RUN_ID'
$specSource = ''
if ($mode -eq 'implement') {
    $spec = $null
    if ($specRunId) {
        $specDir = Join-Path $OutputDir 'spec-run'
        Save-RunArtifact $specRunId 'agent-spec' $specDir
        $spec = Get-Content -LiteralPath (Join-Path $specDir 'spec.md') -Raw
        Remove-Item -LiteralPath $specDir -Recurse -Force
        $specSource = "run $specRunId"
    }
    elseif ($issue -and -not $pr) {
        $spec = Get-IssueSpec $issue
        if ($spec) { $specSource = "issue #$issue" }
    }
    if ($spec) {
        $spec | Set-Content -LiteralPath (Join-Path $OutputDir 'spec.md') -Encoding utf8NoBOM
    }
}

# Iteration: feedback, and the earlier change the agent continues from. For a pull request it
# is already on the branch. A previous run's change is applied on top of that run's base,
# after any changes that run itself continued from (previous-01.patch, previous-02.patch...).
$continueRunId = Get-Input 'CONTINUE_RUN_ID'
if ($continueRunId) {
    $previousDir = Join-Path $OutputDir 'previous'
    Save-RunArtifact $continueRunId 'agent-request' (Join-Path $previousDir 'request')
    Save-RunArtifact $continueRunId 'agent-change' (Join-Path $previousDir 'change')
    $earlier = @(Get-ChildItem -LiteralPath (Join-Path $previousDir 'request') -Filter 'previous-*.patch' -File | Sort-Object Name)
    $index = 1
    foreach ($patch in $earlier) {
        Copy-Item -LiteralPath $patch.FullName -Destination (Join-Path $OutputDir ('previous-{0:00}.patch' -f $index))
        $index++
    }
    Copy-Item -LiteralPath (Join-Path $previousDir 'change/change.patch') -Destination (Join-Path $OutputDir ('previous-{0:00}.patch' -f $index))
    $baseSha = (Get-Content -LiteralPath (Join-Path $previousDir 'request/request.json') -Raw | ConvertFrom-Json).baseSha
    Remove-Item -LiteralPath $previousDir -Recurse -Force
    # The continued change only exists in that run's artifacts, so there is no branch to open
    # a pull request from.
    $createPr = 'false'
}
if ($feedback) {
    $feedback | Set-Content -LiteralPath (Join-Path $OutputDir 'feedback.md') -Encoding utf8NoBOM
}

[ordered]@{
    event = $EventName
    mode = $mode
    issue = $issue
    pr = $pr
    baseSha = $baseSha
    headBranch = $headBranch
    createPr = $createPr
    spec = $specSource
    continueRunId = $continueRunId
    hasFeedback = [bool] $feedback
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir 'request.json') -Encoding utf8NoBOM

if ($ackOn) {
    if ($commentId -and -not $DryRun) {
        & gh api --method POST "repos/$Repository/issues/comments/$commentId/reactions" -f content=eyes *> $null
    }
    $what = if ($mode -eq 'spec') { 'Writing a spec' } elseif ($pr) { 'Working on the feedback' } else { 'Implementing' }
    $specNote = if ($mode -eq 'implement' -and $specSource) { " from the approved spec ($specSource)" } else { '' }
    Write-GitHubComment $ackOn "$what${specNote}: [agent run]($RunUrl)."
}

Set-Outputs ([ordered]@{
        proceed = 'true'
        mode = $mode
        issue = $issue
        pr = $pr
        'base-sha' = $baseSha
        'head-branch' = $headBranch
        'create-pr' = $createPr
    })
