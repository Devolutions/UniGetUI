# Decides what the single fix round works on, without an agent:
#   - every failed automated check (build, tests, formatting, translations, paths),
#   - every review finding marked implementable with severity blocker, major or minor.
# Nits and findings that are not implementable stay with the human reviewer.
#
# Writes fix-request.json and fix-request.md to -OutputDir and sets the needs-fix output.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ChecksPath,

    [Parameter(Mandatory)]
    [string] $ReviewPath,

    [Parameter(Mandatory)]
    [string] $OutputDir,

    [string[]] $Severities = @('blocker', 'major', 'minor'),

    [int] $MaxItems = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null
$checks = if (Test-Path -LiteralPath $ChecksPath) { Get-Content -LiteralPath $ChecksPath -Raw | ConvertFrom-Json } else { $null }
$review = if (Test-Path -LiteralPath $ReviewPath) { Get-Content -LiteralPath $ReviewPath -Raw | ConvertFrom-Json } else { $null }

$items = [System.Collections.Generic.List[object]]::new()
if ($checks) {
    foreach ($check in @($checks.checks | Where-Object { $_.status -eq 'failed' })) {
        $items.Add([pscustomobject]@{
                id = "check:$($check.name -replace '[^A-Za-z0-9.]+', '-')"
                source = 'check'
                severity = 'major'
                file = ''
                line = $null
                title = "The $($check.name) check failed"
                details = [string] $check.details
                fix = 'Make this check pass without weakening it: fix the code, add the missing strings, or apply the formatters.'
            })
    }
}
$severityOrder = @{ blocker = 0; major = 1; minor = 2; nit = 3 }
if ($review) {
    $selected = @($review.findings | Where-Object {
            $_.implementable -eq $true -and $Severities -contains ([string] $_.severity).ToLowerInvariant()
        } | Sort-Object { $severityOrder[([string] $_.severity).ToLowerInvariant()] })
    foreach ($finding in $selected) {
        $items.Add([pscustomobject]@{
                id = [string] $finding.id
                source = 'review'
                severity = ([string] $finding.severity).ToLowerInvariant()
                file = [string] $finding.file
                line = $finding.line
                title = [string] $finding.title
                details = if ($finding.PSObject.Properties['details']) { [string] $finding.details } else { '' }
                fix = if ($finding.PSObject.Properties['fix']) { [string] $finding.fix } else { '' }
            })
    }
}

$request = @($items | Select-Object -First $MaxItems)
$skipped = @($items | Select-Object -Skip $MaxItems)
[pscustomobject]@{ items = $request; skipped = $skipped } | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $OutputDir 'fix-request.json') -Encoding utf8NoBOM

$markdown = @(
    '# Problems to fix'
    ''
    'Fix each problem below as described. Report on every id.'
    foreach ($item in $request) {
        ''
        $where = if ($item.file) { " in ``$($item.file)$(if ($item.line) { ":$($item.line)" })``" } else { '' }
        "## $($item.id) ($($item.severity))$where"
        ''
        $item.title
        if ($item.details) { ''; $item.details }
        if ($item.fix) { ''; "Fix: $($item.fix)" }
    }
)
$markdown -join "`n" | Set-Content -LiteralPath (Join-Path $OutputDir 'fix-request.md') -Encoding utf8NoBOM

Write-Host "$($request.Count) problem(s) for the fix round: $(($request | ForEach-Object id) -join ', ')"
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    "needs-fix=$(($request.Count -gt 0).ToString().ToLowerInvariant())" | Add-Content -Path $env:GITHUB_OUTPUT -Encoding utf8
}
