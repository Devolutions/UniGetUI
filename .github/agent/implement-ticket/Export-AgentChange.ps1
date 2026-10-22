# Collects what the agent produced into one directory for the pull-request job: the change
# as a binary git patch against the base commit (whether the agent committed or not), the
# pull request description, and the screenshots it references.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BaseSha,

    [Parameter(Mandatory)]
    [string] $PullRequestBody,

    [Parameter(Mandatory)]
    [string] $ScreenshotsDir,

    [Parameter(Mandatory)]
    [string] $OutputDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Git {
    param(
        [Parameter(ValueFromRemainingArguments)]
        [string[]] $Arguments
    )

    $output = & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
    return $output
}

New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null
$patchPath = Join-Path $OutputDir 'change.patch'

# Workflow scaffolding in the workspace (IronRDP checkout, artifacts, NuGet cache) is
# excluded through .git/info/exclude, which the workflow wrote before the agent started.
Invoke-Git add --all | Out-Null
$files = @(Invoke-Git diff --cached --name-only $BaseSha)
# git writes the files itself, so PowerShell can't re-encode the patch or change its line endings.
Invoke-Git diff --cached --binary "--output=$patchPath" $BaseSha | Out-Null
Invoke-Git diff --cached --stat "--output=$(Join-Path $OutputDir 'change.stat')" $BaseSha | Out-Null

$hasChanges = $files.Count -gt 0
Write-Host "Agent changed $($files.Count) file(s)"
$files | ForEach-Object { Write-Host "  $_" }

if (Test-Path -LiteralPath $PullRequestBody) {
    Copy-Item -LiteralPath $PullRequestBody -Destination (Join-Path $OutputDir 'pr.md')
}
else {
    Write-Warning "The agent did not write a pull request description to $PullRequestBody"
}

$screenshotsOut = Join-Path $OutputDir 'screenshots'
New-Item -Path $screenshotsOut -ItemType Directory -Force | Out-Null
if (Test-Path -LiteralPath $ScreenshotsDir) {
    Get-ChildItem -LiteralPath $ScreenshotsDir -Filter '*.png' -File |
        Copy-Item -Destination $screenshotsOut
}

$screenshotNames = @(Get-ChildItem -LiteralPath $screenshotsOut -Filter '*.png' -File | ForEach-Object { $_.Name })
# The prompt asks for an after-*.png of every acceptance criterion; without one, nothing
# shows the change working, so the run gets a warning a reviewer will see.
$hasAfterScreenshots = @($screenshotNames | Where-Object { $_ -like 'after-*' }).Count -gt 0
if ($hasChanges -and -not $hasAfterScreenshots) {
    Write-Host '::warning::The agent changed code but saved no after-*.png screenshot showing the change working'
}

[pscustomobject]@{
    BaseSha = $BaseSha
    HasChanges = $hasChanges
    HasAfterScreenshots = $hasAfterScreenshots
    Files = $files
    Screenshots = $screenshotNames
} | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $OutputDir 'change.json') -Encoding utf8NoBOM

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    "has-changes=$($hasChanges.ToString().ToLowerInvariant())" | Add-Content -Path $env:GITHUB_OUTPUT -Encoding utf8
}
