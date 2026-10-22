# Stops every running UniGetUI and waits for it to exit, since the running app locks the build
# output that tests and formatters need. Copilot CLI refuses to run a bare
# `Get-Process | Stop-Process` pipeline, so the agent uses this script instead.
[CmdletBinding()]
param(
    [int] $TimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$processes = @(Get-Process -Name 'UniGetUI' -ErrorAction SilentlyContinue)
foreach ($process in $processes) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
foreach ($process in $processes) {
    $remaining = [Math]::Max(0, [int] ($deadline - (Get-Date)).TotalMilliseconds)
    if (-not $process.WaitForExit($remaining)) {
        throw "UniGetUI (pid $($process.Id)) did not exit within $TimeoutSeconds seconds"
    }
}

Write-Host "Stopped $($processes.Count) UniGetUI process(es)"
