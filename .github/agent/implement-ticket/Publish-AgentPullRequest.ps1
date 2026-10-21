# Turns the agent's output (from Export-AgentChange.ps1) into a draft pull request. This runs
# in a separate job from the agent, with the write token the agent never had:
#   1. checks the patch and refuses changes under .github/,
#   2. commits it on a new branch from the base commit and pushes it,
#   3. publishes the screenshots on the agent-assets branch, so the description can show them,
#   4. opens a draft pull request that links the ticket and the workflow run.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ChangeDir,

    [Parameter(Mandatory)]
    [string] $Repository,

    [Parameter(Mandatory)]
    [string] $BaseBranch,

    [Parameter(Mandatory)]
    [string] $BranchName,

    [Parameter(Mandatory)]
    [string] $RunUrl,

    [string] $RunId = $env:GITHUB_RUN_ID,

    # Issue number of the ticket, if it came from an issue.
    [string] $IssueNumber,

    [string] $AssetsBranch = 'agent-assets'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Native {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter(ValueFromRemainingArguments)] [string[]] $Arguments
    )

    $output = & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
    return $output
}

$patchPath = Join-Path $ChangeDir 'change.patch'
$bodyPath = Join-Path $ChangeDir 'pr.md'
$screenshotsDir = Join-Path $ChangeDir 'screenshots'

if (-not (Test-Path -LiteralPath $patchPath) -or (Get-Item -LiteralPath $patchPath).Length -eq 0) {
    throw 'The agent produced no change'
}

# 1. Check the patch.
$changedPaths = @(Invoke-Native git apply --numstat $patchPath | ForEach-Object { ($_ -split "`t", 3)[2] })
$forbidden = @($changedPaths | Where-Object { $_ -match '^\.github/' -or $_ -match '(^|/)\.git(/|$)' })
if ($forbidden.Count -gt 0) {
    throw "The agent's patch changes paths it must not touch: $($forbidden -join ', ')"
}
Invoke-Native git apply --check $patchPath | Out-Null

# Title and body from the agent's description; the first "# " line is the title.
$title = $null
$bodyLines = @()
if (Test-Path -LiteralPath $bodyPath) {
    foreach ($line in Get-Content -LiteralPath $bodyPath) {
        if ($null -eq $title -and $line -match '^#\s+(?<title>.+)$') {
            $title = $Matches.title.Trim()
            continue
        }
        $bodyLines += $line
    }
}
if ([string]::IsNullOrWhiteSpace($title)) {
    $title = if ($IssueNumber) { "Implement #$IssueNumber" } else { 'Implement ticket' }
}
if ($title.Length -gt 120) {
    $title = $title.Substring(0, 117) + '...'
}

# 2. Commit on a new branch and push it.
Invoke-Native git config user.name 'github-actions[bot]' | Out-Null
Invoke-Native git config user.email '41898282+github-actions[bot]@users.noreply.github.com' | Out-Null
Invoke-Native git switch --create $BranchName | Out-Null
Invoke-Native git apply --index $patchPath | Out-Null
$commitMessage = @(
    $title
    ''
    "Implemented by the implement-ticket agent workflow: $RunUrl"
    $(if ($IssueNumber) { "Refs #$IssueNumber" })
) -join "`n"
Invoke-Native git commit --quiet --message $commitMessage | Out-Null
Invoke-Native git push --quiet origin "HEAD:refs/heads/$BranchName" | Out-Null
Write-Host "Pushed $BranchName"

# 3. Publish the screenshots on the assets branch, in a folder for this run.
$screenshots = @()
if (Test-Path -LiteralPath $screenshotsDir) {
    $screenshots = @(Get-ChildItem -LiteralPath $screenshotsDir -Filter '*.png' -File)
}
$assetsBaseUrl = "https://raw.githubusercontent.com/$Repository/$AssetsBranch/$RunId"
if ($screenshots.Count -gt 0) {
    # Each run writes only its own folder, so a push that loses a race just retries on top
    # of the new branch head.
    $assetsDir = Join-Path ([System.IO.Path]::GetTempPath()) "agent-assets-$RunId"
    $published = $false
    for ($attempt = 1; $attempt -le 5 -and -not $published; $attempt++) {
        Remove-Item -LiteralPath $assetsDir -Recurse -Force -ErrorAction SilentlyContinue
        Invoke-Native git worktree prune | Out-Null
        & git ls-remote --exit-code --heads origin $AssetsBranch *> $null
        if ($LASTEXITCODE -eq 0) {
            Invoke-Native git fetch --quiet --depth 1 origin $AssetsBranch | Out-Null
            Invoke-Native git worktree add --quiet --detach $assetsDir FETCH_HEAD | Out-Null
        }
        else {
            Invoke-Native git worktree add --quiet --detach $assetsDir | Out-Null
            Invoke-Native git -C $assetsDir checkout --quiet --orphan "$AssetsBranch-$RunId" | Out-Null
            Invoke-Native git -C $assetsDir rm -r --force --quiet . | Out-Null
            "Screenshots published by the implement-ticket agent workflow, one folder per run. Not part of UniGetUI." |
                Set-Content -Path (Join-Path $assetsDir 'README.md') -Encoding utf8NoBOM
        }

        $runDir = Join-Path $assetsDir $RunId
        New-Item -Path $runDir -ItemType Directory -Force | Out-Null
        $screenshots | Copy-Item -Destination $runDir
        Invoke-Native git -C $assetsDir add --all | Out-Null
        Invoke-Native git -C $assetsDir commit --quiet --message "Screenshots for $RunUrl" | Out-Null
        & git -C $assetsDir push --quiet origin "HEAD:refs/heads/$AssetsBranch" 2>&1 | Write-Host
        $published = $LASTEXITCODE -eq 0
        Invoke-Native git worktree remove --force $assetsDir | Out-Null
        if (-not $published) {
            Write-Warning "Publishing screenshots failed (attempt $attempt); retrying"
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
    if (-not $published) {
        throw "Could not publish screenshots to the $AssetsBranch branch"
    }
}

# 4. Open the draft pull request. Screenshot links point at the published copies, and any
# screenshot the description does not show is listed at the end.
$body = ($bodyLines -join "`n").Trim()
$referenced = @{}
$body = [regex]::Replace($body, '\]\(\s*(\./)?screenshots/(?<name>[^)\s]+)\s*\)', {
        param($match)
        $name = $match.Groups['name'].Value
        $referenced[$name] = $true
        "]($assetsBaseUrl/$([uri]::EscapeDataString($name)))"
    })
$unreferenced = @($screenshots | Where-Object { -not $referenced.ContainsKey($_.Name) })
if ($unreferenced.Count -gt 0) {
    $body += "`n`n## More screenshots`n"
    foreach ($screenshot in $unreferenced) {
        $body += "`n![$($screenshot.BaseName)]($assetsBaseUrl/$([uri]::EscapeDataString($screenshot.Name)))`n"
    }
}

$footer = @(
    ''
    '---'
    $(if ($IssueNumber) { "Refs #$IssueNumber" })
    ''
    "> [!NOTE]"
    "> Draft opened by the [implement-ticket agent workflow]($RunUrl). The change and the screenshots were produced by an AI agent and have not been reviewed yet."
) -join "`n"
$body = "$body`n$footer"
$finalBodyPath = Join-Path $ChangeDir 'pr-final.md'
$body | Set-Content -LiteralPath $finalBodyPath -Encoding utf8NoBOM

$prOutput = & gh pr create `
    --repo $Repository `
    --draft `
    --base $BaseBranch `
    --head $BranchName `
    --title $title `
    --body-file $finalBodyPath 2>&1
if ($LASTEXITCODE -ne 0) {
    $message = ($prOutput | Out-String).Trim()
    if ($message -notmatch 'not permitted to create or approve pull requests') {
        throw "gh pr create failed: $message"
    }

    # The repository doesn't let GitHub Actions open pull requests, and no token that may was
    # given. The branch and screenshots are pushed, so hand a maintainer a one-click link
    # and the description instead of failing the run.
    $compareUrl = "https://github.com/$Repository/compare/$([uri]::EscapeDataString($BaseBranch))...$([uri]::EscapeDataString($BranchName))?expand=1&title=$([uri]::EscapeDataString($title))"
    Write-Host "::warning::GitHub Actions may not open pull requests in this repository. Open it from $compareUrl (the description is in the job summary)."
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        @(
            '## Pull request not opened'
            ''
            'This repository does not allow GitHub Actions to create pull requests, and no `GH_AW_GITHUB_TOKEN` secret was available.'
            "The change is on branch ``$BranchName``. [Open the pull request]($compareUrl) (mark it as a draft) and paste the description below."
            ''
            '````markdown'
            $body
            '````'
            ''
            '### Preview'
            ''
            $body
        ) -join "`n" | Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding utf8
    }
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        "compare-url=$compareUrl" | Add-Content -Path $env:GITHUB_OUTPUT -Encoding utf8
    }
    return
}

$prUrl = ($prOutput | Select-Object -Last 1).ToString().Trim()
Write-Host "Opened $prUrl"

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    "pr-url=$prUrl" | Add-Content -Path $env:GITHUB_OUTPUT -Encoding utf8
}
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    "Draft pull request: $prUrl" | Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding utf8
}
