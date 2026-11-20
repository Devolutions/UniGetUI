#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PublishDir,

    [Parameter(Mandatory)]
    [string] $RuntimeIdentifier,

    [string] $DepsJson,

    [string] $Version,

    [string] $OutputDir = $PublishDir
)

$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ToolRoot = Join-Path $PSScriptRoot 'third-party-licenses'
$ConfigDir = Join-Path $ToolRoot 'config'
$PublishDir = (Resolve-Path $PublishDir).Path
$IsWindowsTarget = $RuntimeIdentifier -like 'win-*'

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path
$NoticesPath = Join-Path $OutputDir 'THIRD-PARTY-NOTICES.txt'
Remove-Item -LiteralPath $NoticesPath, ([IO.Path]::ChangeExtension($NoticesPath, '.json')) -ErrorAction SilentlyContinue

if (-not $DepsJson) {
    $DepsJson = Get-ChildItem (Join-Path $RepoRoot 'src/UniGetUI.Avalonia/bin') -Recurse -File -Filter 'UniGetUI.deps.json' |
        Where-Object { $_.Directory.Name -eq $RuntimeIdentifier -and $_.FullName -match '[\\/]Release[\\/]' } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $DepsJson) {
        throw "No Release UniGetUI.deps.json found for $RuntimeIdentifier under src/UniGetUI.Avalonia/bin. Build or publish UniGetUI for that runtime first."
    }
}

if (-not $Version) {
    $CoreData = Get-Content (Join-Path $RepoRoot 'src/UniGetUI.Core.Data/CoreData.cs') -Raw
    if ($CoreData -notmatch 'VersionName\s*=\s*"([^"]+)"') {
        throw 'Could not read VersionName from CoreData.cs.'
    }
    $Version = $Matches[1]
}

$PackageVersions = @{}
$Projects = Get-ChildItem (Join-Path $RepoRoot 'src') -Directory | ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -File -Filter '*.csproj' }
foreach ($Project in $Projects) {
    $Content = Get-Content -LiteralPath $Project.FullName -Raw
    foreach ($Match in [regex]::Matches($Content, '<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"')) {
        $Package = $Match.Groups[1].Value
        if (-not $PackageVersions.ContainsKey($Package)) { $PackageVersions[$Package] = [Collections.Generic.HashSet[string]]::new() }
        [void]$PackageVersions[$Package].Add($Match.Groups[2].Value)
    }
}

function Get-PackageVersion([string] $Package) {
    $Versions = @($PackageVersions[$Package])
    if ($Versions.Count -eq 0) { throw "No PackageReference to $Package found under src." }
    if ($Versions.Count -gt 1) { throw "$Package is referenced at several versions under src: $($Versions -join ', ')." }
    return $Versions[0]
}

$ArtifactPaths = @($DepsJson)
if ($IsWindowsTarget) {
    $PingetCrates = Join-Path $ConfigDir 'pinget-crates.json'
    $PingetVersion = Get-PackageVersion 'Devolutions.Pinget.Cli.Rust'
    $CratesVersion = (Get-Content -LiteralPath $PingetCrates -Raw | ConvertFrom-Json).appVersion
    if ($PingetVersion -ne $CratesVersion) {
        throw "config/pinget-crates.json lists the crates of pinget $CratesVersion, but UniGetUI ships pinget $PingetVersion. Run scripts/third-party-licenses/Update-PingetCrates.ps1."
    }
    $ArtifactPaths += $PingetCrates
}

$ManualSources = @(Join-Path $ConfigDir 'manual-components.json')
if ($IsWindowsTarget) { $ManualSources += Join-Path $ConfigDir 'manual-components.windows.json' }

$ManualPaths = @()
$ResolvedManual = @()
try {
    foreach ($Source in $ManualSources) {
        $Document = Get-Content -LiteralPath $Source -Raw | ConvertFrom-Json -AsHashtable
        $Pinned = @($Document.components | Where-Object { $_.versionFromPackageReference })
        if (-not $Pinned) {
            $ManualPaths += $Source
            continue
        }

        foreach ($Entry in $Pinned) {
            $Entry.version = Get-PackageVersion $Entry.versionFromPackageReference
            $Entry.Remove('versionFromPackageReference')
        }
        $Resolved = Join-Path $ConfigDir ".resolved-$([guid]::NewGuid().ToString('N')).json"
        $Document | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Resolved -Encoding utf8NoBOM
        $ResolvedManual += $Resolved
        $ManualPaths += $Resolved
    }

    Write-Host "deps.json : $DepsJson"
    Write-Host "Binaries  : $PublishDir"
    Write-Host "Version   : $Version"

    & (Join-Path $ToolRoot 'Invoke-ThirdPartyLicenses.ps1') `
        -AppName 'UniGetUI' `
        -AppVersion $Version `
        -ArtifactPath $ArtifactPaths `
        -BinariesPath $PublishDir `
        -ManualComponentsPath $ManualPaths `
        -OutputPath $NoticesPath `
        -Check `
        -CheckOutputDirectory $OutputDir `
        -Json `
        -RepoRoot $RepoRoot
    $ToolExitCode = $LASTEXITCODE
}
finally {
    $ResolvedManual | ForEach-Object { Remove-Item -LiteralPath $_ -ErrorAction SilentlyContinue }
}

if (-not (Test-Path -LiteralPath $NoticesPath -PathType Leaf)) {
    throw "The notices file was not written to $NoticesPath."
}

exit $ToolExitCode
