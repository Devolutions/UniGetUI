# Builds UniGetUI from the working tree (Debug, win-x64) and launches it on the live desktop,
# replacing any running instance. It goes through the unigetui-desktop action's
# Start-UniGetUI.ps1, so telemetry and the Cargo manager are off, first-run dialogs are
# skipped, and the main window ends up maximized in the foreground.
#
# Prints the build log tail on failure, and a JSON summary of the running window on success.
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    # Build only, without launching.
    [switch] $NoLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$project = Join-Path $repoRoot 'src\UniGetUI.Avalonia\UniGetUI.Avalonia.csproj'
$logDir = if ([string]::IsNullOrWhiteSpace($env:DESKTOP_SESSION_ARTIFACTS)) { Join-Path $repoRoot 'artifacts' } else { $env:DESKTOP_SESSION_ARTIFACTS }
New-Item -Path $logDir -ItemType Directory -Force | Out-Null
$buildLog = Join-Path $logDir 'unigetui-dev-build.log'

# A running instance locks its output files.
Get-Process -Name 'UniGetUI' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Host "Building $Configuration (log: $buildLog)"
# Serial build, as in build-release.yml: parallel project builds can race on the same output
# file on CI runners (CS2012).
& dotnet build $project -c $Configuration -p:Platform=x64 -r win-x64 -p:UseSharedCompilation=false -p:BuildInParallel=false -m:1 --nologo -v q -clp:ErrorsOnly *> $buildLog
if ($LASTEXITCODE -ne 0) {
    Get-Content -Path $buildLog -Tail 60 | Write-Host
    throw "dotnet build failed with exit code $LASTEXITCODE; full log in $buildLog"
}

$exe = Get-ChildItem -Path (Join-Path $repoRoot "src\UniGetUI.Avalonia\bin\x64\$Configuration") -Filter 'UniGetUI.exe' -Recurse -File |
    Where-Object { $_.DirectoryName -like '*\win-x64' } |
    Sort-Object -Property LastWriteTime -Descending |
    Select-Object -First 1
if ($null -eq $exe) {
    throw "dotnet build succeeded but UniGetUI.exe was not found under src\UniGetUI.Avalonia\bin\x64\$Configuration"
}
Write-Host "Built $($exe.FullName)"

if ($NoLaunch) {
    return
}

& (Join-Path $repoRoot '.github\actions\unigetui-desktop\Start-UniGetUI.ps1') -UniGetUIPath $exe.FullName | Out-Host

Import-Module $env:DESKTOP_SESSION_MODULE -Force
try {
    $window = Get-DesktopWindow -ProcessName 'UniGetUI' -Main
    [pscustomobject]@{
        ExePath = $exe.FullName
        ProcessId = $window.ProcessId
        WindowHandle = $window.Handle
        Title = $window.Title
        Left = $window.Left
        Top = $window.Top
        Right = $window.Right
        Bottom = $window.Bottom
    } | ConvertTo-Json -Compress
}
finally {
    Close-DesktopPSSession
}
