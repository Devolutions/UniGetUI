# Helpers for driving the live RDP desktop that the desktop-session action set up.
# Everything is read from the DESKTOP_SESSION_* environment variables that action exports,
# so a later step only needs:  Import-Module $env:DESKTOP_SESSION_MODULE

Set-StrictMode -Version Latest

$script:DesktopPSSession = $null

function Get-DesktopSessionSetting {
    param([Parameter(Mandatory)] [string] $Name)

    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "$Name is not set; run the desktop-session action earlier in this job"
    }
    return $value
}

# Returns the desktop width and height, and the PSHost endpoint (which includes the
# Windows session id of the interactive desktop).
function Get-DesktopSessionInfo {
    $size = Get-DesktopSessionSetting -Name 'DESKTOP_SESSION_SIZE'
    if ($size -notmatch '^(?<width>[1-9][0-9]*)[xX](?<height>[1-9][0-9]*)$') {
        throw "DESKTOP_SESSION_SIZE must use WxH format, got '$size'"
    }
    $width = [int] $Matches.width
    $height = [int] $Matches.height
    $endpoint = Get-Content -Path (Get-DesktopSessionSetting -Name 'DESKTOP_SESSION_PSHOST_ENDPOINT') -Raw | ConvertFrom-Json

    return [pscustomobject]@{
        Width = $width
        Height = $height
        SessionId = [int] $endpoint.SessionId
        UserName = $endpoint.UserName
        ArtifactsDir = Get-DesktopSessionSetting -Name 'DESKTOP_SESSION_ARTIFACTS'
    }
}

# Runs one ironrdp-agent command against the session. A failing native command does not
# throw in PowerShell, so the exit code is checked here.
function Invoke-DesktopAgent {
    param(
        [Parameter(ValueFromRemainingArguments)]
        [string[]] $Arguments
    )

    $agent = Get-DesktopSessionSetting -Name 'DESKTOP_SESSION_AGENT'
    $endpoint = Get-DesktopSessionSetting -Name 'DESKTOP_SESSION_ENDPOINT'
    $output = & $agent --endpoint $endpoint @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $message = ($output | Out-String).Trim()
        throw "ironrdp-agent $($Arguments -join ' ') failed: $message"
    }
    return $output
}

# Saves a PNG of the whole desktop. A relative name is saved in the artifacts directory.
function Save-DesktopScreenshot {
    param([Parameter(Mandatory)] [string] $Name)

    $path = if ([System.IO.Path]::IsPathRooted($Name)) {
        $Name
    }
    else {
        Join-Path (Get-DesktopSessionSetting -Name 'DESKTOP_SESSION_ARTIFACTS') $Name
    }
    Invoke-DesktopAgent screenshot $path | Out-Null
    return $path
}

# Throws unless the screenshot has the desktop size and is not a single uniform color.
function Test-DesktopScreenshot {
    param([Parameter(Mandatory)] [string] $Path)

    Add-Type -AssemblyName System.Drawing
    $info = Get-DesktopSessionInfo
    $bitmap = [System.Drawing.Bitmap]::new($Path)
    try {
        if ($bitmap.Width -ne $info.Width -or $bitmap.Height -ne $info.Height) {
            throw "Screenshot is $($bitmap.Width)x$($bitmap.Height), expected $($info.Width)x$($info.Height)"
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

# Returns the share (0 to 1) of sampled pixels that differ between two screenshots, inside
# -Rect (any object with Left, Top, Right and Bottom, such as a Get-DesktopWindow result) or
# over the whole image.
function Get-DesktopScreenshotChange {
    param(
        [Parameter(Mandatory)]
        [string] $BeforePath,

        [Parameter(Mandatory)]
        [string] $AfterPath,

        [object] $Rect,

        [int] $Step = 8,

        [int] $Tolerance = 24
    )

    Add-Type -AssemblyName System.Drawing
    $before = [System.Drawing.Bitmap]::new($BeforePath)
    $after = [System.Drawing.Bitmap]::new($AfterPath)
    try {
        $width = [Math]::Min($before.Width, $after.Width)
        $height = [Math]::Min($before.Height, $after.Height)
        $left = 0
        $top = 0
        $right = $width
        $bottom = $height
        if ($null -ne $Rect) {
            $left = [Math]::Max(0, [int] $Rect.Left)
            $top = [Math]::Max(0, [int] $Rect.Top)
            $right = [Math]::Min($width, [int] $Rect.Right)
            $bottom = [Math]::Min($height, [int] $Rect.Bottom)
        }
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

# Presses the scancodes in order and releases them in reverse, so several codes make a
# chord: Send-DesktopKey 0xE05B, 0x13 is Win+R, Send-DesktopKey 0x01 is Escape.
function Send-DesktopKey {
    param(
        [Parameter(Mandatory)]
        [string[]] $Scancode
    )

    foreach ($code in $Scancode) {
        Invoke-DesktopAgent key-scancode --scancode $code --pressed true | Out-Null
    }
    for ($i = $Scancode.Count - 1; $i -ge 0; $i--) {
        Invoke-DesktopAgent key-scancode --scancode $Scancode[$i] --pressed false | Out-Null
    }
}

# Types text as Unicode key events, independent of the keyboard layout.
function Send-DesktopText {
    param([Parameter(Mandatory)] [string] $Text)

    foreach ($character in $Text.ToCharArray()) {
        Invoke-DesktopAgent key-unicode --char ([string] $character) --pressed true | Out-Null
        Invoke-DesktopAgent key-unicode --char ([string] $character) --pressed false | Out-Null
    }
}

function Move-DesktopMouse {
    param(
        [Parameter(Mandatory)] [int] $X,
        [Parameter(Mandatory)] [int] $Y
    )

    Invoke-DesktopAgent mouse-move --x $X --y $Y | Out-Null
}

function Invoke-DesktopClick {
    param(
        [Parameter(Mandatory)] [int] $X,
        [Parameter(Mandatory)] [int] $Y,
        [ValidateSet('left', 'right', 'middle')] [string] $Button = 'left'
    )

    Move-DesktopMouse -X $X -Y $Y
    Invoke-DesktopAgent mouse-button --button $Button --pressed true | Out-Null
    Invoke-DesktopAgent mouse-button --button $Button --pressed false | Out-Null
}

# A PowerShell remoting session whose runspace lives inside the interactive desktop, so
# processes it starts show up on that desktop. It is opened once and reused.
function Get-DesktopPSSession {
    if ($null -ne $script:DesktopPSSession -and [string] $script:DesktopPSSession.State -eq 'Opened') {
        return $script:DesktopPSSession
    }

    Import-Module AwakeCoding.PSRemoting -ErrorAction Stop
    $endpoint = Get-Content -Path (Get-DesktopSessionSetting -Name 'DESKTOP_SESSION_PSHOST_ENDPOINT') -Raw | ConvertFrom-Json
    $script:DesktopPSSession = New-PSHostSession -HostName $endpoint.HostName -Port ([int] $endpoint.Port)
    return $script:DesktopPSSession
}

function Close-DesktopPSSession {
    if ($null -ne $script:DesktopPSSession) {
        Remove-PSSession -Session $script:DesktopPSSession -ErrorAction SilentlyContinue
        $script:DesktopPSSession = $null
    }
}

# Runs a script block inside the interactive desktop session. Pass an array as a single
# argument with -ArgumentList (, $array).
function Invoke-DesktopCommand {
    param(
        [Parameter(Mandatory)]
        [scriptblock] $ScriptBlock,

        [object[]] $ArgumentList = @()
    )

    Invoke-Command -Session (Get-DesktopPSSession) -ScriptBlock $ScriptBlock -ArgumentList $ArgumentList
}

# Runs a command in the desktop session over the NOW protocol: Devolutions Agent's RDP
# extension runs it as the session's user, and its output comes back over the RDP virtual
# channel. Needs the desktop-session action's now-channel input (DESKTOP_SESSION_NOW=1).
# Throws when the command fails; returns its output.
function Invoke-DesktopNowCommand {
    param(
        [Parameter(Mandatory)]
        [string] $Command,

        [ValidateSet('powershell', 'pwsh')]
        [string] $Shell = 'powershell'
    )

    if ($env:DESKTOP_SESSION_NOW -ne '1') {
        throw 'The NOW channel is not available in this desktop session (desktop-session now-channel input is off)'
    }
    Invoke-DesktopAgent now $Shell $Command
}

# Starts a program on the desktop and returns its process id and Windows session id.
function Start-DesktopProcess {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [string[]] $ArgumentList = @()
    )

    Invoke-DesktopCommand -ArgumentList $FilePath, (, $ArgumentList) -ScriptBlock {
        param($Path, [string[]] $Arguments)

        $startParameters = @{
            FilePath = $Path
            WorkingDirectory = Split-Path -Path $Path -Parent
            PassThru = $true
        }
        if ($Arguments.Count -gt 0) {
            $startParameters['ArgumentList'] = $Arguments
        }
        $process = Start-Process @startParameters
        [pscustomobject]@{
            ProcessId = $process.Id
            SessionId = $process.SessionId
        }
    }
}

$script:WindowProbeType = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopSession
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

        public static bool Show(long handle, int cmdShow)
        {
            var hWnd = new IntPtr(handle);
            ShowWindow(hWnd, cmdShow);
            return SetForegroundWindow(hWnd);
        }
    }
}
'@

# Lists the visible top-level windows of processes with this name in the desktop session.
# With -Main, returns only the largest visible, unowned, titled window, if any.
function Get-DesktopWindow {
    param(
        [Parameter(Mandatory)]
        [string] $ProcessName,

        [switch] $Main
    )

    $windows = @(Invoke-DesktopCommand -ArgumentList $ProcessName, $script:WindowProbeType -ScriptBlock {
            param($Name, $TypeDefinition)

            if (-not ('DesktopSession.WindowProbe' -as [type])) {
                Add-Type -TypeDefinition $TypeDefinition
            }

            $sessionId = (Get-Process -Id $PID).SessionId
            $processIds = [int[]] @(Get-Process -Name $Name -ErrorAction SilentlyContinue |
                    Where-Object { $_.SessionId -eq $sessionId } |
                    ForEach-Object { $_.Id })
            if ($processIds.Count -gt 0) {
                [DesktopSession.WindowProbe]::Find($processIds)
            }
        } | Where-Object { $null -ne $_ })

    if (-not $Main) {
        return $windows
    }

    return $windows |
        Where-Object { -not $_.Owned -and -not [string]::IsNullOrWhiteSpace($_.Title) -and ($_.Right - $_.Left) -ge 400 -and ($_.Bottom - $_.Top) -ge 300 } |
        Sort-Object -Property { ($_.Right - $_.Left) * ($_.Bottom - $_.Top) } -Descending |
        Select-Object -First 1
}

# Restores (or maximizes) a window and asks for it to become the foreground window.
# Returns whether SetForegroundWindow succeeded; a real click on the window also raises it.
function Show-DesktopWindow {
    param(
        [Parameter(Mandatory)]
        [long] $Handle,

        [switch] $Maximize
    )

    # SW_MAXIMIZE = 3, SW_RESTORE = 9
    $command = if ($Maximize) { 3 } else { 9 }
    Invoke-DesktopCommand -ArgumentList $Handle, $command, $script:WindowProbeType -ScriptBlock {
        param($WindowHandle, $ShowCommand, $TypeDefinition)

        if (-not ('DesktopSession.WindowProbe' -as [type])) {
            Add-Type -TypeDefinition $TypeDefinition
        }
        [DesktopSession.WindowProbe]::Show([long] $WindowHandle, [int] $ShowCommand)
    }
}

Export-ModuleMember -Function @(
    'Get-DesktopSessionInfo',
    'Invoke-DesktopAgent',
    'Save-DesktopScreenshot',
    'Test-DesktopScreenshot',
    'Get-DesktopScreenshotChange',
    'Send-DesktopKey',
    'Send-DesktopText',
    'Move-DesktopMouse',
    'Invoke-DesktopClick',
    'Get-DesktopPSSession',
    'Close-DesktopPSSession',
    'Invoke-DesktopCommand',
    'Invoke-DesktopNowCommand',
    'Start-DesktopProcess',
    'Get-DesktopWindow',
    'Show-DesktopWindow'
)
