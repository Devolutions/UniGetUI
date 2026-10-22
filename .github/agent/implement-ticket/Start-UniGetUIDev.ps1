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
& (Join-Path $PSScriptRoot 'Stop-UniGetUIDev.ps1') | Out-Null

# Restoring evaluates every project, so it is skipped while the project files and the restore
# results are the same as after this script's last restore. The restore results matter too: a
# Release restore (such as a NativeAOT publish) rewrites the same project.assets.json files
# without Debug-only packages. The stamp is under obj, which git ignores.
function Get-RestoreState {
    $srcDir = Join-Path $repoRoot 'src'
    $inputs = @(Get-ChildItem -Path $srcDir -Recurse -File -Include '*.csproj', '*.props', '*.targets' |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) +
        @(Get-Item -Path (Join-Path $repoRoot 'global.json'))
    $inputHashes = $inputs | Sort-Object -Property FullName | Get-FileHash -Algorithm SHA256 |
        ForEach-Object { "$($_.Path)=$($_.Hash)" }
    $assets = Get-ChildItem -Path $srcDir -Directory | ForEach-Object { Join-Path $_.FullName 'obj\project.assets.json' } |
        Where-Object { Test-Path -LiteralPath $_ } | Sort-Object |
        ForEach-Object { "$_@$((Get-Item -LiteralPath $_).LastWriteTimeUtc.Ticks)" }
    return (@($inputHashes) + @($assets)) -join "`n"
}

$restoreStamp = Join-Path $repoRoot "src\UniGetUI.Avalonia\obj\agent-restore-$Configuration.txt"
$restoreArgs = @()
if ((Test-Path -LiteralPath $restoreStamp) -and (Get-Content -LiteralPath $restoreStamp -Raw) -eq (Get-RestoreState)) {
    $restoreArgs = @('--no-restore')
}

Write-Host "Building $Configuration (log: $buildLog)"
# Serial build, as in build-release.yml: parallel project builds can race on the same output
# file on CI runners (CS2012). Unlike release builds, it keeps the shared compiler server,
# which roughly halves the agent's repeated incremental builds.
& dotnet build $project -c $Configuration -p:Platform=x64 -r win-x64 -p:BuildInParallel=false -m:1 @restoreArgs --nologo -v q -clp:ErrorsOnly *> $buildLog
if ($LASTEXITCODE -ne 0) {
    Get-Content -Path $buildLog -Tail 60 | Write-Host
    throw "dotnet build failed with exit code $LASTEXITCODE; full log in $buildLog"
}
Set-Content -LiteralPath $restoreStamp -Value (Get-RestoreState) -NoNewline -Encoding utf8NoBOM

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
