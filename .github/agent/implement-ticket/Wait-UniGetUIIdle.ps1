# Waits until UniGetUI has settled: the page on screen is not loading packages, and the main
# window looks the same for -SettleSeconds. Use it after launching the app or acting on it,
# instead of a fixed sleep, so screenshots don't catch a page half loaded.
#
# Loading is read from the UI Automation tree: a packages page shows its LoadingProgressBar
# only while it loads, and Avalonia leaves invisible controls out of the tree. An open dialog
# does not count as busy; its title is reported so a screenshot of it can be taken on purpose.
#
# With -Screenshot, saves a screenshot once settled (a relative name goes in
# $env:AGENT_SCREENSHOTS). Prints a JSON summary; Idle is false if the timeout was reached.
[CmdletBinding()]
param(
    [string] $Screenshot,

    [int] $TimeoutSeconds = 120,

    [int] $SettleSeconds = 3,

    # Share of sampled window pixels that may differ between two polls of a settled window.
    [double] $MaxChange = 0.002
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module $env:DESKTOP_SESSION_MODULE -Force

$stateProbe = {
    param($Handle)

    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $automation = [System.Windows.Automation.AutomationElement]
    $root = $automation::FromHandle([IntPtr] [long] $Handle)
    $find = {
        param([string] $Id)
        $condition = [System.Windows.Automation.PropertyCondition]::new($automation::AutomationIdProperty, $Id)
        $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    }
    $dialog = & $find 'ModalTitle'

    [pscustomobject]@{
        Loading = $null -ne (& $find 'LoadingProgressBar')
        Dialog = if ($null -ne $dialog) { $dialog.Current.Name } else { $null }
    }
}

$pollDir = Join-Path ([System.IO.Path]::GetTempPath()) "unigetui-idle-$PID"
New-Item -Path $pollDir -ItemType Directory -Force | Out-Null
$started = Get-Date
$deadline = $started.AddSeconds($TimeoutSeconds)
$previous = $null
$stableSince = $null
$idle = $false
$state = $null
$poll = 0

try {
    while ($true) {
        $window = Get-DesktopWindow -ProcessName 'UniGetUI' -Main
        if ($null -eq $window) {
            throw 'UniGetUI has no main window; start it with Start-UniGetUIDev.ps1'
        }

        $state = Invoke-DesktopCommand -ArgumentList $window.Handle -ScriptBlock $stateProbe
        $current = Save-DesktopScreenshot -Name (Join-Path $pollDir "poll-$poll.png")
        $poll++
        $change = if ($null -ne $previous) {
            Get-DesktopScreenshotChange -BeforePath $previous -AfterPath $current -Rect $window
        }
        else {
            1.0
        }
        if ($null -ne $previous) {
            Remove-Item -LiteralPath $previous -Force
        }
        $previous = $current

        if ($state.Loading -or $change -gt $MaxChange) {
            $stableSince = $null
        }
        elseif ($null -eq $stableSince) {
            $stableSince = Get-Date
        }

        if ($null -ne $stableSince -and ((Get-Date) - $stableSince).TotalSeconds -ge $SettleSeconds) {
            $idle = $true
            break
        }
        if ((Get-Date) -ge $deadline) {
            break
        }
        Start-Sleep -Milliseconds 750
    }

    if (-not $idle) {
        $reason = if ($state.Loading) { 'is still loading packages' } else { 'is still changing on screen' }
        Write-Warning "UniGetUI $reason after $TimeoutSeconds seconds"
    }

    $screenshotPath = $null
    if (-not [string]::IsNullOrWhiteSpace($Screenshot)) {
        $screenshotPath = if ([System.IO.Path]::IsPathRooted($Screenshot)) {
            $Screenshot
        }
        else {
            Join-Path $env:AGENT_SCREENSHOTS $Screenshot
        }
        Save-DesktopScreenshot -Name $screenshotPath | Out-Null
    }

    [pscustomobject]@{
        Idle = $idle
        Loading = $state.Loading
        Dialog = $state.Dialog
        WaitedSeconds = [Math]::Round(((Get-Date) - $started).TotalSeconds, 1)
        Screenshot = $screenshotPath
    } | ConvertTo-Json -Compress
}
finally {
    Remove-Item -LiteralPath $pollDir -Recurse -Force -ErrorAction SilentlyContinue
    Close-DesktopPSSession
}
