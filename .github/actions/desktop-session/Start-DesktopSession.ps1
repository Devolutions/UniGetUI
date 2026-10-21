# Sets up a live interactive Windows desktop on this runner with IronRDP's agentic RDP
# harness (testing/agentic-rdp in the IronRDP repo): localhost RDP, an ironrdp-agent daemon
# connected to it, and a PowerShell remoting endpoint running inside that desktop session.
#
# IronRDP's own orchestrator tears the session down when it returns, so this script calls
# its stage scripts directly (except the daemon start, see Start-DetachedAgentDaemon) and
# leaves the session up for later steps of the job. It then
# exports what those steps need (see DesktopSession.psm1) to GITHUB_ENV and GITHUB_OUTPUT.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $IronRdpRoot,

    [string] $AgentPath = (Join-Path $IronRdpRoot 'target\release\ironrdp-agent.exe'),

    [string] $DesktopSize = '1920x1080',

    [Parameter(Mandatory)]
    [string] $ArtifactsDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$IronRdpRoot = [System.IO.Path]::GetFullPath($IronRdpRoot)
$AgentPath = [System.IO.Path]::GetFullPath($AgentPath)
$ArtifactsDir = [System.IO.Path]::GetFullPath($ArtifactsDir)
$harnessRoot = Join-Path $IronRdpRoot 'testing\agentic-rdp'
# The daemon outlives this step, so the pipe name is recorded rather than derived from $PID later.
$endpoint = "\\.\pipe\desktop-session-$PID"
$rdpStatePath = Join-Path $ArtifactsDir 'rdp-state.json'
$daemonStatePath = Join-Path $ArtifactsDir 'agent-daemon.json'
$sessionStatePath = Join-Path $ArtifactsDir 'agent-session.json'
$psHostEndpointPath = Join-Path $ArtifactsDir 'pshost-endpoint.json'
# Same names as IronRDP's orchestrator, so its -CleanupOnly mode finds them.
$taskName = 'IronRdpAgenticPSHost'
$psHostPort = 45985

# Server Manager opens at every administrator logon on Windows Server and would sit on top
# of whatever later steps put on the desktop. Its scheduled task and its own registry key
# can't be changed from here, so this sets the equivalent group policy.
function Disable-ServerManagerAtLogon {
    try {
        New-Item -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Server\ServerManager' -Force | Out-Null
        New-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Server\ServerManager' -Name 'DoNotOpenAtLogon' -PropertyType DWord -Value 1 -Force | Out-Null
    }
    catch {
        Write-Warning "Could not disable Server Manager at logon: $($_.Exception.Message)"
    }
}

# The runner ends every process a step started when the step ends, so IronRDP's
# Start-AgentDaemon.ps1 (Start-Process) would leave a daemon that dies with this step.
# A process created through WMI is a child of the WMI provider host instead, so it outlives
# the step. Same readiness probe and state file as Start-AgentDaemon.ps1, so IronRDP's
# cleanup still stops it.
function Start-DetachedAgentDaemon {
    $stdoutPath = Join-Path $ArtifactsDir 'ironrdp-agent.stdout.log'
    $stderrPath = Join-Path $ArtifactsDir 'ironrdp-agent.stderr.log'
    $commandLine = "cmd.exe /d /s /c `"set IRONRDP_LOG=debug&& `"$AgentPath`" --endpoint $endpoint daemon-start --skip-certificate-check 1> `"$stdoutPath`" 2> `"$stderrPath`"`""
    $created = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = $commandLine
        CurrentDirectory = $ArtifactsDir
    }
    if ($created.ReturnValue -ne 0) {
        throw "Win32_Process.Create failed with code $($created.ReturnValue) for: $commandLine"
    }

    # A failing native command does not throw, so readiness is detected via the exit code.
    # stderr is discarded because "pipe not found" is expected until the daemon is listening.
    $agentProcess = $null
    $deadline = (Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        if ($null -eq $agentProcess) {
            $agentProcess = Get-CimInstance -ClassName Win32_Process -Filter "ParentProcessId = $($created.ProcessId) AND Name = 'ironrdp-agent.exe'" |
                Select-Object -First 1
        }

        & $AgentPath --endpoint $endpoint status 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0 -and $null -ne $agentProcess) {
            $state = [pscustomobject]@{
                ProcessId = [int] $agentProcess.ProcessId
                WrapperProcessId = [int] $created.ProcessId
                Endpoint = $endpoint
                AgentPath = $AgentPath
                LogPath = $stderrPath
                StandardOutputPath = $stdoutPath
                StandardErrorPath = $stderrPath
                CertificateCheckSkipped = $true
            }
            $state | ConvertTo-Json -Depth 4 | Set-Content -Path $daemonStatePath -Encoding utf8NoBOM
            return $state
        }

        if ($null -eq (Get-Process -Id $created.ProcessId -ErrorAction SilentlyContinue)) {
            throw "ironrdp-agent daemon exited during startup; see $stderrPath"
        }
    } while ((Get-Date) -lt $deadline)

    Get-Process -Id $created.ProcessId -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if ($null -ne $agentProcess) {
        Stop-Process -Id $agentProcess.ProcessId -Force -ErrorAction SilentlyContinue
    }
    throw "Timed out waiting for ironrdp-agent daemon on $endpoint"
}

New-Item -Path $ArtifactsDir -ItemType Directory -Force | Out-Null

# Export first, so the cleanup action can find the session even if a stage below fails.
$exports = [ordered]@{
    DESKTOP_SESSION_AGENT = $AgentPath
    DESKTOP_SESSION_ENDPOINT = $endpoint
    DESKTOP_SESSION_PSHOST_ENDPOINT = $psHostEndpointPath
    DESKTOP_SESSION_IRONRDP_ROOT = $IronRdpRoot
    DESKTOP_SESSION_ARTIFACTS = $ArtifactsDir
    DESKTOP_SESSION_SIZE = $DesktopSize
    DESKTOP_SESSION_MODULE = Join-Path $PSScriptRoot 'DesktopSession.psm1'
}
foreach ($entry in $exports.GetEnumerator()) {
    [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_ENV)) {
        "$($entry.Key)=$($entry.Value)" | Add-Content -Path $env:GITHUB_ENV -Encoding utf8
    }
}
$exports | ConvertTo-Json | Set-Content -Path (Join-Path $ArtifactsDir 'desktop-session.json') -Encoding utf8NoBOM

$connected = $false
try {
    Write-Host '::group::Enable local RDP'
    $rdpInfo = & (Join-Path $harnessRoot 'Enable-LocalRdp.ps1') -StatePath $rdpStatePath | ConvertFrom-Json
    Disable-ServerManagerAtLogon
    Write-Host '::endgroup::'

    Write-Host '::group::Start ironrdp-agent daemon'
    Start-DetachedAgentDaemon | ConvertTo-Json -Compress | Write-Host
    Write-Host '::endgroup::'

    Write-Host '::group::Register interactive PSHostServer'
    & (Join-Path $harnessRoot 'Start-InteractivePSHostServer.ps1') `
        -Register `
        -WorkspaceRoot $IronRdpRoot `
        -EndpointPath $psHostEndpointPath `
        -Port $psHostPort `
        -ArtifactsDir $ArtifactsDir `
        -TaskName $taskName | Write-Host
    Write-Host '::endgroup::'

    Write-Host '::group::Connect ironrdp-agent session'
    & (Join-Path $harnessRoot 'Connect-AgentSession.ps1') `
        -AgentPath $AgentPath `
        -Endpoint $endpoint `
        -UserName $rdpInfo.DomainUserName `
        -Password $rdpInfo.Password `
        -HostName $rdpInfo.HostName `
        -Port ([int] $rdpInfo.Port) `
        -DesktopSize $DesktopSize `
        -ArtifactsDir $ArtifactsDir `
        -StatePath $sessionStatePath | Write-Host
    $connected = $true
    Write-Host '::endgroup::'

    Write-Host '::group::Start interactive PSHostServer'
    Start-ScheduledTask -TaskName $taskName
    & (Join-Path $harnessRoot 'Start-InteractivePSHostServer.ps1') `
        -Wait `
        -WorkspaceRoot $IronRdpRoot `
        -EndpointPath $psHostEndpointPath `
        -TimeoutSeconds 180 | Write-Host
    Write-Host '::endgroup::'

    Write-Host '::group::Verify interactive remoting'
    & (Join-Path $harnessRoot 'Invoke-InteractiveCommand.ps1') `
        -Mode Verify `
        -WorkspaceRoot $IronRdpRoot `
        -EndpointPath $psHostEndpointPath `
        -ArtifactsDir $ArtifactsDir | Write-Host
    Write-Host '::endgroup::'
}
catch {
    Write-Host "::error::Desktop session setup failed: $($_.Exception.Message)"
    if ($connected) {
        $failureScreenshotPath = Join-Path $ArtifactsDir 'desktop-session-failure.png'
        & $AgentPath --endpoint $endpoint screenshot $failureScreenshotPath 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Warning "Captured failure screenshot at $failureScreenshotPath"
        }
    }

    throw
}

$sessionId = (Get-Content -Path $psHostEndpointPath -Raw | ConvertFrom-Json).SessionId
Write-Host "Desktop session $sessionId is ready ($DesktopSize)"

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    @(
        "agent-path=$AgentPath"
        "endpoint=$endpoint"
        "pshost-endpoint-path=$psHostEndpointPath"
        "ironrdp-root=$IronRdpRoot"
        "artifacts-dir=$ArtifactsDir"
        "module-path=$($exports.DESKTOP_SESSION_MODULE)"
        "session-id=$sessionId"
    ) | Add-Content -Path $env:GITHUB_OUTPUT -Encoding utf8
}
