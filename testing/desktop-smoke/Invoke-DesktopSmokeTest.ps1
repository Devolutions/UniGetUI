# Runs UniGetUI inside a real interactive Windows desktop session, using IronRDP's
# agentic RDP harness (testing/agentic-rdp in the IronRDP repo) for every stage up to an
# interactive PowerShell remoting endpoint, then Invoke-UniGetUISmokeScenario.ps1.
#
# IronRDP's own orchestrator always tears the session down when it returns, so this script
# calls its stage scripts directly and keeps the session alive for the UniGetUI stage.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $IronRdpRoot,

    [Parameter(Mandatory)]
    [string] $UniGetUIPath,

    [string] $DesktopSize = '1920x1080',

    [string] $AgentPath = (Join-Path $IronRdpRoot 'target\release\ironrdp-agent.exe'),

    [Parameter(Mandatory)]
    [string] $ArtifactsDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$harnessRoot = Join-Path $IronRdpRoot 'testing\agentic-rdp'
$endpoint = "\\.\pipe\unigetui-desktop-smoke-$PID"
$rdpStatePath = Join-Path $ArtifactsDir 'rdp-state.json'
$daemonStatePath = Join-Path $ArtifactsDir 'agent-daemon.json'
$sessionStatePath = Join-Path $ArtifactsDir 'agent-session.json'
$psHostEndpointPath = Join-Path $ArtifactsDir 'pshost-endpoint.json'
# Same names as IronRDP's orchestrator, so its -CleanupOnly mode finds them.
$taskName = 'IronRdpAgenticPSHost'
$psHostPort = 45985

function Invoke-Agent {
    param(
        [Parameter(ValueFromRemainingArguments)]
        [string[]] $Arguments
    )

    $output = & $AgentPath --endpoint $endpoint @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $message = ($output | Out-String).Trim()
        throw "ironrdp-agent $($Arguments -join ' ') failed: $message"
    }
    return $output
}

# Server Manager opens at every administrator logon on Windows Server and would sit on top
# of the desktop that the screenshots check.
function Disable-ServerManagerAtLogon {
    try {
        New-Item -Path 'HKLM:\SOFTWARE\Microsoft\ServerManager' -Force | Out-Null
        New-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\ServerManager' -Name 'DoNotOpenServerManagerAtLogon' -PropertyType DWord -Value 1 -Force | Out-Null
        Get-ScheduledTask -TaskName 'ServerManager' -ErrorAction SilentlyContinue | Disable-ScheduledTask | Out-Null
    }
    catch {
        Write-Warning "Could not disable Server Manager at logon: $($_.Exception.Message)"
    }
}

function Invoke-Cleanup {
    Get-Process -Name 'UniGetUI' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    if (Test-Path $sessionStatePath) {
        try {
            Invoke-Agent disconnect | Out-Null
        }
        catch {
            Write-Warning "Could not disconnect the agent session: $($_.Exception.Message)"
        }

        # IronRDP's cleanup would otherwise try to disconnect again through a pipe name
        # derived from its own process id.
        Remove-Item -Path $sessionStatePath -Force -ErrorAction SilentlyContinue
    }

    & (Join-Path $harnessRoot 'Invoke-AgenticRdpTest.ps1') `
        -CleanupOnly `
        -WorkspaceRoot $IronRdpRoot `
        -AgentPath $AgentPath `
        -ArtifactsDir $ArtifactsDir
}

New-Item -Path $ArtifactsDir -ItemType Directory -Force | Out-Null
$sessionInfo = $null

try {
    Write-Host '::group::Enable local RDP'
    $rdpInfo = & (Join-Path $harnessRoot 'Enable-LocalRdp.ps1') -StatePath $rdpStatePath | ConvertFrom-Json
    Disable-ServerManagerAtLogon
    Write-Host '::endgroup::'

    Write-Host '::group::Start ironrdp-agent daemon'
    & (Join-Path $harnessRoot 'Start-AgentDaemon.ps1') `
        -AgentPath $AgentPath `
        -Endpoint $endpoint `
        -ArtifactsDir $ArtifactsDir `
        -StatePath $daemonStatePath `
        -SkipCertificateCheck | Write-Host
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
    $sessionJson = & (Join-Path $harnessRoot 'Connect-AgentSession.ps1') `
        -AgentPath $AgentPath `
        -Endpoint $endpoint `
        -UserName $rdpInfo.DomainUserName `
        -Password $rdpInfo.Password `
        -HostName $rdpInfo.HostName `
        -Port ([int] $rdpInfo.Port) `
        -DesktopSize $DesktopSize `
        -ArtifactsDir $ArtifactsDir `
        -StatePath $sessionStatePath
    $sessionInfo = $sessionJson | ConvertFrom-Json
    Write-Host $sessionJson
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

    Write-Host '::group::Run UniGetUI desktop smoke scenario'
    & (Join-Path $PSScriptRoot 'Invoke-UniGetUISmokeScenario.ps1') `
        -AgentPath $AgentPath `
        -Endpoint $endpoint `
        -PSHostEndpointPath $psHostEndpointPath `
        -UniGetUIPath $UniGetUIPath `
        -ArtifactsDir $ArtifactsDir `
        -DesktopSize $DesktopSize | Write-Host
    Write-Host '::endgroup::'
}
catch {
    Write-Host "::error::UniGetUI desktop smoke test failed: $($_.Exception.Message)"
    if ($null -ne $sessionInfo) {
        try {
            $failureScreenshotPath = Join-Path $ArtifactsDir 'agent-failure.png'
            Invoke-Agent screenshot $failureScreenshotPath | Out-Null
            Write-Warning "Captured failure screenshot at $failureScreenshotPath"
        }
        catch {
            Write-Warning "Could not capture failure screenshot: $($_.Exception.Message)"
        }
    }

    throw
}
finally {
    Invoke-Cleanup
}
