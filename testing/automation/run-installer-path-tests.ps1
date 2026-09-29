# Compiles but NEVER runs the production installer. Runs only the isolated HKCU test harness.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [string]$Compiler = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$installerExtras = Join-Path $repo 'InstallerExtras'
$run = Join-Path $ArtifactDirectory ("path-validation-" + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $run -Force
$testKey = "Software\Devolutions\UniGetUI\InstallerPathTests\" + [guid]::NewGuid().ToString('N')
$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
    [Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
$restrictedKeys = @()
$previousRoots = @()
# Keep AppId below Inno's 57-character CRC-shortening threshold so the seeded
# prior-choice key has exactly the same name that the fixture looks up.
$taskAppId = 'UniGetUI-PathTasks-' + [guid]::NewGuid().ToString('N')
$previousKey = "Software\Microsoft\Windows\CurrentVersion\Uninstall\${taskAppId}_is1"
function Restore-TestAcl($Restricted) {
    $acl = [Security.AccessControl.RegistrySecurity]::new()
    $acl.SetSecurityDescriptorSddlForm($Restricted.OriginalAcl, [Security.AccessControl.AccessControlSections]::Access)
    $Restricted.Key.SetAccessControl($acl)
}
try {
    & $Compiler /Q "/DTestRegistryKey=$testKey" "/DTestOutput=$run" (Join-Path $installerExtras 'AddToPath.Tests.iss')
    if ($LASTEXITCODE -ne 0) { throw "PATH harness compile failed ($LASTEXITCODE)" }
    $test = Start-Process (Join-Path $run 'PathTests.exe') -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', "/LOG=`"$run\PathTests.log`""
    ) -Wait -PassThru
    # InitializeSetup returns false intentionally; rely on the assertion report, not exit 1.
    $report = Get-Content (Join-Path $run 'PathTests.result.txt') -Raw
    if ($report -notmatch '^PASS: \d+ assertions$') { throw $report }
    Write-Host "$report (harness exit $($test.ExitCode), intentional cancellation)"
    foreach ($fixture in @(
        @{ Name = 'CheckSz'; Kind = [Microsoft.Win32.RegistryValueKind]::String },
        @{ Name = 'CheckExpandSz'; Kind = [Microsoft.Win32.RegistryValueKind]::ExpandString }
    )) {
        $key = $registry.OpenSubKey("$testKey\$($fixture.Name)")
        try {
            if ($key.GetValueKind('Path') -ne $fixture.Kind) { throw "Wrong type in $($fixture.Name)" }
            $raw = $key.GetValue('Path', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if ($raw -cne '%SystemRoot%; "C:\Keep\" ;;') { throw "Raw string changed in $($fixture.Name)" }
        } finally { $key.Dispose() }
    }
    Write-Host 'PASS: independent raw UTF-16/type verification of both registry kinds'

    # Apply denies only to newly created test keys. Keep FullControl handles open
    # so the original ACL can always be restored in finally, even if a test fails.
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    foreach ($fixture in @(
        @{ Name = 'ReadDenied'; Target = 'Environment'; Rights = [Security.AccessControl.RegistryRights]::QueryValues },
        @{ Name = 'WriteDenied'; Target = 'Environment'; Rights = [Security.AccessControl.RegistryRights]::SetValue },
        @{ Name = 'OwnerDenied'; Target = 'Owners'; Rights = [Security.AccessControl.RegistryRights]::SetValue }
    )) {
        $envKey = $registry.CreateSubKey("$testKey\$($fixture.Name)\Environment")
        try { $envKey.SetValue('Path', 'C:\User-owned original', [Microsoft.Win32.RegistryValueKind]::String) }
        finally { $envKey.Dispose() }
        $ownerKey = $registry.CreateSubKey("$testKey\$($fixture.Name)\Owners")
        $ownerKey.Dispose()
        $key = $registry.OpenSubKey("$testKey\$($fixture.Name)\$($fixture.Target)",
            [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree,
            [Security.AccessControl.RegistryRights]::FullControl)
        $originalAcl = $key.GetAccessControl().GetSecurityDescriptorSddlForm(
            [Security.AccessControl.AccessControlSections]::Access)
        $restrictedKeys += @{ Key = $key; OriginalAcl = $originalAcl }
        $acl = $key.GetAccessControl()
        $rule = [Security.AccessControl.RegistryAccessRule]::new(
            $sid, $fixture.Rights, [Security.AccessControl.AccessControlType]::Deny)
        $acl.AddAccessRule($rule)
        $key.SetAccessControl($acl)
    }
    $aclTest = Start-Process (Join-Path $run 'PathTests.exe') -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', "/LOG=`"$run\PathTests-acl.log`"", '/ACLTEST'
    ) -Wait -PassThru
    $report = Get-Content (Join-Path $run 'PathTests-acl.result.txt') -Raw
    if ($report -ne 'PASS: 3 assertions') { throw $report }
    foreach ($restricted in $restrictedKeys) { Restore-TestAcl $restricted }
    foreach ($name in @('ReadDenied', 'WriteDenied', 'OwnerDenied')) {
        $key = $registry.OpenSubKey("$testKey\$name\Environment")
        try {
            if ($key.GetValue('Path') -cne 'C:\User-owned original' -or
                $key.GetValueKind('Path') -ne [Microsoft.Win32.RegistryValueKind]::String) {
                throw "${name}: failed registry operation changed PATH"
            }
        } finally { $key.Dispose() }
    }
    Write-Host "PASS: 3 explicit access-denied failures; all original values/types intact (exit $($aclTest.ExitCode))"

    # Reuse the production task declarations and the exact alias startup helpers.
    # The fixture cancels on its tasks page, never installs or calls UpdateUnigetPath.
    $script = [IO.File]::ReadAllText((Join-Path $repo 'UniGetUI.iss'))
    $tasks = [regex]::Match($script, '(?ms)^\[Tasks\]\r?\n(?<tasks>.*?)(?=^\[)')
    if (-not $tasks.Success) { throw 'Cannot locate production task declarations' }
    $tasksFile = Join-Path $run 'Tasks-under-test.iss'
    [IO.File]::WriteAllText($tasksFile, "[Tasks]`r`n" + $tasks.Groups['tasks'].Value)
    & $Compiler /Q "/DTestRegistryKey=$testKey" "/DTestOutput=$run" "/DTestAppId=$taskAppId" `
        "/DTestTasksFile=$tasksFile" /FPathTaskTests (Join-Path $installerExtras 'AddToPath.Tests.iss')
    if ($LASTEXITCODE -ne 0) { throw "Task wiring harness compile failed ($LASTEXITCODE)" }
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry32, [Microsoft.Win32.RegistryView]::Registry64)) {
        $previousRoots += [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
    }
    $entry = 'C:\UniGetUI PATH Test'
    $ownerName = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::Unicode.GetBytes($entry.ToLowerInvariant()))).ToLowerInvariant()
    $cases = @(
        @{ Name = 'default-unchecked'; Args = @(); Path = 0; Before = 0 },
        @{ Name = 'alias-enable'; Args = @('ADDTOPATH=1'); Path = 1; Before = 0 },
        @{ Name = 'alias-disable'; Args = @('ADDTOPATH=0'); Path = 0; Before = 0 },
        @{ Name = 'case-insensitive'; Args = @('aDdToPaTh=1'); Path = 1; Before = 0 },
        @{ Name = 'same-value-repeated'; Args = @('ADDTOPATH=1', 'addtopath=1'); Path = 1; Before = 0 },
        @{ Name = 'same-zero-repeated'; Args = @('ADDTOPATH=0', 'addtopath=0'); Path = 0; Before = 0 },
        @{ Name = 'native-merge-still-works'; Args = @('/MERGETASKS="regularinstall\addtopath"'); Path = 1; Before = 1 },
        @{ Name = 'native-tasks-still-works'; Args = @('/TASKS="regularinstall,regularinstall\addtopath"'); Path = 1; Before = 1 },
        @{ Name = 'override-tasks-disable'; Args = @('/TASKS="regularinstall,regularinstall\startmenuicon,regularinstall\addtopath"', 'ADDTOPATH=0'); Path = 0; Before = 1 },
        @{ Name = 'override-tasks-enable'; Args = @('ADDTOPATH=1', '/TASKS="regularinstall,regularinstall\desktopicon"'); Path = 1; Before = 0 },
        @{ Name = 'override-merge-disable'; Args = @('/MERGETASKS="regularinstall\addtopath"', 'ADDTOPATH=0'); Path = 0; Before = 1 },
        @{ Name = 'override-merge-enable'; Args = @('/MERGETASKS="!regularinstall\addtopath,!regularinstall\desktopicon"', 'ADDTOPATH=1'); Path = 1; Before = 0 },
        @{ Name = 'portable-enable-ignored'; Args = @('/TASKS="portableinstall"', 'ADDTOPATH=1'); Path = 0; Before = 0; Portable = 1 },
        @{ Name = 'portable-disable-ignored'; Args = @('/TASKS="portableinstall"', 'ADDTOPATH=0'); Path = 0; Before = 0; Portable = 1 },
        @{ Name = 'portable-owned-untouched'; Args = @('/TASKS="portableinstall"', 'ADDTOPATH=1'); Path = 0; Before = 0; Portable = 1; Seed = 'owned' },
        @{ Name = 'unrequested-slash-not-supported'; Args = @('/ADDTOPATH=1'); Path = 0; Before = 0 },
        @{ Name = 'previous-on-retained'; Args = @(); Path = 1; Before = 1; Previous = 'on'; Seed = 'owned' },
        @{ Name = 'previous-on-alias-off'; Args = @('ADDTOPATH=0'); Path = 0; Before = 1; Previous = 'on'; Seed = 'owned' },
        @{ Name = 'previous-off-alias-on'; Args = @('ADDTOPATH=1'); Path = 1; Before = 0; Previous = 'off' },
        @{ Name = 'preexisting-enable-unowned'; Args = @('ADDTOPATH=1'); Path = 1; Before = 0; Seed = 'preexisting' },
        @{ Name = 'preexisting-disable-preserved'; Args = @('/MERGETASKS="regularinstall\addtopath"', 'ADDTOPATH=0'); Path = 0; Before = 1; Seed = 'preexisting' },
        @{ Name = 'invalid-missing-equals'; Args = @('ADDTOPATH'); Invalid = 'Invalid argument' },
        @{ Name = 'invalid-empty'; Args = @('ADDTOPATH='); Invalid = 'Invalid argument' },
        @{ Name = 'invalid-number'; Args = @('ADDTOPATH=2'); Invalid = 'Invalid argument' },
        @{ Name = 'invalid-word'; Args = @('ADDTOPATH=true'); Invalid = 'Invalid argument' },
        @{ Name = 'invalid-leading-zero'; Args = @('ADDTOPATH=01'); Invalid = 'Invalid argument' },
        @{ Name = 'invalid-whitespace'; Args = @('"ADDTOPATH= 1"'); Invalid = 'Invalid argument' },
        @{ Name = 'invalid-name-whitespace'; Args = @('"ADDTOPATH =1"'); Invalid = 'Invalid argument' },
        @{ Name = 'invalid-trailing-whitespace'; Args = @('"ADDTOPATH=1 "'); Invalid = 'Invalid argument' },
        @{ Name = 'invalid-extra-equals'; Args = @('ADDTOPATH=1=0'); Invalid = 'Invalid argument' },
        @{ Name = 'conflicting-on-off'; Args = @('ADDTOPATH=1', 'addtopath=0'); Invalid = 'Conflicting ADDTOPATH' },
        @{ Name = 'conflicting-off-on'; Args = @('ADDTOPATH=0', 'ADDTOPATH=1'); Invalid = 'Conflicting ADDTOPATH' },
        @{ Name = 'invalid-portable-owned'; Args = @('/TASKS="portableinstall"', 'ADDTOPATH=2'); Seed = 'owned'; Invalid = 'Invalid argument' }
    )
    foreach ($case in $cases) {
        foreach ($root in $previousRoots) {
            $root.DeleteSubKeyTree($previousKey, $false)
            if ($case.Previous) {
                $key = $root.CreateSubKey($previousKey)
                try {
                    $selected = 'regularinstall,regularinstall\startmenuicon'
                    $deselected = 'portableinstall,regularinstall\desktopicon'
                    if ($case.Previous -eq 'on') { $selected += ',regularinstall\addtopath' }
                    else { $deselected += ',regularinstall\addtopath' }
                    $key.SetValue('Inno Setup: Selected Tasks', $selected)
                    $key.SetValue('Inno Setup: Deselected Tasks', $deselected)
                } finally { $key.Dispose() }
            }
        }
        $initial = 'C:\TaskKeep'
        if ($case.Seed) { $initial += ";$entry" }
        $key = $registry.CreateSubKey("$testKey\TaskEnvironment")
        try { $key.SetValue('Path', $initial, [Microsoft.Win32.RegistryValueKind]::String) }
        finally { $key.Dispose() }
        $registry.DeleteSubKeyTree("$testKey\TaskOwners", $false)
        if ($case.Seed -eq 'owned') {
            $key = $registry.CreateSubKey("$testKey\TaskOwners")
            try { $key.SetValue($ownerName, "O$entry", [Microsoft.Win32.RegistryValueKind]::String) }
            finally { $key.Dispose() }
        }
        $reportFile = Join-Path $run 'PathTaskTests.result.txt'
        if (Test-Path $reportFile) { Remove-Item $reportFile }
        $logFile = Join-Path $run ("task-" + $case.Name + '.log')
        $taskTest = Start-Process (Join-Path $run 'PathTaskTests.exe') -ArgumentList (
            @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', "/LOG=`"$logFile`"") + $case.Args
        ) -Wait -PassThru
        $report = Get-Content $reportFile -Raw
        Copy-Item $reportFile (Join-Path $run ("task-" + $case.Name + '.result.txt'))
        if ($taskTest.ExitCode -eq 0) { throw "Task harness unexpectedly completed installation: $($case.Name)" }
        if ($case.Invalid) {
            if ($taskTest.ExitCode -ne 1 -or $report -ne 'REJECTED: invalid ADDTOPATH' -or
                (Get-Content $logFile -Raw) -notmatch ('ERROR: ' + $case.Invalid)) {
                throw "Invalid property not rejected/logged: $($case.Name): $report"
            }
            $expected = $initial
            $owned = $case.Seed -eq 'owned'
        } else {
            if ($report -notmatch '^PASS: task wiring') { throw "$($case.Name): $report" }
            $state = ($report -split '\r?\n' | Select-Object -Skip 1) -join "`n" | ConvertFrom-StringData
            $portable = [int]$case.Portable
            $alias = [int](@($case.Args | Where-Object { $_ -match '^ADDTOPATH($|=)' }).Count -gt 0)
            foreach ($check in @(
                @{ Name = 'Path'; Value = $case.Path }, @{ Name = 'BeforePath'; Value = $case.Before },
                @{ Name = 'Portable'; Value = $portable }, @{ Name = 'Regular'; Value = 1 - $portable },
                @{ Name = 'Alias'; Value = $alias }
            )) {
                if ([int]$state[$check.Name] -ne $check.Value) {
                    throw "$($case.Name): unexpected $($check.Name): $report"
                }
            }
            if ($case.Previous -and ($state.StartMenu -ne '1' -or $state.Desktop -ne '0')) {
                throw "Previous unrelated tasks were not restored: $($case.Name)"
            }
            $expected = $initial
            $owned = $case.Seed -eq 'owned'
            if (-not $portable) {
                if ($case.Path) {
                    if (-not $case.Seed) { $expected += ";$entry"; $owned = $true }
                } else {
                    if ($owned) { $expected = 'C:\TaskKeep' }
                    $owned = $false
                }
            }
        }
        $key = $registry.OpenSubKey("$testKey\TaskEnvironment")
        try {
            if ($key.GetValue('Path') -cne $expected -or $key.GetValueKind('Path') -ne [Microsoft.Win32.RegistryValueKind]::String) {
                throw "Isolated task PATH value/type mismatch: $($case.Name)"
            }
        } finally { $key.Dispose() }
        $key = $registry.OpenSubKey("$testKey\TaskOwners")
        try {
            $record = if ($null -ne $key) { $key.GetValue($ownerName) } else { $null }
            if (($owned -and $record -cne "O$entry") -or (-not $owned -and $null -ne $record)) {
                throw "Isolated task ownership mismatch: $($case.Name)"
            }
        } finally { if ($null -ne $key) { $key.Dispose() } }
        Write-Host "PASS: command-line/task wiring $($case.Name)"
    }

    # Compile a COPY of the full installer with tiny dummy payloads and signing disabled.
    # This validates task/custom-message/event integration without secrets or production output.
    $fixtureRoot = Join-Path $run 'installer-fixture'
    $extras = Join-Path $fixtureRoot 'InstallerExtras'
    $payload = Join-Path $fixtureRoot 'unigetui_bin'
    $icons = Join-Path $fixtureRoot 'src\SharedAssets\Assets\Images'
    $null = New-Item -ItemType Directory -Path $extras, $payload, $icons -Force
    foreach ($pattern in @('*.iss', '*.png', 'ForceUniGetUIPortable')) {
        Get-ChildItem (Join-Path $installerExtras $pattern) -File | Copy-Item -Destination $extras
    }
    Copy-Item (Join-Path $repo 'src\SharedAssets\Assets\Images\icon.ico') $icons
    foreach ($name in @('UniGetUI.exe', 'uniget.exe', 'IntegrityTree.json')) {
        [IO.File]::WriteAllText((Join-Path $payload $name), 'isolated compiler fixture, not executable')
    }
    [IO.File]::WriteAllText((Join-Path $extras 'netcorecheck_x64.exe'), 'isolated compiler fixture')
    $script = [IO.File]::ReadAllText((Join-Path $repo 'UniGetUI.iss'))
    $script = $script -replace '(?m)^SignTool=azsign\r?$', '; Signing disabled in isolated compiler fixture'
    $script = $script -replace '(?m)^SignedUninstaller=yes\r?$', 'SignedUninstaller=no'
    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'UniGetUI.iss'), $script)
    & $Compiler /Q "/O$run" '/FUniGetUI-unsigned-isolated' '/DInstallerCompression=none' (Join-Path $fixtureRoot 'UniGetUI.iss')
    if ($LASTEXITCODE -ne 0) { throw "Isolated full installer compile failed ($LASTEXITCODE)" }
    Write-Host "PASS: unsigned full installer compiled, NOT executed: $run\UniGetUI-unsigned-isolated.exe"
    Write-Host "Artifacts: $run"
} finally {
    foreach ($root in $previousRoots) {
        try { $root.DeleteSubKeyTree($previousKey, $false) }
        finally { $root.Dispose() }
    }
    foreach ($restricted in $restrictedKeys) {
        try { Restore-TestAcl $restricted }
        finally { $restricted.Key.Dispose() }
    }
    $registry.DeleteSubKeyTree($testKey, $false)
    $registry.Dispose()
}
