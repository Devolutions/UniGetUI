#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds UniGetUI, produces the published output, and packages artifacts.

.PARAMETER Configuration
    Build configuration (Debug or Release). Default: Release.

.PARAMETER Platform
    Target platform. Default: x64.

.PARAMETER OutputPath
    Directory for final packaged artifacts (zip, installer). Default: ./output

.PARAMETER SkipTests
    Skip running dotnet test before build.

.PARAMETER SkipInstaller
    Skip building the Inno Setup installer.

.PARAMETER Version
    Version string to stamp into the build (e.g. "3.3.7"). If not provided,
    the current version from SharedAssemblyInfo.cs is used.
#>

[CmdletBinding()]
param(
    [string] $Configuration = "Release",
    [string] $Platform = "x64",
    [string] $OutputPath = (Join-Path $PSScriptRoot ".." "output"),
    [switch] $SkipTests,
    [switch] $SkipInstaller,
    [string] $Version
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$SrcDir = Join-Path $RepoRoot "src"
$WindowsSolution = Join-Path $SrcDir "UniGetUI.Windows.slnx"
$PublishProject = Join-Path $SrcDir "UniGetUI" "UniGetUI.csproj"
$BinDir = Join-Path $RepoRoot "unigetui_bin"
$BuildPropsPath = Join-Path $SrcDir "Directory.Build.props"
[xml] $BuildProps = Get-Content $BuildPropsPath
$PortableTargetFramework = @($BuildProps.Project.PropertyGroup | Where-Object { $_.PortableTargetFramework } | Select-Object -First 1).PortableTargetFramework
$WindowsTargetPlatformVersion = @($BuildProps.Project.PropertyGroup | Where-Object { $_.WindowsTargetPlatformVersion } | Select-Object -First 1).WindowsTargetPlatformVersion

if ([string]::IsNullOrWhiteSpace($PortableTargetFramework) -or [string]::IsNullOrWhiteSpace($WindowsTargetPlatformVersion)) {
    throw "Could not resolve the target framework from $BuildPropsPath"
}

$TargetFramework = "$PortableTargetFramework-windows$WindowsTargetPlatformVersion"
$PublishDir = Join-Path $SrcDir "UniGetUI" "bin" $Platform $Configuration $TargetFramework "win-$Platform" "publish"

# --- Version stamping ---
if ($Version) {
    Write-Host "Stamping version: $Version"
    & (Join-Path $PSScriptRoot "set-version.ps1") -Version $Version
}

# --- Read version from SharedAssemblyInfo.cs ---
$AssemblyInfoPath = Join-Path $SrcDir "SharedAssemblyInfo.cs"
$VersionMatch = Select-String -Path $AssemblyInfoPath -Pattern 'AssemblyInformationalVersion\("([^"]+)"\)'
$PackageVersion = if ($VersionMatch) { $VersionMatch.Matches[0].Groups[1].Value } else { "0.0.0" }
Write-Host "Building UniGetUI version: $PackageVersion"

# --- Test ---
if (-not $SkipTests) {
    Write-Host "`n=== Running tests ===" -ForegroundColor Cyan
    dotnet test $WindowsSolution --verbosity q --nologo --ignore-failed-sources /p:Platform=$Platform
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed with exit code $LASTEXITCODE"
    }
}

# --- Build / Publish ---
Write-Host "`n=== Publishing $Configuration|$Platform ===" -ForegroundColor Cyan
dotnet clean $WindowsSolution -v m --nologo /p:Platform=$Platform

dotnet publish $PublishProject /noLogo /p:Configuration=$Configuration /p:Platform=$Platform --ignore-failed-sources -v m
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish WinUI failed with exit code $LASTEXITCODE"
}

# --- Stage binaries ---
if (Test-Path $BinDir) { Remove-Item $BinDir -Recurse -Force }
New-Item $BinDir -ItemType Directory | Out-Null
# Move published output into unigetui_bin
Get-ChildItem $PublishDir | Move-Item -Destination $BinDir -Force

# Keep smaller symbols for useful local crash source information, and prune oversized ones.
$MaxShippedPdbSizeBytes = 1MB

$PdbsToRemove = Get-ChildItem $BinDir -Filter "*.pdb" -File | Where-Object {
    $_.Length -gt $MaxShippedPdbSizeBytes
}

if ($PdbsToRemove.Count -gt 0) {
    $RemovedPdbBytes = ($PdbsToRemove | Measure-Object -Property Length -Sum).Sum
    $PdbsToRemove | Remove-Item -Force
    Write-Host ("Removed {0} oversized PDBs above {1:N2} MiB ({2:N2} MiB total)." -f $PdbsToRemove.Count, ($MaxShippedPdbSizeBytes / 1MB), ($RemovedPdbBytes / 1MB))
}

# WingetUI.exe alias for backward compat
Copy-Item (Join-Path $BinDir "UniGetUI.exe") (Join-Path $BinDir "WingetUI.exe") -Force

# --- Package output ---
if (Test-Path $OutputPath) { Remove-Item $OutputPath -Recurse -Force }
New-Item $OutputPath -ItemType Directory | Out-Null

$ZipPath = Join-Path $OutputPath "UniGetUI.$Platform.zip"
Write-Host "`n=== Refreshing integrity tree before zip packaging ===" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "refresh-integrity-tree.ps1") -Path $BinDir -FailOnUnexpectedFiles

Write-Host "`n=== Creating zip: $ZipPath ===" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $BinDir "*") -DestinationPath $ZipPath -CompressionLevel Optimal

# --- Installer (Inno Setup) ---
if (-not $SkipInstaller) {
    $IsccPath = $null
    # Search common install locations
    foreach ($candidate in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )) {
        if (Test-Path $candidate) { $IsccPath = $candidate; break }
    }

    if ($IsccPath) {
        Write-Host "`n=== Building installer ===" -ForegroundColor Cyan
        $InstallerBaseName = "UniGetUI.Installer.$Platform"
        $IssPath = Join-Path $RepoRoot "UniGetUI.iss"
        $IssContent = Get-Content $IssPath -Raw

        Write-Host "`n=== Refreshing integrity tree before installer packaging ===" -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot "refresh-integrity-tree.ps1") -Path $BinDir -FailOnUnexpectedFiles

        try {
            $IssContentNoSign = $IssContent -Replace '(?m)^SignTool=.*$', '; SignTool=azsign (disabled for local build)'
            $IssContentNoSign = $IssContentNoSign -Replace '(?m)^SignedUninstaller=yes', 'SignedUninstaller=no'
            Set-Content $IssPath $IssContentNoSign -NoNewline

            & $IsccPath $IssPath /F"$InstallerBaseName" /O"$OutputPath"
            if ($LASTEXITCODE -ne 0) {
                throw "Inno Setup failed with exit code $LASTEXITCODE"
            }
        }
        finally {
            Set-Content $IssPath $IssContent -NoNewline
        }
    } else {
        Write-Warning "Inno Setup 6 (ISCC.exe) not found — skipping installer build."
    }
}

# --- Checksums ---
Write-Host "`n=== Checksums ===" -ForegroundColor Cyan
$ChecksumFile = Join-Path $OutputPath "checksums.$Platform.txt"
Get-ChildItem $OutputPath -File | Where-Object { $_.Name -notlike "checksums.*.txt" } | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    "$hash  $($_.Name)" | Tee-Object -FilePath $ChecksumFile -Append
}

# --- Cleanup ---
if (Test-Path $BinDir) { Remove-Item $BinDir -Recurse -Force }

Write-Host "`n=== Build complete ===" -ForegroundColor Green
Write-Host "Artifacts in: $OutputPath"
