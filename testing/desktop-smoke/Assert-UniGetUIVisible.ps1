# Checks that the UniGetUI main window is really on screen in the desktop session: it must
# exist in that session, and enough of its area must differ from the bare-desktop screenshot
# the unigetui-desktop action took before launching it.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BaselineScreenshot,

    [int] $TimeoutSeconds = 60,

    # Share of sampled pixels inside the UniGetUI window that must differ from the bare desktop.
    [double] $MinChangedFraction = 0.25
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module $env:DESKTOP_SESSION_MODULE -Force

$desktop = Get-DesktopSessionInfo
$screenshotPath = Join-Path $desktop.ArtifactsDir 'unigetui-main-window.png'

try {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $clicked = $false
    $changedFraction = 0.0
    do {
        $mainWindow = Get-DesktopWindow -ProcessName 'UniGetUI' -Main
        if ($null -eq $mainWindow) {
            throw "UniGetUI has no main window in desktop session $($desktop.SessionId)"
        }

        Save-DesktopScreenshot -Name $screenshotPath | Out-Null
        $changedFraction = Get-DesktopScreenshotChange -BeforePath $BaselineScreenshot -AfterPath $screenshotPath -Rect $mainWindow
        Write-Host ("{0:P0} of the UniGetUI window area changed from the bare desktop" -f $changedFraction)

        # A window started from a background process may not be allowed to the foreground;
        # a real click on its title bar raises it.
        if ($changedFraction -lt $MinChangedFraction) {
            if (-not $clicked) {
                $x = [int] ($mainWindow.Left + ($mainWindow.Right - $mainWindow.Left) * 0.7)
                $y = [int] ([Math]::Max(0, $mainWindow.Top) + 12)
                Invoke-DesktopClick -X $x -Y $y
                Move-DesktopMouse -X 1 -Y ($desktop.Height - 1)
                $clicked = $true
            }
            Start-Sleep -Seconds 3
        }
    } while ($changedFraction -lt $MinChangedFraction -and (Get-Date) -lt $deadline)

    Test-DesktopScreenshot -Path $screenshotPath
    if ($changedFraction -lt $MinChangedFraction) {
        throw ("UniGetUI window is not visible in {0}: only {1:P0} of its area changed from the bare desktop" -f $screenshotPath, $changedFraction)
    }

    $result = [pscustomobject]@{
        SessionId = $desktop.SessionId
        MainWindow = $mainWindow
        ChangedFraction = [Math]::Round($changedFraction, 3)
        BaselineScreenshot = $BaselineScreenshot
        Screenshot = $screenshotPath
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $desktop.ArtifactsDir 'unigetui-smoke.json') -Encoding utf8NoBOM
    $result | ConvertTo-Json -Compress -Depth 6
}
finally {
    Close-DesktopPSSession
}
