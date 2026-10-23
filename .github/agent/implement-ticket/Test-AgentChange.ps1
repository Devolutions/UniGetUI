# Checks the agent's change after it finishes, so the pull request reports results the agent
# did not write itself:
#   paths         nothing under .github/ changed
#   build         the app builds (Start-UniGetUIDev.ps1 -NoLaunch)
#   tests         the test projects that depend on a changed project pass
#   formatting    both AGENTS.md formatters leave the changed C# files unchanged
#   translations  every literal user-facing string the change adds is in lang_en.json
#
# Writes checks.json and checks.md to -OutputDir and sets the checks-passed output. A failed
# check does not fail the step: the report goes to the pull request for a reviewer.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BaseSha,

    [Parameter(Mandatory)]
    [string] $OutputDir,

    [int] $TestBudgetSeconds = 600
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$logDir = Join-Path $OutputDir 'check-logs'
New-Item -Path $logDir -ItemType Directory -Force | Out-Null
$results = [System.Collections.Generic.List[object]]::new()

function Add-Result {
    param([string] $Name, [ValidateSet('passed', 'failed', 'skipped')] [string] $Status, [string] $Details, [double] $Seconds = 0)

    $results.Add([pscustomobject]@{ name = $Name; status = $Status; details = $Details; seconds = [int] $Seconds })
    Write-Host "[$Status] $Name ($([int] $Seconds)s) $Details"
}

# Runs a native command, logging its output to a file, and returns its exit code.
function Invoke-Logged {
    param([string] $LogName, [string] $FilePath, [string[]] $Arguments)

    $log = Join-Path $logDir $LogName
    & $FilePath @Arguments *> $log
    return $LASTEXITCODE
}

function Get-LogTail {
    param([string] $LogName, [int] $Lines = 15)
    return ((Get-Content -LiteralPath (Join-Path $logDir $LogName) -Tail $Lines) -join "`n").Trim()
}

Push-Location $repoRoot
try {
    & git add --all
    $changed = @(& git diff --cached --name-only $BaseSha)
    $addedLines = @(& git diff --cached --unified=0 $BaseSha -- '*.cs' '*.axaml' | Where-Object { $_ -match '^\+[^+]' })
    & git reset --quiet

    # paths
    $forbidden = @($changed | Where-Object { $_ -match '^\.github/' })
    if ($forbidden.Count -gt 0) { Add-Result 'paths' 'failed' "changes under .github/: $($forbidden -join ', ')" }
    else { Add-Result 'paths' 'passed' "$($changed.Count) file(s) changed, none under .github/" }

    $csFiles = @($changed | Where-Object { $_ -like '*.cs' -and (Test-Path -LiteralPath $_) })
    $codeChanged = @($changed | Where-Object { $_ -match '\.(cs|axaml|csproj|props|targets)$' }).Count -gt 0

    # build
    & (Join-Path $PSScriptRoot 'Stop-UniGetUIDev.ps1') | Out-Null
    $started = Get-Date
    if (-not $codeChanged) {
        Add-Result 'build' 'skipped' 'no code changed'
    }
    else {
        try {
            & (Join-Path $PSScriptRoot 'Start-UniGetUIDev.ps1') -NoLaunch *> (Join-Path $logDir 'build.log')
            Add-Result 'build' 'passed' 'Debug win-x64 build succeeded' ((Get-Date) - $started).TotalSeconds
        }
        catch {
            Add-Result 'build' 'failed' "$($_.Exception.Message)`n$(Get-LogTail 'build.log')" ((Get-Date) - $started).TotalSeconds
        }
    }

    # tests: test projects that contain a changed file or reference a changed project directly,
    # within a time budget (the pull request's own CI runs every test project).
    $projects = @{}
    foreach ($csproj in Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -Filter '*.csproj' -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) {
        $references = @(Select-Xml -Path $csproj.FullName -XPath '//ProjectReference' | ForEach-Object {
                [System.IO.Path]::GetFullPath((Join-Path $csproj.DirectoryName ($_.Node.Include -replace '\\', '/')))
            })
        $projects[$csproj.FullName] = [pscustomobject]@{ Path = $csproj.FullName; Dir = $csproj.DirectoryName + [System.IO.Path]::DirectorySeparatorChar; References = $references }
    }
    $changedProjects = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $changed) {
        $full = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $file))
        $owner = $projects.Values | Where-Object { $full.StartsWith($_.Dir, [StringComparison]::OrdinalIgnoreCase) } |
            Sort-Object { $_.Dir.Length } -Descending | Select-Object -First 1
        if ($owner) { [void] $changedProjects.Add($owner.Path) }
    }
    # The TUI tests take about five minutes and their test host sometimes crashes on the desktop
    # runner, so they run here only when the TUI itself changed.
    $onlyWhenChanged = @{ 'UniGetUI.Tui.Tests' = 'src/UniGetUI.Tui/' }
    $testProjects = @($projects.Values | Where-Object {
            $name = [System.IO.Path]::GetFileNameWithoutExtension($_.Path)
            $name -like '*.Tests' -and
            ($changedProjects.Contains($_.Path) -or @($_.References | Where-Object { $changedProjects.Contains($_) }).Count -gt 0) -and
            (-not $onlyWhenChanged.ContainsKey($name) -or @($changed | Where-Object { $_.StartsWith($onlyWhenChanged[$name]) }).Count -gt 0)
        } | ForEach-Object { $_.Path } | Sort-Object)
    if ($testProjects.Count -eq 0) {
        Add-Result 'tests' 'skipped' 'no test project covers the changed projects'
    }
    else {
        $testsStarted = Get-Date
        foreach ($testProject in $testProjects) {
            $name = [System.IO.Path]::GetFileNameWithoutExtension($testProject)
            if (((Get-Date) - $testsStarted).TotalSeconds -gt $TestBudgetSeconds) {
                Add-Result "tests: $name" 'skipped' "over the $TestBudgetSeconds s test budget; the pull request's CI runs it"
                continue
            }
            $started = Get-Date
            # Serial build, as in Start-UniGetUIDev.ps1 (CS2012), and results outside the
            # working tree so they stay out of the agent's patch.
            $exit = Invoke-Logged "test-$name.log" 'dotnet' @(
                'test', $testProject, '/p:Platform=x64', '/p:BuildInParallel=false', '-m:1', '--nologo', '-v', 'q',
                '--blame-hang-timeout', '5m', '--results-directory', (Join-Path $logDir "TestResults-$name")
            )
            $summary = (Select-String -LiteralPath (Join-Path $logDir "test-$name.log") -Pattern '(Passed|Failed)!.*' | ForEach-Object { $_.Matches[0].Value.Trim() }) -join '; '
            if ($exit -eq 0) { Add-Result "tests: $name" 'passed' $summary ((Get-Date) - $started).TotalSeconds }
            else { Add-Result "tests: $name" 'failed' "exit code $exit. $summary`n$(Get-LogTail "test-$name.log" 25)" ((Get-Date) - $started).TotalSeconds }
        }
    }

    # formatting, limited to the changed C# files
    if ($csFiles.Count -eq 0) {
        Add-Result 'formatting' 'skipped' 'no C# file changed'
    }
    else {
        $started = Get-Date
        $include = @('--include') + $csFiles
        $whitespace = Invoke-Logged 'format-whitespace.log' 'dotnet' (@('format', 'whitespace', 'src', '--folder', '--verify-no-changes', '--verbosity', 'minimal') + $include)
        $style = Invoke-Logged 'format-style.log' 'dotnet' (@('format', 'style', 'src/UniGetUI.Windows.slnx', '--no-restore', '--verify-no-changes', '--verbosity', 'minimal') + $include)
        $failures = @(
            if ($whitespace -ne 0) { "whitespace:`n$(Get-LogTail 'format-whitespace.log')" }
            if ($style -ne 0) { "style:`n$(Get-LogTail 'format-style.log')" }
        )
        if ($failures.Count -eq 0) { Add-Result 'formatting' 'passed' "$($csFiles.Count) C# file(s) already formatted" ((Get-Date) - $started).TotalSeconds }
        else { Add-Result 'formatting' 'failed' ($failures -join "`n") ((Get-Date) - $started).TotalSeconds }
    }

    # translations: keys missing from lang_en.json after the change that were not missing before
    if ($addedLines.Count -eq 0) {
        Add-Result 'translations' 'skipped' 'no C# or AXAML line added'
    }
    else {
        $sync = Join-Path $repoRoot 'scripts\translation\Sync-TranslationSources.ps1'
        $baseTree = Join-Path ([System.IO.Path]::GetTempPath()) "agent-base-$PID"
        & git worktree add --quiet --detach $baseTree $BaseSha
        try {
            # The script writes its inventory, then throws when the English file is out of sync,
            # which main already is; only the keys this change adds matter here.
            try { & $sync -RepositoryRoot $baseTree -CheckOnly -InventoryOutputPath (Join-Path $logDir 'translations-base.json') *> $null } catch { }
            try { & $sync -RepositoryRoot $repoRoot -CheckOnly -InventoryOutputPath (Join-Path $logDir 'translations-change.json') *> $null } catch { }
        }
        finally {
            & git worktree remove --force $baseTree
        }
        $before = @((Get-Content -LiteralPath (Join-Path $logDir 'translations-base.json') -Raw | ConvertFrom-Json).missingKeys)
        $after = @((Get-Content -LiteralPath (Join-Path $logDir 'translations-change.json') -Raw | ConvertFrom-Json).missingKeys)
        $new = @($after | Where-Object { $before -notcontains $_ })
        if ($new.Count -eq 0) { Add-Result 'translations' 'passed' 'every new translated string is in lang_en.json' }
        else {
            $list = ($new | ForEach-Object { '`' + $_ + '`' }) -join ', '
            Add-Result 'translations' 'failed' "missing from src/Languages/lang_en.json: $list"
        }
    }
}
finally {
    Pop-Location
}

$passed = @($results | Where-Object { $_.status -eq 'failed' }).Count -eq 0
[pscustomobject]@{ passed = $passed; checks = $results } | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $OutputDir 'checks.json') -Encoding utf8NoBOM

$icon = @{ passed = '✅'; failed = '❌'; skipped = '➖' }
$markdown = @(
    '| Check | Result | Details |'
    '| --- | --- | --- |'
    foreach ($result in $results) {
        $details = ($result.details -replace '\r?\n', '<br>' -replace '\|', '\|')
        if ($details.Length -gt 600) { $details = $details.Substring(0, 597) + '...' }
        "| $($result.name) | $($icon[$result.status]) $($result.status) | $details |"
    }
)
$markdown -join "`n" | Set-Content -LiteralPath (Join-Path $OutputDir 'checks.md') -Encoding utf8NoBOM

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    "checks-passed=$($passed.ToString().ToLowerInvariant())" | Add-Content -Path $env:GITHUB_OUTPUT -Encoding utf8
}
if (-not $passed) {
    Write-Host "::warning::Some checks of the agent's change failed; see checks.md"
}
