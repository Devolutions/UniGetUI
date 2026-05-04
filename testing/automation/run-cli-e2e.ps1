$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$runningOnWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows
)

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$srcRoot = Join-Path $repoRoot 'src'
$configuration = if ($env:CONFIGURATION) { $env:CONFIGURATION } else { 'Release' }
$defaultManifest = if ($runningOnWindows) {
    Join-Path $PSScriptRoot 'cli-e2e.manifest.windows.json'
}
else {
    Join-Path $PSScriptRoot 'cli-e2e.manifest.linux.json'
}
$manifestPath = if ($env:UNIGETUI_CLI_E2E_MANIFEST) {
    $env:UNIGETUI_CLI_E2E_MANIFEST
}
else {
    $defaultManifest
}

if (-not (Test-Path $manifestPath)) {
    throw "CLI E2E manifest not found at $manifestPath"
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json -AsHashtable
if ($null -eq $manifest) {
    throw "Could not parse CLI E2E manifest at $manifestPath"
}

$artifactRoot = if ($env:UNIGETUI_CLI_E2E_ARTIFACTS) {
    $env:UNIGETUI_CLI_E2E_ARTIFACTS
}
else {
    Join-Path ([System.IO.Path]::GetTempPath()) ("unigetui-cli-e2e-" + [Guid]::NewGuid().ToString('N'))
}
if (Test-Path $artifactRoot) {
    Remove-Item -Recurse -Force $artifactRoot
}
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$artifactRoot = (Resolve-Path $artifactRoot).Path

$daemonRoot = $artifactRoot
$downloadRoot = Join-Path $daemonRoot 'downloads'
$coveragePath = Join-Path $daemonRoot 'coverage.json'
$inventoryPath = Join-Path $daemonRoot 'environment.json'
$daemonStdOutLog = Join-Path $daemonRoot 'headless-daemon.stdout.log'
$daemonStdErrLog = Join-Path $daemonRoot 'headless-daemon.stderr.log'
$preserveArtifacts = $true

$localDataRoot = if ($runningOnWindows) {
    Join-Path $daemonRoot 'AppData\Local'
}
else {
    Join-Path $daemonRoot '.local\share'
}
$roamingDataRoot = if ($runningOnWindows) {
    Join-Path $daemonRoot 'AppData\Roaming'
}
else {
    $null
}
$dotnetHomeRoot = Join-Path $daemonRoot '.dotnet-home'
$npmGlobalRoot = Join-Path $daemonRoot 'npm-global'

New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
New-Item -ItemType Directory -Path $localDataRoot -Force | Out-Null
New-Item -ItemType Directory -Path $dotnetHomeRoot -Force | Out-Null
New-Item -ItemType Directory -Path $npmGlobalRoot -Force | Out-Null
if (-not [string]::IsNullOrWhiteSpace($roamingDataRoot)) {
    New-Item -ItemType Directory -Path $roamingDataRoot -Force | Out-Null
}

$env:DOTNET_CLI_HOME = $dotnetHomeRoot
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:UNIGETUI_GITHUB_TOKEN_NAMESPACE = "cli-e2e-$([Guid]::NewGuid().ToString('N'))"
$env:UNIGETUI_AVALONIA_DEVTOOLS = 'disabled'
$env:npm_config_prefix = $npmGlobalRoot

if (-not $runningOnWindows) {
    $env:HOME = $daemonRoot
    $env:USERPROFILE = $daemonRoot
    $env:XDG_DATA_HOME = $localDataRoot
}

$coverage = [ordered]@{
    manifest = $manifest.name
    tested = @()
    excluded = @($manifest.excludedCommands)
}

function Add-Tested {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Command,
        [string] $Mode = 'success'
    )

    $script:coverage.tested += [ordered]@{
        command = $Command
        mode = $Mode
    }
}

function Write-Stage {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    Write-Host "== $Name =="
}

function Find-BuiltArtifact {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ProjectDirectory,
        [Parameter(Mandatory = $true)]
        [string] $FileName
    )

    $outputRoot = Join-Path $ProjectDirectory 'bin'
    if (-not (Test-Path $outputRoot)) {
        return $null
    }

    return Get-ChildItem -Path $outputRoot -Recurse -File -Filter $FileName |
        Sort-Object @{
            Expression = { if ($_.FullName -like "*\bin\*\$configuration\*") { 0 } else { 1 } }
        }, @{
            Expression = { $_.FullName }
        } |
        Select-Object -First 1 -ExpandProperty FullName
}

function Get-ManifestManagerByRole {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Role
    )

    $matches = @($manifest.packageManagers | Where-Object { @($_.roles) -contains $Role })
    if ($matches.Count -eq 0) {
        return $null
    }

    return $matches[0]
}

function Get-PackageArguments {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable] $Fixture,
        [switch] $IncludeVersion,
        [switch] $IncludeSource
    )

    $arguments = @(
        '--manager', [string]$Fixture.manager,
        '--id', [string]$Fixture.packageId
    )
    if ($IncludeVersion -and $Fixture.ContainsKey('installVersion')) {
        $arguments += @('--version', [string]$Fixture.installVersion)
    }
    if ($IncludeSource -and $Fixture.ContainsKey('sourceName')) {
        $arguments += @('--source', [string]$Fixture.sourceName)
    }
    if ($Fixture.ContainsKey('scope')) {
        $arguments += @('--scope', [string]$Fixture.scope)
    }
    return $arguments
}

function Find-PackageMatch {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Packages,
        [Parameter(Mandatory = $true)]
        [hashtable] $Fixture,
        [string] $Version
    )

    return @($Packages | Where-Object {
            $_.id -eq $Fixture.packageId -and (
                -not $PSBoundParameters.ContainsKey('Version') -or $_.version -eq $Version
            )
        })[0]
}

function Resolve-QueueOutputPath {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable] $QueueFixture
    )

    $safeManager = ([string]$QueueFixture.manager) -replace '[^A-Za-z0-9._-]', '_'
    $safePackage = ([string]$QueueFixture.packageId) -replace '[^A-Za-z0-9._-]', '_'
    $targetDirectory = Join-Path $downloadRoot "$safeManager-$safePackage"
    New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
    return $targetDirectory
}

function Get-DaemonCommand {
    $daemonProject = Join-Path $srcRoot ([string]$manifest.daemon.project)
    if (-not (Test-Path $daemonProject)) {
        throw "Daemon project not found at $daemonProject"
    }

    switch ([string]$manifest.daemon.kind) {
        'winui-exe' {
            $daemonExe = if ($env:UNIGETUI_DAEMON_EXE) {
                $env:UNIGETUI_DAEMON_EXE
            }
            else {
                Find-BuiltArtifact -ProjectDirectory (Split-Path $daemonProject -Parent) -FileName "$($manifest.daemon.assemblyName).exe"
            }
            if ([string]::IsNullOrWhiteSpace($daemonExe) -or -not (Test-Path $daemonExe)) {
                throw "WinUI headless executable was not found. Expected $($manifest.daemon.assemblyName).exe under $(Split-Path $daemonProject -Parent)\bin\$configuration"
            }

            return @{
                FilePath = (Resolve-Path $daemonExe).Path
                WorkingDirectory = Split-Path (Resolve-Path $daemonExe).Path -Parent
            }
        }
        'avalonia-dll' {
            $daemonDll = if ($env:UNIGETUI_DAEMON_DLL) {
                $env:UNIGETUI_DAEMON_DLL
            }
            else {
                Find-BuiltArtifact -ProjectDirectory (Split-Path $daemonProject -Parent) -FileName "$($manifest.daemon.assemblyName).dll"
            }
            if ([string]::IsNullOrWhiteSpace($daemonDll) -or -not (Test-Path $daemonDll)) {
                throw "Avalonia headless daemon DLL was not found. Expected $($manifest.daemon.assemblyName).dll under $(Split-Path $daemonProject -Parent)\bin\$configuration"
            }

            $resolvedDll = (Resolve-Path $daemonDll).Path
            return @{
                FilePath = 'dotnet'
                WorkingDirectory = Split-Path $resolvedDll -Parent
                PrefixArguments = @($resolvedDll)
            }
        }
        default {
            throw "Unsupported daemon kind $($manifest.daemon.kind)"
        }
    }
}

$pipeName = "UniGetUI.CI.$([Guid]::NewGuid().ToString('N'))"
$transportArgs = @('--transport', 'named-pipe', '--pipe-name', $pipeName)
$daemonExtraArgs = @('--headless', '--ipc-api-transport', 'named-pipe', '--ipc-api-pipe-name', $pipeName)
$daemonCommand = Get-DaemonCommand
$cliCommand = $daemonCommand
$process = $null
$gracefulShutdown = $false

function Get-DaemonLog {
    $stdout = if (Test-Path $daemonStdOutLog) { Get-Content $daemonStdOutLog -Raw } else { '' }
    $stderr = if (Test-Path $daemonStdErrLog) { Get-Content $daemonStdErrLog -Raw } else { '' }
    return ($stdout, $stderr -join [Environment]::NewLine).Trim()
}

function Stop-Daemon {
    if ($null -ne $script:process -and -not $script:process.HasExited) {
        Stop-Process -Id $script:process.Id
    }
}

function Invoke-CliRaw {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    $commandArguments = @()
    if ($cliCommand.ContainsKey('PrefixArguments')) {
        $commandArguments += $cliCommand.PrefixArguments
    }
    $commandArguments += $transportArgs + $Arguments
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $cliCommand.FilePath
    $startInfo.WorkingDirectory = $cliCommand.WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $commandArguments) {
        [void]$startInfo.ArgumentList.Add([string]$argument)
    }

    $commandProcess = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $commandProcess) {
        throw "Failed to start CLI command: $($cliCommand.FilePath) $($commandArguments -join ' ')"
    }

    $stdout = $commandProcess.StandardOutput.ReadToEnd()
    $stderr = $commandProcess.StandardError.ReadToEnd()
    $commandProcess.WaitForExit()
    $text = (@($stdout, $stderr) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
            $_.TrimEnd()
        }) -join [Environment]::NewLine
    return @{
        ExitCode = $commandProcess.ExitCode
        Text = $text
    }
}

function Invoke-CliJson {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    $result = Invoke-CliRaw -Arguments $Arguments
    if ($result.ExitCode -ne 0) {
        throw "CLI command failed ($($result.ExitCode)): $($Arguments -join ' ')`n$($result.Text)"
    }
    if ([string]::IsNullOrWhiteSpace($result.Text)) {
        throw "CLI command returned empty output: $($Arguments -join ' ')"
    }
    return $result.Text | ConvertFrom-Json
}

function Invoke-CliFailure {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    $result = Invoke-CliRaw -Arguments $Arguments
    if ($result.ExitCode -eq 0) {
        throw "CLI command unexpectedly succeeded: $($Arguments -join ' ')`n$($result.Text)"
    }
    return $result
}

function Wait-ForCliCondition {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,
        [Parameter(Mandatory = $true)]
        [scriptblock] $Condition,
        [Parameter(Mandatory = $true)]
        [string] $FailureMessage,
        [int] $TimeoutSeconds = 120,
        [int] $DelaySeconds = 2
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastResponse = $null
    $lastError = $null

    do {
        try {
            $lastResponse = Invoke-CliJson -Arguments $Arguments
            $lastError = $null
            if (& $Condition $lastResponse) {
                return $lastResponse
            }
        }
        catch {
            $lastError = $_.Exception.Message
        }

        Start-Sleep -Seconds $DelaySeconds
    } while ((Get-Date) -lt $deadline)

    if ($null -ne $lastResponse) {
        throw "$FailureMessage`nLast payload: $($lastResponse | ConvertTo-Json -Depth 10)"
    }

    throw "$FailureMessage`nLast error: $lastError"
}

function Wait-ForInstalledPackage {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable] $Fixture,
        [string] $ExpectedVersion,
        [int] $TimeoutSeconds = 180
    )

    return Wait-ForCliCondition `
        -Arguments @('package', 'installed', '--manager', [string]$Fixture.manager) `
        -Condition {
            param($response)
            @($response.packages | Where-Object {
                    $_.id -eq $Fixture.packageId -and (
                        [string]::IsNullOrWhiteSpace($ExpectedVersion) -or $_.version -eq $ExpectedVersion
                    )
                }).Count -gt 0
        } `
        -FailureMessage "package installed did not report $($Fixture.packageId) for manager $($Fixture.manager)" `
        -TimeoutSeconds $TimeoutSeconds `
        -DelaySeconds 3
}

function Wait-ForPackageRemoval {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable] $Fixture,
        [int] $TimeoutSeconds = 180
    )

    return Wait-ForCliCondition `
        -Arguments @('package', 'installed', '--manager', [string]$Fixture.manager) `
        -Condition {
            param($response)
            @($response.packages | Where-Object { $_.id -eq $Fixture.packageId }).Count -eq 0
        } `
        -FailureMessage "$($Fixture.packageId) still appears in package installed for manager $($Fixture.manager)" `
        -TimeoutSeconds $TimeoutSeconds `
        -DelaySeconds 3
}

function Wait-ForPackageUpdateVisibility {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable] $Fixture,
        [int] $TimeoutSeconds = 120
    )

    return Wait-ForCliCondition `
        -Arguments @('package', 'updates', '--manager', [string]$Fixture.manager) `
        -Condition {
            param($response)
            @($response.updates | Where-Object { $_.id -eq $Fixture.packageId }).Count -gt 0
        } `
        -FailureMessage "package updates did not report $($Fixture.packageId) for manager $($Fixture.manager)" `
        -TimeoutSeconds $TimeoutSeconds `
        -DelaySeconds 3
}

function Get-LatestFixtureVersion {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable] $Fixture,
        [Parameter(Mandatory = $true)]
        [object] $SearchResponse,
        [Parameter(Mandatory = $true)]
        [object] $VersionResponse
    )

    $searchMatch = @($SearchResponse.packages | Where-Object { $_.id -eq $Fixture.packageId })[0]
    if ($null -eq $searchMatch) {
        throw "package search did not return $($Fixture.packageId) for manager $($Fixture.manager)"
    }

    $candidateVersions = @()
    if (-not [string]::IsNullOrWhiteSpace($searchMatch.version)) {
        $candidateVersions += [string]$searchMatch.version
    }
    $candidateVersions += @($VersionResponse.versions)

    $latestVersion = @($candidateVersions | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne $Fixture.installVersion
        })[0]

    if ([string]::IsNullOrWhiteSpace($latestVersion)) {
        throw "Could not resolve a newer version for $($Fixture.packageId) on manager $($Fixture.manager)"
    }

    return [string]$latestVersion
}

function Assert-JsonCommandSucceeded {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Response,
        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    if ($Response.status -ne 'success') {
        throw "$Label failed: $($Response | ConvertTo-Json -Depth 10)"
    }
}

function Write-EnvironmentInventory {
    $inventory = [ordered]@{
        manifest = $manifest.name
        os = if ($runningOnWindows) { 'windows' } else { 'linux' }
        dotnet = (& dotnet --version)
        python = ''
        pip = ''
        npm = ''
    }

    try {
        $inventory.python = (& python --version 2>&1 | Out-String).Trim()
    }
    catch {
        $inventory.python = $_.Exception.Message
    }

    try {
        $inventory.pip = (& python -m pip --version 2>&1 | Out-String).Trim()
    }
    catch {
        $inventory.pip = $_.Exception.Message
    }

    try {
        $inventory.npm = (& npm --version 2>&1 | Out-String).Trim()
    }
    catch {
        $inventory.npm = $_.Exception.Message
    }

    Set-Content -Path $inventoryPath -Value ($inventory | ConvertTo-Json -Depth 8) -Encoding UTF8
}

Write-EnvironmentInventory

try {
    $daemonArguments = @()
    if ($daemonCommand.ContainsKey('PrefixArguments')) {
        $daemonArguments += $daemonCommand.PrefixArguments
    }
    $daemonArguments += $daemonExtraArgs

    $process = Start-Process `
        -FilePath $daemonCommand.FilePath `
        -ArgumentList $daemonArguments `
        -WorkingDirectory $daemonCommand.WorkingDirectory `
        -RedirectStandardOutput $daemonStdOutLog `
        -RedirectStandardError $daemonStdErrLog `
        -PassThru

    $status = Wait-ForCliCondition `
        -Arguments @('status') `
        -Condition { param($response) $response.running -and $response.transport -eq 'named-pipe' } `
        -FailureMessage 'Headless daemon never became ready over named-pipe IPC.' `
        -TimeoutSeconds 120 `
        -DelaySeconds 2

    Write-Stage 'Status and headless transport'
    if ($status.namedPipeName -ne $pipeName) {
        throw "status did not report the expected named pipe name. Expected $pipeName, got $($status.namedPipeName)"
    }
    if (-not $runningOnWindows) {
        $expectedSocketPath = "/tmp/$pipeName"
        if ($status.namedPipePath -ne $expectedSocketPath) {
            throw "status did not report the expected Unix socket path. Expected $expectedSocketPath, got $($status.namedPipePath)"
        }
    }
    Add-Tested 'status'

    $version = Invoke-CliJson -Arguments @('version')
    if ($version.build -le 0) {
        throw "version did not return a positive build number"
    }
    Add-Tested 'version'

    $appState = Invoke-CliJson -Arguments @('app', 'status')
    if (-not $appState.app.headless -or $appState.app.windowAvailable -or $appState.app.canNavigate -or -not $appState.app.canQuit) {
        throw "app status did not report the expected headless state: $($appState | ConvertTo-Json -Depth 8)"
    }
    Add-Tested 'app status'

    Invoke-CliFailure -Arguments @('app', 'show') | Out-Null
    Add-Tested 'app show' 'expected-failure'

    Invoke-CliFailure -Arguments @('app', 'navigate', '--page', 'settings') | Out-Null
    Add-Tested 'app navigate' 'expected-failure'

    $bundleFixture = Get-ManifestManagerByRole -Role 'bundle'
    Invoke-CliFailure -Arguments @('package', 'show', '--id', [string]$bundleFixture.packageId, '--source', [string]$bundleFixture.sourceName) | Out-Null
    Add-Tested 'package show' 'expected-failure'

    if ($runningOnWindows -and $manifest.transport.verifyNoTcpListener) {
        $connections = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -eq $process.Id }
        if (@($connections).Count -ne 0) {
            throw "Headless named-pipe session opened a TCP listener unexpectedly: $(@($connections | Select-Object -ExpandProperty LocalPort) -join ', ')"
        }
    }

    Write-Stage 'Manager inspection'
    $managers = Invoke-CliJson -Arguments @('manager', 'list')
    foreach ($fixture in @($manifest.packageManagers)) {
        if (@($managers.managers | Where-Object { $_.name -eq $fixture.manager }).Count -eq 0) {
            throw "manager list did not report $($fixture.manager)"
        }
    }
    Add-Tested 'manager list'

    foreach ($fixture in @($manifest.packageManagers)) {
        $maintenance = Invoke-CliJson -Arguments @('manager', 'maintenance', '--manager', [string]$fixture.manager)
        if ($maintenance.maintenance.manager -ne $fixture.manager) {
            throw "manager maintenance did not return the expected manager payload for $($fixture.manager)"
        }
        if (@($maintenance.maintenance.supportedActions | Where-Object { $_ -eq 'reload' }).Count -eq 0) {
            throw "manager maintenance did not expose reload for $($fixture.manager)"
        }

        $reloadManager = Invoke-CliJson -Arguments @('manager', 'reload', '--manager', [string]$fixture.manager)
        if ($reloadManager.operationStatus -ne 'completed') {
            throw "manager reload did not complete successfully for $($fixture.manager): $($reloadManager | ConvertTo-Json -Depth 8)"
        }
    }
    Add-Tested 'manager maintenance'
    Add-Tested 'manager reload'

    $toggleManagerFixture = Get-ManifestManagerByRole -Role 'toggle-manager'
    $disableManager = Invoke-CliJson -Arguments @('manager', 'disable', '--manager', [string]$toggleManagerFixture.manager)
    if ($disableManager.manager.enabled) {
        throw "manager disable did not disable $($toggleManagerFixture.manager)"
    }
    $enableManager = Invoke-CliJson -Arguments @('manager', 'enable', '--manager', [string]$toggleManagerFixture.manager)
    if (-not $enableManager.manager.enabled) {
        throw "manager enable did not re-enable $($toggleManagerFixture.manager)"
    }
    Add-Tested 'manager enable'
    Add-Tested 'manager disable'

    $disableNotifications = Invoke-CliJson -Arguments @('manager', 'notifications', 'disable', '--manager', [string]$bundleFixture.manager)
    if (-not $disableNotifications.manager.notificationsSuppressed) {
        throw "manager notifications disable did not suppress notifications for $($bundleFixture.manager)"
    }
    $enableNotifications = Invoke-CliJson -Arguments @('manager', 'notifications', 'enable', '--manager', [string]$bundleFixture.manager)
    if ($enableNotifications.manager.notificationsSuppressed) {
        throw "manager notifications enable did not re-enable notifications for $($bundleFixture.manager)"
    }
    Add-Tested 'manager notifications disable'
    Add-Tested 'manager notifications enable'

    foreach ($fixture in @($manifest.packageManagers)) {
        $sources = Invoke-CliJson -Arguments @('source', 'list', '--manager', [string]$fixture.manager)
        if (@($sources.sources | Where-Object { $_.name -eq $fixture.sourceName }).Count -eq 0) {
            throw "source list did not report $($fixture.sourceName) for $($fixture.manager)"
        }
    }
    Add-Tested 'source list'

    $sourceFixture = Get-ManifestManagerByRole -Role 'source'
    if ($null -ne $sourceFixture) {
        Write-Stage 'Source add/remove'
        $sourceDirectory = Join-Path $daemonRoot 'LocalFeed'
        New-Item -ItemType Directory -Path $sourceDirectory -Force | Out-Null
        $sourceUri = ([System.Uri](Resolve-Path $sourceDirectory).Path).AbsoluteUri
        $customSourceName = 'ci-local-feed'
        $addedSource = Invoke-CliJson -Arguments @(
            'source', 'add',
            '--manager', [string]$sourceFixture.manager,
            '--name', $customSourceName,
            '--url', $sourceUri
        )
        Assert-JsonCommandSucceeded -Response $addedSource -Label 'source add'
        $sourcesAfterAdd = Invoke-CliJson -Arguments @('source', 'list', '--manager', [string]$sourceFixture.manager)
        if (@($sourcesAfterAdd.sources | Where-Object { $_.name -eq $customSourceName -and $_.url -eq $sourceUri }).Count -eq 0) {
            throw "source add did not expose the configured custom source"
        }
        $removedSource = Invoke-CliJson -Arguments @(
            'source', 'remove',
            '--manager', [string]$sourceFixture.manager,
            '--name', $customSourceName,
            '--url', $sourceUri
        )
        Assert-JsonCommandSucceeded -Response $removedSource -Label 'source remove'
        $sourcesAfterRemove = Invoke-CliJson -Arguments @('source', 'list', '--manager', [string]$sourceFixture.manager)
        if (@($sourcesAfterRemove.sources | Where-Object { $_.name -eq $customSourceName }).Count -ne 0) {
            throw "source remove did not remove the configured custom source"
        }
        Add-Tested 'source add'
        Add-Tested 'source remove'
    }

    Write-Stage 'Settings and secure settings'
    $settings = Invoke-CliJson -Arguments @('settings', 'list')
    if (@($settings.settings | Where-Object { $_.name -eq 'FreshValue' }).Count -eq 0) {
        throw "settings list did not report FreshValue"
    }
    Add-Tested 'settings list'

    $setFreshValue = Invoke-CliJson -Arguments @('settings', 'set', '--key', 'FreshValue', '--value', 'cli-smoke')
    if ($setFreshValue.setting.stringValue -ne 'cli-smoke') {
        throw "settings set did not persist FreshValue"
    }
    $getFreshValue = Invoke-CliJson -Arguments @('settings', 'get', '--key', 'FreshValue')
    if ($getFreshValue.setting.stringValue -ne 'cli-smoke') {
        throw "settings get did not return FreshValue"
    }
    Add-Tested 'settings set'
    Add-Tested 'settings get'

    $setFreshBool = Invoke-CliJson -Arguments @('settings', 'set', '--key', 'FreshBoolSetting', '--enabled', 'true')
    if (-not $setFreshBool.setting.boolValue) {
        throw "settings set did not enable FreshBoolSetting"
    }

    $secureList = Invoke-CliJson -Arguments @('settings', 'secure', 'list')
    if (@($secureList.settings | Where-Object { $_.key -eq $manifest.secureSettings.toggleKey }).Count -eq 0) {
        throw "settings secure list did not report $($manifest.secureSettings.toggleKey)"
    }
    $secureGet = Invoke-CliJson -Arguments @('settings', 'secure', 'get', '--key', [string]$manifest.secureSettings.toggleKey)
    if ($secureGet.setting.key -ne $manifest.secureSettings.toggleKey) {
        throw "settings secure get did not return the requested key"
    }
    Add-Tested 'settings secure list'
    Add-Tested 'settings secure get'

    if ($manifest.secureSettings.allowSet) {
        $secureSetOn = Invoke-CliJson -Arguments @(
            'settings', 'secure', 'set',
            '--key', [string]$manifest.secureSettings.toggleKey,
            '--enabled', 'true'
        )
        if (-not $secureSetOn.setting.enabled) {
            throw "settings secure set did not enable $($manifest.secureSettings.toggleKey)"
        }
        Add-Tested 'settings secure set'

        $maintenanceWithCustomPaths = Invoke-CliJson -Arguments @('manager', 'maintenance', '--manager', [string]$manifest.secureSettings.managerForExecutableOverride)
        if (-not $maintenanceWithCustomPaths.maintenance.customExecutablePathsAllowed) {
            throw "manager maintenance did not reflect enabled custom executable paths"
        }

        $setExecutable = Invoke-CliJson -Arguments @(
            'manager', 'set-executable',
            '--manager', [string]$manifest.secureSettings.managerForExecutableOverride,
            '--path', [string]$maintenanceWithCustomPaths.maintenance.effectiveExecutablePath
        )
        if ($setExecutable.maintenance.configuredExecutablePath -ne $maintenanceWithCustomPaths.maintenance.effectiveExecutablePath) {
            throw "manager set-executable did not persist the configured executable path"
        }
        $clearExecutable = Invoke-CliJson -Arguments @(
            'manager', 'clear-executable',
            '--manager', [string]$manifest.secureSettings.managerForExecutableOverride
        )
        if (-not [string]::IsNullOrWhiteSpace($clearExecutable.maintenance.configuredExecutablePath)) {
            throw "manager clear-executable did not clear the custom executable path"
        }
        Add-Tested 'manager set-executable'
        Add-Tested 'manager clear-executable'

        $secureSetOff = Invoke-CliJson -Arguments @(
            'settings', 'secure', 'set',
            '--key', [string]$manifest.secureSettings.toggleKey,
            '--enabled', 'false'
        )
        if ($secureSetOff.setting.enabled) {
            throw "settings secure set did not disable $($manifest.secureSettings.toggleKey)"
        }
    }

    Write-Stage 'Shortcut and backup'
    $syntheticShortcut = Join-Path $daemonRoot 'SyntheticShortcut.lnk'
    New-Item -ItemType File -Path $syntheticShortcut | Out-Null

    $keepShortcut = Invoke-CliJson -Arguments @('shortcut', 'set', '--path', $syntheticShortcut, '--status', 'keep')
    if ($keepShortcut.shortcut.status -ne 'keep') {
        throw "shortcut set did not persist keep"
    }
    $shortcuts = Invoke-CliJson -Arguments @('shortcut', 'list')
    if (@($shortcuts.shortcuts | Where-Object { $_.path -eq $syntheticShortcut -and $_.status -eq 'keep' -and $_.existsOnDisk }).Count -eq 0) {
        throw "shortcut list did not report the kept shortcut"
    }
    $deleteShortcut = Invoke-CliJson -Arguments @('shortcut', 'set', '--path', $syntheticShortcut, '--status', 'delete')
    if ($deleteShortcut.shortcut.status -ne 'delete' -or (Test-Path $syntheticShortcut)) {
        throw "shortcut set --status delete did not delete the shortcut"
    }
    $resetShortcut = Invoke-CliJson -Arguments @('shortcut', 'reset', '--path', $syntheticShortcut)
    if ($resetShortcut.shortcut.status -ne 'unknown') {
        throw "shortcut reset did not clear the shortcut verdict"
    }
    $resetAllShortcuts = Invoke-CliJson -Arguments @('shortcut', 'reset-all')
    Assert-JsonCommandSucceeded -Response $resetAllShortcuts -Label 'shortcut reset-all'
    Add-Tested 'shortcut list'
    Add-Tested 'shortcut set'
    Add-Tested 'shortcut reset'
    Add-Tested 'shortcut reset-all'

    $appLog = Invoke-CliJson -Arguments @('log', 'app', '--level', '5')
    if (@($appLog.entries).Count -eq 0) {
        throw "log app returned no entries"
    }
    Add-Tested 'log app'

    $backupStatus = Invoke-CliJson -Arguments @('backup', 'status')
    if ([string]::IsNullOrWhiteSpace($backupStatus.backup.backupDirectory)) {
        throw "backup status did not report the backup directory"
    }
    Add-Tested 'backup status'

    $backupDirectory = Join-Path $daemonRoot 'backups'
    $setBackupDirectory = Invoke-CliJson -Arguments @('settings', 'set', '--key', 'ChangeBackupOutputDirectory', '--value', $backupDirectory)
    $setBackupFileName = Invoke-CliJson -Arguments @('settings', 'set', '--key', 'ChangeBackupFileName', '--value', 'cli-e2e-backup')
    $disableBackupTimestamping = Invoke-CliJson -Arguments @('settings', 'set', '--key', 'EnableBackupTimestamping', '--enabled', 'false')
    if ($setBackupDirectory.setting.stringValue -ne $backupDirectory -or $setBackupFileName.setting.stringValue -ne 'cli-e2e-backup' -or $disableBackupTimestamping.setting.boolValue) {
        throw "backup settings did not persist correctly"
    }

    $localBackup = Invoke-CliJson -Arguments @('backup', 'local', 'create')
    Assert-JsonCommandSucceeded -Response $localBackup -Label 'backup local create'
    if (-not (Test-Path $localBackup.path)) {
        throw "backup local create did not write the reported backup path"
    }
    Add-Tested 'backup local create'

    Write-Stage 'Package discovery'
    $fixtureState = @{}
    foreach ($fixture in @($manifest.packageManagers)) {
        $search = Invoke-CliJson -Arguments @('package', 'search', '--manager', [string]$fixture.manager, '--query', [string]$fixture.query, '--max-results', '20')
        $details = Invoke-CliJson -Arguments (@('package', 'details') + (Get-PackageArguments -Fixture $fixture -IncludeSource))
        $versions = Invoke-CliJson -Arguments (@('package', 'versions') + (Get-PackageArguments -Fixture $fixture -IncludeSource))
        if (@($versions.versions | Where-Object { $_ -eq $fixture.installVersion }).Count -eq 0) {
            throw "package versions did not include $($fixture.installVersion) for $($fixture.packageId) on $($fixture.manager)"
        }
        if ($details.package.id -ne $fixture.packageId) {
            throw "package details did not return $($fixture.packageId) for $($fixture.manager)"
        }
        $latestVersion = Get-LatestFixtureVersion -Fixture $fixture -SearchResponse $search -VersionResponse $versions
        $downloadDirectory = Join-Path $downloadRoot (([string]$fixture.manager -replace '[^A-Za-z0-9._-]', '_') + '-sync')
        New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
        $download = Invoke-CliJson -Arguments (@('package', 'download') + (Get-PackageArguments -Fixture $fixture -IncludeSource) + @('--output', $downloadDirectory))
        Assert-JsonCommandSucceeded -Response $download -Label "package download $($fixture.packageId)"
        if ([string]::IsNullOrWhiteSpace($download.outputPath) -or -not (Test-Path $download.outputPath)) {
            throw "package download did not create an artifact for $($fixture.packageId) on $($fixture.manager)"
        }

        $fixtureState[[string]$fixture.manager] = [ordered]@{
            fixture = $fixture
            latestVersion = $latestVersion
        }
    }
    Add-Tested 'package search'
    Add-Tested 'package details'
    Add-Tested 'package versions'
    Add-Tested 'package download'

    Write-Stage 'Bundle roundtrip and bundle install'
    $resetBundle = Invoke-CliJson -Arguments @('bundle', 'reset')
    Assert-JsonCommandSucceeded -Response $resetBundle -Label 'bundle reset'
    $bundleAfterReset = Invoke-CliJson -Arguments @('bundle', 'get')
    if ($bundleAfterReset.bundle.packageCount -ne 0) {
        throw "bundle get did not return an empty bundle after reset"
    }
    $addBundlePackage = Invoke-CliJson -Arguments (@('bundle', 'add') + (Get-PackageArguments -Fixture $bundleFixture -IncludeVersion -IncludeSource) + @('--selection', 'search'))
    if ($addBundlePackage.package.id -ne $bundleFixture.packageId) {
        throw "bundle add did not add $($bundleFixture.packageId)"
    }
    $bundle = Invoke-CliJson -Arguments @('bundle', 'get')
    if (@($bundle.bundle.packages | Where-Object { $_.id -eq $bundleFixture.packageId -and $_.selectedVersion -eq $bundleFixture.installVersion }).Count -eq 0) {
        throw "bundle get did not report the selected bundle package"
    }
    $exportedBundle = Invoke-CliJson -Arguments @('bundle', 'export')
    if ([string]::IsNullOrWhiteSpace($exportedBundle.content)) {
        throw "bundle export returned no content"
    }
    $bundleRoundtripPath = Join-Path $daemonRoot 'BundleRoundtrip.json'
    Set-Content -Path $bundleRoundtripPath -Value $exportedBundle.content -Encoding UTF8
    $removeBundlePackage = Invoke-CliJson -Arguments (@('bundle', 'remove') + (Get-PackageArguments -Fixture $bundleFixture -IncludeSource))
    if ($removeBundlePackage.removedCount -lt 1) {
        throw "bundle remove did not remove $($bundleFixture.packageId)"
    }
    $importBundle = Invoke-CliJson -Arguments @('bundle', 'import', '--path', $bundleRoundtripPath)
    Assert-JsonCommandSucceeded -Response $importBundle -Label 'bundle import'
    $bundleInstall = Invoke-CliJson -Arguments @('bundle', 'install')
    if ($bundleInstall.status -ne 'success' -or @($bundleInstall.results | Where-Object { $_.package.id -eq $bundleFixture.packageId }).Count -eq 0) {
        throw "bundle install did not report a successful package result: $($bundleInstall | ConvertTo-Json -Depth 10)"
    }
    Wait-ForInstalledPackage -Fixture $bundleFixture | Out-Null
    Add-Tested 'bundle get'
    Add-Tested 'bundle reset'
    Add-Tested 'bundle add'
    Add-Tested 'bundle remove'
    Add-Tested 'bundle export'
    Add-Tested 'bundle import'
    Add-Tested 'bundle install'

    Write-Stage 'Operation queue control'
    $queuedOperationIds = @()
    foreach ($queueFixture in @($manifest.queueOperations)) {
        $queueDownload = Invoke-CliJson -Arguments @(
            'package', 'download',
            '--manager', [string]$queueFixture.manager,
            '--id', [string]$queueFixture.packageId,
            '--source', [string]$queueFixture.sourceName,
            '--output', (Resolve-QueueOutputPath -QueueFixture $queueFixture),
            '--wait', 'false'
        )
        if ($queueDownload.status -ne 'success' -or $queueDownload.completed -or [string]::IsNullOrWhiteSpace($queueDownload.operationId)) {
            throw "package download --wait false did not return an in-progress operation payload for $($queueFixture.packageId): $($queueDownload | ConvertTo-Json -Depth 10)"
        }
        $queuedOperationIds += [string]$queueDownload.operationId
    }

    $queuedOperations = Wait-ForCliCondition `
        -Arguments @('operation', 'list') `
        -Condition {
            param($response)
            $targeted = @($response.operations | Where-Object { $queuedOperationIds -contains $_.id })
            $targeted.Count -eq $queuedOperationIds.Count
        } `
        -FailureMessage 'operation list never reported the queued download operations.' `
        -TimeoutSeconds 180 `
        -DelaySeconds 2

    $queuedOperation = @($queuedOperations.operations | Where-Object {
            $queuedOperationIds -contains $_.id
        })[0]

    $operationDetails = Invoke-CliJson -Arguments @('operation', 'get', '--id', $queuedOperation.id)
    if ($operationDetails.operation.id -ne $queuedOperation.id) {
        throw "operation get did not return the requested queued operation id"
    }
    $operationOutput = Invoke-CliJson -Arguments @('operation', 'output', '--id', $queuedOperation.id, '--tail', '10')
    if ($operationOutput.output.operationId -ne $queuedOperation.id) {
        throw "operation output did not return the requested queued operation id"
    }

    foreach ($operationId in $queuedOperationIds) {
        $waitedOperation = Invoke-CliJson -Arguments @('operation', 'wait', '--id', $operationId, '--timeout', '300', '--delay', '1')
        if ($waitedOperation.operation.status -ne 'succeeded') {
            throw "operation wait did not report success for operation ${operationId}: $($waitedOperation | ConvertTo-Json -Depth 10)"
        }
    }

    foreach ($operationId in $queuedOperationIds) {
        $completedOutput = Invoke-CliJson -Arguments @('operation', 'output', '--id', $operationId)
        if ($completedOutput.output.lineCount -lt 0) {
            throw "operation output reported an invalid line count for operation $operationId"
        }
        $forget = Invoke-CliJson -Arguments @('operation', 'forget', '--id', $operationId)
        Assert-JsonCommandSucceeded -Response $forget -Label "operation forget $operationId"
    }
    $operationsAfterForget = Invoke-CliJson -Arguments @('operation', 'list')
    if (@($operationsAfterForget.operations | Where-Object { $queuedOperationIds -contains $_.id }).Count -ne 0) {
        throw "operation forget did not remove all queued download operations"
    }
    Add-Tested 'operation list'
    Add-Tested 'operation get'
    Add-Tested 'operation output'
    Add-Tested 'operation wait'
    Add-Tested 'operation forget'

    Write-Stage 'Package lifecycle and updates'
    $specificUpdateFixture = @($manifest.packageManagers | Where-Object { @($_.roles) -contains 'specific-update' })[0]
    if ($null -eq $specificUpdateFixture) {
        throw 'The CLI E2E manifest must define a package manager fixture with the specific-update role.'
    }
    $directInstallFixtures = @($manifest.packageManagers | Where-Object {
            $null -ne $specificUpdateFixture -and [string]$_.manager -eq [string]$specificUpdateFixture.manager
        })
    $otherInstallFixtures = @($manifest.packageManagers | Where-Object {
            [string]$_.manager -ne [string]$bundleFixture.manager -and
            [string]$_.manager -ne [string]$specificUpdateFixture.manager
        })
    $directInstallFixtures += $otherInstallFixtures
    $bundleLatestVersion = [string]$fixtureState[$bundleFixture.manager].latestVersion

    foreach ($installFixture in $directInstallFixtures) {
        $installResult = Invoke-CliJson -Arguments (@('package', 'install') + (Get-PackageArguments -Fixture $installFixture -IncludeVersion -IncludeSource))
        Assert-JsonCommandSucceeded -Response $installResult -Label "package install $($installFixture.manager)"
        Wait-ForInstalledPackage -Fixture $installFixture -ExpectedVersion $installFixture.installVersion | Out-Null
    }
    Add-Tested 'package install'
    Add-Tested 'package installed'

    $updateDiscoveryFixtures = @($manifest.packageManagers | Where-Object { @($_.roles) -contains 'update-discovery' })
    foreach ($updateDiscoveryFixture in $updateDiscoveryFixtures) {
        $reloadManager = Invoke-CliJson -Arguments @('manager', 'reload', '--manager', [string]$updateDiscoveryFixture.manager)
        if ($reloadManager.operationStatus -ne 'completed') {
            throw "manager reload before package updates did not complete successfully for $($updateDiscoveryFixture.manager): $($reloadManager | ConvertTo-Json -Depth 8)"
        }
    }
    foreach ($updateDiscoveryFixture in $updateDiscoveryFixtures) {
        Wait-ForPackageUpdateVisibility -Fixture $updateDiscoveryFixture | Out-Null
    }
    $allUpdates = Invoke-CliJson -Arguments @('package', 'updates')
    if ($null -eq $allUpdates.updates) {
        throw "package updates did not return an updates payload"
    }
    Add-Tested 'package updates'

    $ignoredAdd = Invoke-CliJson -Arguments @('package', 'ignored', 'add', '--manager', [string]$bundleFixture.manager, '--id', [string]$bundleFixture.packageId)
    Assert-JsonCommandSucceeded -Response $ignoredAdd -Label 'package ignored add'
    $ignoredList = Invoke-CliJson -Arguments @('package', 'ignored', 'list')
    if (@($ignoredList.ignoredUpdates | Where-Object { $_.packageId -eq $bundleFixture.packageId }).Count -eq 0) {
        throw "package ignored list did not report the ignored dotnet-tool fixture"
    }
    $ignoredRemove = Invoke-CliJson -Arguments @('package', 'ignored', 'remove', '--manager', [string]$bundleFixture.manager, '--id', [string]$bundleFixture.packageId)
    Assert-JsonCommandSucceeded -Response $ignoredRemove -Label 'package ignored remove'
    Add-Tested 'package ignored list'
    Add-Tested 'package ignored add'
    Add-Tested 'package ignored remove'

    $specificUpdate = Invoke-CliJson -Arguments (@(
            'package', 'update'
        ) + (Get-PackageArguments -Fixture $specificUpdateFixture -IncludeSource) + @(
            '--version', [string]$fixtureState[$specificUpdateFixture.manager].latestVersion
        ))
    Assert-JsonCommandSucceeded -Response $specificUpdate -Label "package update $($specificUpdateFixture.manager)"
    Wait-ForInstalledPackage -Fixture $specificUpdateFixture -ExpectedVersion ([string]$fixtureState[$specificUpdateFixture.manager].latestVersion) | Out-Null
    Add-Tested 'package update'

    $updateManager = Invoke-CliFailure -Arguments @('package', 'update-manager', '--manager', [string]$bundleFixture.manager)
    if ($updateManager.Text -notmatch 'cannot update manager packages') {
        throw "package update-manager did not report the expected headless limitation: $($updateManager.Text)"
    }
    Add-Tested 'package update-manager' 'expected-failure'

    $updateAll = Invoke-CliFailure -Arguments @('package', 'update-all')
    if ($updateAll.Text -notmatch 'cannot update all packages') {
        throw "package update-all did not report the expected headless limitation: $($updateAll.Text)"
    }
    Add-Tested 'package update-all' 'expected-failure'

    $reinstall = Invoke-CliJson -Arguments (@('package', 'reinstall') + (Get-PackageArguments -Fixture $bundleFixture -IncludeSource))
    Assert-JsonCommandSucceeded -Response $reinstall -Label 'package reinstall'
    Wait-ForInstalledPackage -Fixture $bundleFixture -ExpectedVersion $bundleLatestVersion | Out-Null
    Add-Tested 'package reinstall'

    $repair = Invoke-CliJson -Arguments (@('package', 'repair') + (Get-PackageArguments -Fixture $bundleFixture -IncludeSource))
    Assert-JsonCommandSucceeded -Response $repair -Label 'package repair'
    Wait-ForInstalledPackage -Fixture $bundleFixture -ExpectedVersion $bundleLatestVersion | Out-Null
    Add-Tested 'package repair'

    $installedAll = Invoke-CliJson -Arguments @('package', 'installed')
    foreach ($fixture in @($manifest.packageManagers)) {
        if (@($installedAll.packages | Where-Object { $_.id -eq $fixture.packageId }).Count -eq 0) {
            throw "package installed did not report $($fixture.packageId) after lifecycle operations"
        }
    }

    Write-Stage 'Logs'
    $operationHistory = Invoke-CliJson -Arguments @('log', 'operations')
    if ($null -eq $operationHistory.history) {
        throw "log operations did not return a history payload"
    }
    $managerLog = Wait-ForCliCondition `
        -Arguments @('log', 'manager', '--manager', [string]$bundleFixture.manager, '--verbose') `
        -Condition {
            param($response)
            @(
                $response.managers |
                    Where-Object {
                        $_.name -eq $bundleFixture.manager -and
                        @($_.tasks | Where-Object { @($_.lines | Where-Object { $_ -match $bundleFixture.packageId }).Count -gt 0 }).Count -gt 0
                    }
            ).Count -gt 0
        } `
        -FailureMessage "log manager did not capture package activity for $($bundleFixture.packageId)" `
        -TimeoutSeconds 180 `
        -DelaySeconds 3
    Add-Tested 'log operations'
    Add-Tested 'log manager'

    Write-Stage 'Package uninstall'
    $uninstallValidationFixtures = @($manifest.packageManagers)
    if ($null -ne $manifest.PSObject.Properties['uninstallValidationManagers']) {
        $uninstallValidationManagerNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($managerName in @($manifest.uninstallValidationManagers)) {
            if (-not [string]::IsNullOrWhiteSpace([string]$managerName)) {
                [void]$uninstallValidationManagerNames.Add([string]$managerName)
            }
        }

        $uninstallValidationFixtures = @($manifest.packageManagers | Where-Object {
                $uninstallValidationManagerNames.Contains([string]$_.manager)
            })
    }
    foreach ($fixture in $uninstallValidationFixtures) {
        $uninstall = Invoke-CliJson -Arguments (@('package', 'uninstall') + (Get-PackageArguments -Fixture $fixture -IncludeSource))
        Assert-JsonCommandSucceeded -Response $uninstall -Label "package uninstall $($fixture.packageId)"
        if (-not [string]::IsNullOrWhiteSpace($uninstall.operationId)) {
            $waitedUninstall = Invoke-CliJson -Arguments @('operation', 'wait', '--id', [string]$uninstall.operationId, '--timeout', '300', '--delay', '1')
            if ($waitedUninstall.operation.status -ne 'succeeded') {
                throw "package uninstall did not complete successfully for $($fixture.packageId): $($waitedUninstall | ConvertTo-Json -Depth 10)"
            }
        }
        $reloadManager = Invoke-CliJson -Arguments @('manager', 'reload', '--manager', [string]$fixture.manager)
        if ($reloadManager.operationStatus -ne 'completed') {
            throw "manager reload after uninstall did not complete successfully for $($fixture.manager): $($reloadManager | ConvertTo-Json -Depth 8)"
        }
        Wait-ForPackageRemoval -Fixture $fixture | Out-Null
    }
    if ($uninstallValidationFixtures.Count -gt 0) {
        Add-Tested 'package uninstall'
    }

    Write-Stage 'Settings reset and shutdown'
    $clearFreshValue = Invoke-CliJson -Arguments @('settings', 'clear', '--key', 'FreshValue')
    if ($clearFreshValue.setting.isSet) {
        throw "settings clear did not clear FreshValue"
    }
    $disableFreshBool = Invoke-CliJson -Arguments @('settings', 'set', '--key', 'FreshBoolSetting', '--enabled', 'false')
    if ($disableFreshBool.setting.boolValue) {
        throw "settings set did not disable FreshBoolSetting"
    }
    Add-Tested 'settings clear'

    $resetSettings = Invoke-CliJson -Arguments @('settings', 'reset')
    Assert-JsonCommandSucceeded -Response $resetSettings -Label 'settings reset'
    Add-Tested 'settings reset'

    $postResetStatus = Invoke-CliJson -Arguments @('status')
    if (-not $postResetStatus.running) {
        throw "settings reset broke the active IPC session"
    }

    $quitApp = Invoke-CliJson -Arguments @('app', 'quit')
    Assert-JsonCommandSucceeded -Response $quitApp -Label 'app quit'
    Add-Tested 'app quit'

    $quitDeadline = (Get-Date).AddSeconds(30)
    while (-not $process.HasExited -and (Get-Date) -lt $quitDeadline) {
        Start-Sleep -Seconds 1
    }

    if (-not $process.HasExited) {
        throw "app quit did not stop the headless daemon"
    }

    $gracefulShutdown = $true
}
finally {
    $coverage.status = if ($gracefulShutdown) { 'success' } else { 'failed' }
    Set-Content -Path $coveragePath -Value ($coverage | ConvertTo-Json -Depth 12) -Encoding UTF8

    if (-not $gracefulShutdown) {
        Stop-Daemon
    }

    $daemonLog = Get-DaemonLog
    if (-not [string]::IsNullOrWhiteSpace($daemonLog)) {
        Write-Host '--- Headless daemon log ---'
        Write-Host $daemonLog
    }

    if (-not $preserveArtifacts) {
        Remove-Item -Recurse -Force $daemonRoot -ErrorAction SilentlyContinue
    }
}
