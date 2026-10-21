# Launches UniGetUI inside the desktop session set up by the desktop-session action, gets it
# past its first-run UI, and leaves its main window maximized in the foreground.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $UniGetUIPath,

    # Opt out of telemetry, and mark the opt-in banner as shown.
    [bool] $DisableTelemetry = $true,

    # Package managers to disable, by their internal name. Cargo needs cargo-binstall and
    # cargo-update, which a runner doesn't have, and would ask for them at startup.
    [string[]] $DisabledManagers = @('Cargo'),

    [int] $WindowTimeoutSeconds = 180,

    # Startup dialogs are done once none has shown for this long.
    [int] $DialogSettleSeconds = 30,

    [int] $DialogTimeoutSeconds = 150
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module $env:DESKTOP_SESSION_MODULE -Force

# UniGetUI shows its dialogs (ImmersiveDialog) as an overlay inside the main window, not as
# separate windows. The overlay's title TextBlock is only in the UI Automation tree while a
# dialog is open, so its presence and text tell which dialog is showing.
$modalProbe = {
    param($Handle)

    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $automation = [System.Windows.Automation.AutomationElement]
    $root = $automation::FromHandle([IntPtr] [long] $Handle)
    $condition = [System.Windows.Automation.PropertyCondition]::new($automation::AutomationIdProperty, 'ModalTitle')
    $title = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)

    [pscustomobject]@{
        IsOpen = $null -ne $title
        Title = if ($null -ne $title) { $title.Current.Name } else { $null }
    }
}

$desktop = Get-DesktopSessionInfo

try {
    # Settings are written before the first launch, as the user who runs UniGetUI. A boolean
    # setting is an empty file named after it in the configuration directory; a dictionary
    # setting is a JSON object in "<name>.json".
    $settings = Invoke-DesktopCommand -ArgumentList $DisableTelemetry, (, $DisabledManagers) -ScriptBlock {
        param([bool] $OptOut, [string[]] $Managers)

        $configurationDir = Join-Path $env:LOCALAPPDATA 'UniGetUI\Configuration'
        New-Item -Path $configurationDir -ItemType Directory -Force | Out-Null
        if ($OptOut) {
            foreach ($setting in @('DisableTelemetry', 'ShownTelemetryBanner')) {
                New-Item -Path (Join-Path $configurationDir $setting) -ItemType File -Force | Out-Null
            }
        }

        $disabled = [ordered]@{}
        foreach ($manager in @($Managers | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
            $disabled[$manager] = $true
        }
        if ($disabled.Count -gt 0) {
            $disabled | ConvertTo-Json -Compress | Set-Content -Path (Join-Path $configurationDir 'DisabledManagers.json') -Encoding utf8NoBOM -NoNewline
        }

        [pscustomobject]@{
            ConfigurationDir = $configurationDir
            TelemetryDisabled = Test-Path (Join-Path $configurationDir 'DisableTelemetry')
            DisabledManagers = @($disabled.Keys)
        }
    }
    if ($DisableTelemetry -and -not $settings.TelemetryDisabled) {
        throw "Could not disable UniGetUI telemetry in $($settings.ConfigurationDir)"
    }
    Write-Host "UniGetUI configuration: $($settings.ConfigurationDir) (telemetry disabled: $($settings.TelemetryDisabled); disabled managers: $(@($settings.DisabledManagers) -join ', '))"

    # Park the pointer in a corner, then record the bare desktop for later comparisons.
    Move-DesktopMouse -X 1 -Y ($desktop.Height - 1)
    $baselinePath = Save-DesktopScreenshot -Name 'desktop-before-unigetui.png'
    Test-DesktopScreenshot -Path $baselinePath

    $launch = Start-DesktopProcess -FilePath $UniGetUIPath
    Write-Host "Launched UniGetUI: process $($launch.ProcessId) in session $($launch.SessionId)"
    if ($launch.SessionId -ne $desktop.SessionId) {
        throw "UniGetUI started in session $($launch.SessionId), not in the desktop session $($desktop.SessionId)"
    }

    $deadline = (Get-Date).AddSeconds($WindowTimeoutSeconds)
    $mainWindow = $null
    do {
        Start-Sleep -Seconds 2
        $mainWindow = Get-DesktopWindow -ProcessName 'UniGetUI' -Main
    } while ($null -eq $mainWindow -and (Get-Date) -lt $deadline)

    if ($null -eq $mainWindow) {
        Get-DesktopWindow -ProcessName 'UniGetUI' | ConvertTo-Json -Depth 4 |
            Set-Content -Path (Join-Path $desktop.ArtifactsDir 'unigetui-window-probe.json') -Encoding utf8NoBOM
        throw "UniGetUI main window did not appear in session $($desktop.SessionId) within $WindowTimeoutSeconds seconds"
    }
    Write-Host "UniGetUI main window appeared: '$($mainWindow.Title)' (pid $($mainWindow.ProcessId))"

    $activated = Show-DesktopWindow -Handle $mainWindow.Handle -Maximize
    Write-Host "Maximized UniGetUI main window (SetForegroundWindow: $activated)"

    # First-run UI: startup checks open dialogs a little after the main window appears, and
    # their timing varies between runs. Missing package manager dependencies are expected on
    # a fresh runner and are skipped with Escape, as a user would. Any other dialog (a crash
    # report, an integrity violation) fails. Done once no dialog has shown for
    # $DialogSettleSeconds, judged only on a probe taken after the last Escape.
    $dismissedDialogs = [System.Collections.Generic.List[string]]::new()
    $lastDialogAt = Get-Date
    $dialogDeadline = $lastDialogAt.AddSeconds($DialogTimeoutSeconds)
    $firstRunCaptured = $false
    $modal = $null
    do {
        Start-Sleep -Seconds 2
        $modal = Invoke-DesktopCommand -ArgumentList $mainWindow.Handle -ScriptBlock $modalProbe
        if ($modal.IsOpen) {
            $lastDialogAt = Get-Date
            if (-not $firstRunCaptured) {
                Save-DesktopScreenshot -Name 'unigetui-first-run-dialog.png' | Out-Null
                $firstRunCaptured = $true
            }

            if ($modal.Title -notlike 'Missing dependency*') {
                throw "UniGetUI shows an unexpected dialog: '$($modal.Title)'"
            }

            Write-Host "Skipping UniGetUI dialog: '$($modal.Title)'"
            $dismissedDialogs.Add($modal.Title)
            Send-DesktopKey -Scancode '0x01'
        }
    } while (($modal.IsOpen -or ((Get-Date) - $lastDialogAt).TotalSeconds -lt $DialogSettleSeconds) -and (Get-Date) -lt $dialogDeadline)

    if ($modal.IsOpen) {
        throw "UniGetUI dialog '$($modal.Title)' is still open after $DialogTimeoutSeconds seconds"
    }

    $mainWindow = Get-DesktopWindow -ProcessName 'UniGetUI' -Main
    if ($null -eq $mainWindow) {
        throw 'UniGetUI main window disappeared during startup'
    }
    Show-DesktopWindow -Handle $mainWindow.Handle -Maximize | Out-Null

    $result = [pscustomobject]@{
        ExePath = $UniGetUIPath
        ProcessId = $mainWindow.ProcessId
        SessionId = $desktop.SessionId
        MainWindow = $mainWindow
        DismissedDialogs = $dismissedDialogs
        Settings = $settings
        BaselineScreenshot = $baselinePath
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $desktop.ArtifactsDir 'unigetui-desktop.json') -Encoding utf8NoBOM

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        @(
            "process-id=$($mainWindow.ProcessId)"
            "window-handle=$($mainWindow.Handle)"
            "baseline-screenshot=$baselinePath"
        ) | Add-Content -Path $env:GITHUB_OUTPUT -Encoding utf8
    }
}
catch {
    $failurePath = Join-Path $desktop.ArtifactsDir 'unigetui-desktop-failure.png'
    try {
        Save-DesktopScreenshot -Name $failurePath | Out-Null
        Write-Warning "Captured failure screenshot at $failurePath"
    }
    catch {
        Write-Warning "Could not capture failure screenshot: $($_.Exception.Message)"
    }

    throw
}
finally {
    Close-DesktopPSSession
}
