# Tears down what Start-DesktopSession.ps1 set up: disconnects the RDP session, then runs
# IronRDP's cleanup (daemon, PSHost server task, firewall rule and RDP settings). It only
# warns on errors, so it is safe to run with if: always() even when setup never finished.
[CmdletBinding()]
param(
    [string] $IronRdpRoot = $env:DESKTOP_SESSION_IRONRDP_ROOT,

    [string] $AgentPath = $env:DESKTOP_SESSION_AGENT,

    [string] $Endpoint = $env:DESKTOP_SESSION_ENDPOINT,

    [string] $ArtifactsDir = $env:DESKTOP_SESSION_ARTIFACTS
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ArtifactsDir) -or [string]::IsNullOrWhiteSpace($IronRdpRoot)) {
    Write-Host 'No desktop session was started; nothing to clean up.'
    return
}

$sessionStatePath = Join-Path $ArtifactsDir 'agent-session.json'
if ((Test-Path $sessionStatePath) -and -not [string]::IsNullOrWhiteSpace($AgentPath) -and (Test-Path $AgentPath)) {
    $output = & $AgentPath --endpoint $Endpoint disconnect 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not disconnect the agent session: $(($output | Out-String).Trim())"
    }

    # IronRDP's cleanup would otherwise try to disconnect again through a pipe name derived
    # from its own process id.
    Remove-Item -Path $sessionStatePath -Force -ErrorAction SilentlyContinue
}

$cleanupScript = Join-Path $IronRdpRoot 'testing\agentic-rdp\Invoke-AgenticRdpTest.ps1'
if (-not (Test-Path $cleanupScript)) {
    Write-Warning "IronRDP cleanup script was not found at $cleanupScript"
    return
}

try {
    & $cleanupScript -CleanupOnly -WorkspaceRoot $IronRdpRoot -AgentPath $AgentPath -ArtifactsDir $ArtifactsDir
}
catch {
    Write-Warning "IronRDP cleanup failed: $($_.Exception.Message)"
}

# The daemon was started through a cmd.exe wrapper (see Start-DesktopSession.ps1), which
# IronRDP's cleanup doesn't know about.
$daemonStatePath = Join-Path $ArtifactsDir 'agent-daemon.json'
if (Test-Path $daemonStatePath) {
    $daemonState = Get-Content -Path $daemonStatePath -Raw | ConvertFrom-Json
    if ($daemonState.PSObject.Properties.Name -contains 'WrapperProcessId') {
        Stop-Process -Id ([int] $daemonState.WrapperProcessId) -Force -ErrorAction SilentlyContinue
    }
}

# Warnings above must not fail the step through a native command's leftover exit code.
$global:LASTEXITCODE = 0
