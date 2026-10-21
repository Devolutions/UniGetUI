# Launches UniGetUI inside the interactive RDP session through the interactive PSHost
# remoting endpoint, waits for its main window, and checks ironrdp-agent screenshots.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $AgentPath,

    [Parameter(Mandatory)]
    [string] $Endpoint,

    [Parameter(Mandatory)]
    [string] $PSHostEndpointPath,

    [Parameter(Mandatory)]
    [string] $UniGetUIPath,

    [Parameter(Mandatory)]
    [string] $ArtifactsDir,

    [string] $DesktopSize = '1920x1080',

    # Package managers to disable, by their internal name. Cargo needs cargo-binstall and
    # cargo-update, which the runner doesn't have, and would ask for them at startup.
    [string[]] $DisabledManagers = @('Cargo'),

    [int] $WindowTimeoutSeconds = 180,

    # Startup dialogs are done once none has shown for this long.
    [int] $DialogSettleSeconds = 30,

    [int] $DialogTimeoutSeconds = 150,

    [int] $ScreenshotTimeoutSeconds = 60,

    # Share of sampled pixels inside the UniGetUI window that must differ from the bare desktop.
    [double] $MinChangedFraction = 0.25
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Agent {
    param(
        [Parameter(ValueFromRemainingArguments)]
        [string[]] $Arguments
    )

    $output = & $AgentPath --endpoint $Endpoint @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $message = ($output | Out-String).Trim()
        throw "ironrdp-agent $($Arguments -join ' ') failed: $message"
    }
    return $output
}

function Send-Key {
    param([Parameter(Mandatory)] [string] $Scancode)

    Invoke-Agent key-scancode --scancode $Scancode --pressed true | Out-Null
    Invoke-Agent key-scancode --scancode $Scancode --pressed false | Out-Null
}

function Save-Screenshot {
    param([Parameter(Mandatory)] [string] $Name)

    $path = Join-Path $ArtifactsDir $Name
    Invoke-Agent screenshot $path | Out-Null
    return $path
}

function Test-PngScreenshot {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [int] $ExpectedWidth,

        [Parameter(Mandatory)]
        [int] $ExpectedHeight
    )

    $bitmap = [System.Drawing.Bitmap]::new($Path)
    try {
        if ($bitmap.Width -ne $ExpectedWidth -or $bitmap.Height -ne $ExpectedHeight) {
            throw "Screenshot is $($bitmap.Width)x$($bitmap.Height), expected ${ExpectedWidth}x${ExpectedHeight}"
        }

        $firstPixel = $bitmap.GetPixel(0, 0).ToArgb()
        $stepX = [Math]::Max(1, [Math]::Floor($bitmap.Width / 64))
        $stepY = [Math]::Max(1, [Math]::Floor($bitmap.Height / 64))
        for ($y = 0; $y -lt $bitmap.Height; $y += $stepY) {
            for ($x = 0; $x -lt $bitmap.Width; $x += $stepX) {
                if ($bitmap.GetPixel($x, $y).ToArgb() -ne $firstPixel) {
                    return
                }
            }
        }

        throw "Screenshot $Path appears uniform; the framebuffer is likely blank"
    }
    finally {
        $bitmap.Dispose()
    }
}

# Returns the share of sampled pixels inside the rectangle that differ between two screenshots.
function Get-ChangedFraction {
    param(
        [Parameter(Mandatory)]
        [string] $BeforePath,

        [Parameter(Mandatory)]
        [string] $AfterPath,

        [Parameter(Mandatory)]
        [object] $Rect,

        [int] $Step = 8,

        [int] $Tolerance = 24
    )

    $before = [System.Drawing.Bitmap]::new($BeforePath)
    $after = [System.Drawing.Bitmap]::new($AfterPath)
    try {
        $left = [Math]::Max(0, [int] $Rect.Left)
        $top = [Math]::Max(0, [int] $Rect.Top)
        $right = [Math]::Min([Math]::Min($before.Width, $after.Width), [int] $Rect.Right)
        $bottom = [Math]::Min([Math]::Min($before.Height, $after.Height), [int] $Rect.Bottom)
        if ($right -le $left -or $bottom -le $top) {
            return 0.0
        }

        $total = 0
        $changed = 0
        for ($y = $top; $y -lt $bottom; $y += $Step) {
            for ($x = $left; $x -lt $right; $x += $Step) {
                $a = $before.GetPixel($x, $y)
                $b = $after.GetPixel($x, $y)
                $total++
                if ([Math]::Abs($a.R - $b.R) -gt $Tolerance -or
                    [Math]::Abs($a.G - $b.G) -gt $Tolerance -or
                    [Math]::Abs($a.B - $b.B) -gt $Tolerance) {
                    $changed++
                }
            }
        }

        return [double] $changed / [Math]::Max(1, $total)
    }
    finally {
        $before.Dispose()
        $after.Dispose()
    }
}

# Defines the window probe in the remote runspace, which lives in the RDP desktop session,
# so window handles and process session ids are the ones the user would see.
$remoteInit = {
    if (-not ('UniGetUISmoke.WindowProbe' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace UniGetUISmoke
{
    public sealed class WindowInfo
    {
        public long Handle;
        public int ProcessId;
        public string Title;
        public bool Owned;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static class WindowProbe
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const uint GW_OWNER = 4;
        private const int SW_MAXIMIZE = 3;

        public static List<WindowInfo> Find(int[] processIds)
        {
            var wanted = new HashSet<int>(processIds);
            var result = new List<WindowInfo>();
            EnumWindows((hWnd, lParam) =>
            {
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (!wanted.Contains((int)pid) || !IsWindowVisible(hWnd))
                {
                    return true;
                }

                var title = new StringBuilder(512);
                GetWindowText(hWnd, title, title.Capacity);
                RECT rect;
                GetWindowRect(hWnd, out rect);
                result.Add(new WindowInfo
                {
                    Handle = hWnd.ToInt64(),
                    ProcessId = (int)pid,
                    Title = title.ToString(),
                    Owned = GetWindow(hWnd, GW_OWNER) != IntPtr.Zero,
                    Left = rect.Left,
                    Top = rect.Top,
                    Right = rect.Right,
                    Bottom = rect.Bottom,
                });
                return true;
            }, IntPtr.Zero);
            return result;
        }

        public static bool MaximizeAndActivate(long handle)
        {
            var hWnd = new IntPtr(handle);
            ShowWindow(hWnd, SW_MAXIMIZE);
            return SetForegroundWindow(hWnd);
        }
    }
}
'@
    }
}

# UniGetUI shows its dialogs (ImmersiveDialog) as an overlay inside the main window, not as
# separate windows. The overlay's title TextBlock is only in the UI Automation tree while a
# dialog is open, so its presence and text tell which dialog is showing.
$remoteModalProbe = {
    param($Handle)

    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $automation = [System.Windows.Automation.AutomationElement]
    $root = $automation::FromHandle([IntPtr] [long] $Handle)
    $condition = [System.Windows.Automation.PropertyCondition]::new($automation::AutomationIdProperty, 'ModalTitle')
    $title = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $elementCount = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition).Count

    [pscustomobject]@{
        IsOpen = $null -ne $title
        Title = if ($null -ne $title) { $title.Current.Name } else { $null }
        AutomationElementCount = $elementCount
    }
}

$remoteProbe = {
    $sessionId = (Get-Process -Id $PID).SessionId
    $processes = @(Get-Process -Name 'UniGetUI' -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $sessionId })
    $processIds = [int[]] @($processes | ForEach-Object { $_.Id })
    $windows = @()
    if ($processIds.Count -gt 0) {
        $windows = @([UniGetUISmoke.WindowProbe]::Find($processIds))
    }

    [pscustomobject]@{
        SessionId = $sessionId
        ProcessIds = $processIds
        Windows = $windows
    }
}

# The main window is the largest visible, unowned, titled window.
function Get-MainWindow {
    param([Parameter(Mandatory)] [object] $Probe)

    return @($Probe.Windows) |
        Where-Object { $null -ne $_ -and -not $_.Owned -and -not [string]::IsNullOrWhiteSpace($_.Title) -and ($_.Right - $_.Left) -ge 400 -and ($_.Bottom - $_.Top) -ge 300 } |
        Sort-Object -Property { ($_.Right - $_.Left) * ($_.Bottom - $_.Top) } -Descending |
        Select-Object -First 1
}

if ($DesktopSize -notmatch '^(?<width>[1-9][0-9]*)[xX](?<height>[1-9][0-9]*)$') {
    throw "DesktopSize must use WxH format, got '$DesktopSize'"
}
$expectedWidth = [int] $Matches.width
$expectedHeight = [int] $Matches.height

New-Item -Path $ArtifactsDir -ItemType Directory -Force | Out-Null
Add-Type -AssemblyName System.Drawing
Import-Module AwakeCoding.PSRemoting -ErrorAction Stop

$psHostEndpoint = Get-Content -Path $PSHostEndpointPath -Raw | ConvertFrom-Json
$session = New-PSHostSession -HostName $psHostEndpoint.HostName -Port ([int] $psHostEndpoint.Port)
$dismissedDialogs = [System.Collections.Generic.List[object]]::new()

try {
    Invoke-Command -Session $session -ScriptBlock $remoteInit

    # Park the pointer in a corner so no hover effect differs between screenshots.
    Invoke-Agent mouse-move --x 1 --y ($expectedHeight - 1) | Out-Null
    $baselinePath = Save-Screenshot -Name 'desktop-before-unigetui.png'
    Test-PngScreenshot -Path $baselinePath -ExpectedWidth $expectedWidth -ExpectedHeight $expectedHeight

    # Settings are written before the first launch, as the user who runs UniGetUI. A boolean
    # setting is an empty file named after it in the configuration directory; a dictionary
    # setting is a JSON object in "<name>.json". Opt out of telemetry and mark the opt-in
    # banner as shown, as for a user who already declined it, and disable package managers
    # that would otherwise ask for missing dependencies at startup.
    $settings = Invoke-Command -Session $session -ArgumentList (, $DisabledManagers) -ScriptBlock {
        param([string[]] $Managers)

        $configurationDir = Join-Path $env:LOCALAPPDATA 'UniGetUI\Configuration'
        New-Item -Path $configurationDir -ItemType Directory -Force | Out-Null
        foreach ($setting in @('DisableTelemetry', 'ShownTelemetryBanner')) {
            New-Item -Path (Join-Path $configurationDir $setting) -ItemType File -Force | Out-Null
        }

        $disabled = [ordered]@{}
        foreach ($manager in $Managers) {
            $disabled[$manager] = $true
        }
        if ($disabled.Count -gt 0) {
            $disabled | ConvertTo-Json -Compress | Set-Content -Path (Join-Path $configurationDir 'DisabledManagers.json') -Encoding utf8NoBOM -NoNewline
        }

        [pscustomobject]@{
            ConfigurationDir = $configurationDir
            DisableTelemetry = Test-Path (Join-Path $configurationDir 'DisableTelemetry')
            DisabledManagers = @($disabled.Keys)
        }
    }
    if (-not $settings.DisableTelemetry) {
        throw "Could not disable UniGetUI telemetry in $($settings.ConfigurationDir)"
    }
    Write-Host "Disabled UniGetUI telemetry in $($settings.ConfigurationDir)"
    Write-Host "Disabled UniGetUI package managers: $(@($settings.DisabledManagers) -join ', ')"

    $launch = Invoke-Command -Session $session -ArgumentList $UniGetUIPath -ScriptBlock {
        param($Path)
        $process = Start-Process -FilePath $Path -WorkingDirectory (Split-Path -Path $Path -Parent) -PassThru
        [pscustomobject]@{
            ProcessId = $process.Id
            SessionId = $process.SessionId
            HostSessionId = (Get-Process -Id $PID).SessionId
        }
    }
    Write-Host "Launched UniGetUI: $($launch | ConvertTo-Json -Compress)"
    if ($launch.SessionId -ne $psHostEndpoint.SessionId) {
        throw "UniGetUI started in session $($launch.SessionId), not in the interactive session $($psHostEndpoint.SessionId)"
    }

    $deadline = (Get-Date).AddSeconds($WindowTimeoutSeconds)
    $mainWindow = $null
    $probe = $null
    do {
        Start-Sleep -Seconds 2
        $probe = Invoke-Command -Session $session -ScriptBlock $remoteProbe
        $mainWindow = Get-MainWindow -Probe $probe
    } while ($null -eq $mainWindow -and (Get-Date) -lt $deadline)

    if ($null -eq $mainWindow) {
        $probe | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $ArtifactsDir 'unigetui-window-probe.json') -Encoding utf8NoBOM
        throw "UniGetUI main window did not appear in session $($psHostEndpoint.SessionId) within $WindowTimeoutSeconds seconds"
    }
    Write-Host "UniGetUI main window appeared: '$($mainWindow.Title)' (pid $($mainWindow.ProcessId))"

    $activated = Invoke-Command -Session $session -ArgumentList $mainWindow.Handle -ScriptBlock {
        param($Handle)
        [UniGetUISmoke.WindowProbe]::MaximizeAndActivate([long] $Handle)
    }
    Write-Host "Maximized UniGetUI main window (SetForegroundWindow: $activated)"

    # First-run UI: startup checks open dialogs a little after the main window appears.
    # Missing package manager dependencies are expected on a fresh runner and are skipped
    # with Escape, as a user would. Any other dialog (a crash report, an integrity
    # violation) fails the test.
    # Their timing varies between runs, so wait until no dialog has shown for
    # $DialogSettleSeconds, and judge only a probe taken after the last Escape.
    $lastDialogAt = Get-Date
    $dialogDeadline = $lastDialogAt.AddSeconds($DialogTimeoutSeconds)
    $firstRunCaptured = $false
    $modal = $null
    do {
        Start-Sleep -Seconds 2
        $modal = Invoke-Command -Session $session -ArgumentList $mainWindow.Handle -ScriptBlock $remoteModalProbe
        if ($modal.IsOpen) {
            $lastDialogAt = Get-Date
            if (-not $firstRunCaptured) {
                Save-Screenshot -Name 'unigetui-first-run-dialog.png' | Out-Null
                $firstRunCaptured = $true
            }

            if ($modal.Title -notlike 'Missing dependency*') {
                throw "UniGetUI shows an unexpected dialog: '$($modal.Title)'"
            }

            Write-Host "Skipping UniGetUI dialog: '$($modal.Title)'"
            $dismissedDialogs.Add($modal.Title)
            Send-Key -Scancode '0x01'
        }
    } while (($modal.IsOpen -or ((Get-Date) - $lastDialogAt).TotalSeconds -lt $DialogSettleSeconds) -and (Get-Date) -lt $dialogDeadline)

    Write-Host "UI Automation elements in the UniGetUI main window: $($modal.AutomationElementCount)"
    if ($modal.IsOpen) {
        throw "UniGetUI dialog '$($modal.Title)' is still open after $DialogTimeoutSeconds seconds"
    }

    $screenshotPath = Join-Path $ArtifactsDir 'unigetui-main-window.png'
    $deadline = (Get-Date).AddSeconds($ScreenshotTimeoutSeconds)
    $clicked = $false
    $changedFraction = 0.0
    do {
        Start-Sleep -Seconds 3
        $probe = Invoke-Command -Session $session -ScriptBlock $remoteProbe
        $mainWindow = Get-MainWindow -Probe $probe
        if ($null -eq $mainWindow) {
            throw 'UniGetUI main window disappeared before the screenshot check'
        }

        Save-Screenshot -Name 'unigetui-main-window.png' | Out-Null
        $changedFraction = Get-ChangedFraction -BeforePath $baselinePath -AfterPath $screenshotPath -Rect $mainWindow
        Write-Host ("{0:P0} of the UniGetUI window area changed from the bare desktop" -f $changedFraction)

        # A window started from a background process may not be allowed to the foreground;
        # a real click on its title bar raises it.
        if ($changedFraction -lt $MinChangedFraction -and -not $clicked) {
            $x = [int] ($mainWindow.Left + ($mainWindow.Right - $mainWindow.Left) * 0.7)
            $y = [int] ([Math]::Max(0, $mainWindow.Top) + 12)
            Invoke-Agent mouse-move --x $x --y $y | Out-Null
            Invoke-Agent mouse-button --button left --pressed true | Out-Null
            Invoke-Agent mouse-button --button left --pressed false | Out-Null
            Invoke-Agent mouse-move --x 1 --y ($expectedHeight - 1) | Out-Null
            $clicked = $true
        }
    } while ($changedFraction -lt $MinChangedFraction -and (Get-Date) -lt $deadline)

    Test-PngScreenshot -Path $screenshotPath -ExpectedWidth $expectedWidth -ExpectedHeight $expectedHeight
    if ($changedFraction -lt $MinChangedFraction) {
        throw ("UniGetUI window is not visible in {0}: only {1:P0} of its area changed from the bare desktop" -f $screenshotPath, $changedFraction)
    }

    $result = [pscustomobject]@{
        UniGetUIPath = $UniGetUIPath
        ProcessIds = $probe.ProcessIds
        SessionId = $probe.SessionId
        MainWindow = $mainWindow
        DismissedDialogs = $dismissedDialogs
        ChangedFraction = [Math]::Round($changedFraction, 3)
        BaselineScreenshot = $baselinePath
        Screenshot = $screenshotPath
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $ArtifactsDir 'unigetui-smoke.json') -Encoding utf8NoBOM
    $result | ConvertTo-Json -Compress -Depth 6
}
finally {
    Remove-PSSession -Session $session -ErrorAction SilentlyContinue
}
