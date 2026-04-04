#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Creates a .deb or .rpm package from a pre-built binary directory.

.DESCRIPTION
    Builds a native Linux package without requiring Ruby/fpm.
    .deb is assembled with tar and ar (binutils).
    .rpm is built with rpmbuild (rpm-build).

.PARAMETER PackageType
    Package format to produce: 'deb' or 'rpm'.

.PARAMETER SourceDir
    Directory containing the pre-built binaries to package.

.PARAMETER OutputPath
    Full path to the output package file (e.g. output/unigetui.deb).

.PARAMETER Version
    Package version string, already formatted for the target format:
      deb: "2026.1.0" or "2026.1.0~beta1"
      rpm: "2026.1.0"

.PARAMETER Architecture
    Target CPU architecture in the format expected by the package type:
      deb: amd64 | arm64
      rpm: x86_64 | aarch64

.PARAMETER Iteration
    RPM Release/iteration field (default: 1).  Ignored for .deb.

.PARAMETER PackageName
    Package name (default: unigetui).

.PARAMETER InstallPrefix
    Absolute install directory on the target system (default: /opt/unigetui).

.PARAMETER Description
    One-line package description.

.PARAMETER Maintainer
    Maintainer field value, e.g. "Name <email>".

.PARAMETER Url
    Homepage / upstream URL.

.PARAMETER AppExecutableName
    Executable filename inside InstallPrefix.

.PARAMETER LauncherName
    Public command name exposed in PATH.

.PARAMETER IconSourcePath
    Source path for the installed desktop icon.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('deb', 'rpm')]
    [string] $PackageType,

    [Parameter(Mandatory)]
    [string] $SourceDir,

    [Parameter(Mandatory)]
    [string] $OutputPath,

    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $Architecture,

    [string] $Iteration     = '1',
    [string] $PackageName   = 'unigetui',
    [string] $InstallPrefix = '/opt/unigetui',
    [string] $Description   = 'UniGetUI - GUI for package managers',
    [string] $Maintainer    = 'Devolutions Inc. <support@devolutions.net>',
    [string] $Url           = 'https://github.com/Devolutions/UniGetUI',
    [string] $AppExecutableName = 'UniGetUI.Avalonia',
    [string] $LauncherName      = 'unigetui',
    [string] $IconSourcePath    = (Join-Path $PSScriptRoot '..\src\UniGetUI\Assets\Images\icon.png')
)

$ErrorActionPreference = 'Stop'

$SourceDir  = (Resolve-Path $SourceDir).Path
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$IconSourcePath = (Resolve-Path $IconSourcePath).Path
$OutDir     = Split-Path $OutputPath
if ($OutDir) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$TmpDir = Join-Path ([System.IO.Path]::GetTempPath()) "pkg-$(New-Guid)"
New-Item -ItemType Directory -Path $TmpDir | Out-Null

$IconInstallDir = '/usr/share/icons/hicolor/512x512/apps'
$IconTargetName = "$LauncherName.png"
$IconInstallPath = "$IconInstallDir/$IconTargetName"
$DesktopFilePath = "/usr/share/applications/$LauncherName.desktop"
$LauncherPath = "/usr/bin/$LauncherName"

function New-LinuxIntegrationAssets {
    param(
        [Parameter(Mandatory)]
        [string] $StageRoot
    )

    $payloadDir = Join-Path $StageRoot ($InstallPrefix.TrimStart('/'))
    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
    & /bin/cp -a "$SourceDir/." $payloadDir
    if ($LASTEXITCODE -ne 0) { throw "cp (payload staging) exited $LASTEXITCODE" }

    $launcherFullPath = Join-Path $StageRoot $LauncherPath.TrimStart('/')
    New-Item -ItemType Directory -Path (Split-Path $launcherFullPath -Parent) -Force | Out-Null
    $launcherCommand = 'exec {0}/{1} "$@"' -f $InstallPrefix, $AppExecutableName
    $launcherScript = @(
        '#!/bin/sh',
        $launcherCommand,
        ''
    ) -join "`n"
    [System.IO.File]::WriteAllText($launcherFullPath, $launcherScript)
    & chmod 755 $launcherFullPath
    if ($LASTEXITCODE -ne 0) { throw "chmod (launcher) exited $LASTEXITCODE" }

    $desktopEntryFullPath = Join-Path $StageRoot $DesktopFilePath.TrimStart('/')
    New-Item -ItemType Directory -Path (Split-Path $desktopEntryFullPath -Parent) -Force | Out-Null
    $desktopEntry = @(
        '[Desktop Entry]',
        'Version=1.0',
        'Type=Application',
        'Name=UniGetUI',
        "Comment=$Description",
        "Exec=$LauncherPath",
        "Icon=$LauncherName",
        'Terminal=false',
        'Categories=System;Utility;',
        'StartupNotify=true',
        ''
    ) -join "`n"
    [System.IO.File]::WriteAllText($desktopEntryFullPath, $desktopEntry)
    & chmod 644 $desktopEntryFullPath
    if ($LASTEXITCODE -ne 0) { throw "chmod (desktop entry) exited $LASTEXITCODE" }

    $iconFullPath = Join-Path $StageRoot $IconInstallPath.TrimStart('/')
    New-Item -ItemType Directory -Path (Split-Path $iconFullPath -Parent) -Force | Out-Null
    Copy-Item -Path $IconSourcePath -Destination $iconFullPath -Force
    & chmod 644 $iconFullPath
    if ($LASTEXITCODE -ne 0) { throw "chmod (icon) exited $LASTEXITCODE" }
}

try {
    # ------------------------------------------------------------------
    # .deb
    # Structure: ar archive containing debian-binary, control.tar.gz, data.tar.gz
    # Tools required: tar, ar (binutils – pre-installed on Ubuntu runners)
    # ------------------------------------------------------------------
    if ($PackageType -eq 'deb') {
        # 1. debian-binary (must be the first ar member, content is "2.0\n")
        $DebianBinaryPath = Join-Path $TmpDir 'debian-binary'
        [System.IO.File]::WriteAllText($DebianBinaryPath, "2.0`n")

        # 2. data.tar.gz – payload and Linux integration assets staged under target paths
        $DataStage = Join-Path $TmpDir 'data'
        New-LinuxIntegrationAssets -StageRoot $DataStage

        $InstalledSizeKb = [long][System.Math]::Ceiling(
            (Get-ChildItem -Recurse -File $DataStage |
             Measure-Object -Property Length -Sum).Sum / 1024
        )

        # 3. control.tar.gz
        $ControlDir = Join-Path $TmpDir 'control'
        New-Item -ItemType Directory -Path $ControlDir | Out-Null

        # dpkg requires LF line endings and a trailing newline in control files
        $ControlLines = @(
            "Package: $PackageName",
            "Version: $Version",
            "Architecture: $Architecture",
            "Maintainer: $Maintainer",
            "Installed-Size: $InstalledSizeKb",
            "Homepage: $Url",
            "Description: $Description",
            "Priority: optional",
            ""
        )
        [System.IO.File]::WriteAllText(
            (Join-Path $ControlDir 'control'),
            ($ControlLines -join "`n")
        )

        $ControlTarPath = Join-Path $TmpDir 'control.tar.gz'
        & tar -czf $ControlTarPath -C $ControlDir .
        if ($LASTEXITCODE -ne 0) { throw "tar (control) exited $LASTEXITCODE" }

        $DataTarPath = Join-Path $TmpDir 'data.tar.gz'
        & tar -czf $DataTarPath -C $DataStage .
        if ($LASTEXITCODE -ne 0) { throw "tar (data) exited $LASTEXITCODE" }

        # 4. Assemble .deb with ar
        #    Member names in the archive must be bare filenames (not paths),
        #    so Push-Location into TmpDir before invoking ar.
        Push-Location $TmpDir
        try {
            & ar rc $OutputPath 'debian-binary' 'control.tar.gz' 'data.tar.gz'
            if ($LASTEXITCODE -ne 0) { throw "ar exited $LASTEXITCODE" }
        }
        finally { Pop-Location }
    }

    # ------------------------------------------------------------------
    # .rpm
    # Built with rpmbuild.  %install copies from the absolute SourceDir
    # into the buildroot so no pre-staged directory tricks are needed.
    # Tools required: rpmbuild (rpm-build apt package)
    # ------------------------------------------------------------------
    elseif ($PackageType -eq 'rpm') {
        $DataStage = Join-Path $TmpDir 'data'
        New-LinuxIntegrationAssets -StageRoot $DataStage

        $RpmTop = Join-Path $TmpDir 'rpmbuild'
        foreach ($d in 'BUILD', 'BUILDROOT', 'RPMS', 'SOURCES', 'SPECS', 'SRPMS') {
            New-Item -ItemType Directory -Path (Join-Path $RpmTop $d) | Out-Null
        }

        $SpecPath = Join-Path $RpmTop 'SPECS' "$PackageName.spec"

        # Spec uses LF line endings; here-string keeps them on Linux/macOS
        $SpecText = @"
Name:       $PackageName
Version:    $Version
Release:    $Iteration
Summary:    $Description
License:    GPL-3.0-or-later
URL:        $Url

%description
$Description

%install
cp -rp $DataStage/. %{buildroot}/

%files
%defattr(-,root,root,-)
$InstallPrefix
$LauncherPath
$DesktopFilePath
$IconInstallPath

%changelog
"@
        [System.IO.File]::WriteAllText($SpecPath, $SpecText)

        & rpmbuild -bb `
            --define "_topdir $RpmTop" `
            --define "_build_name_fmt %%{NAME}-%%{VERSION}-%%{RELEASE}.%%{ARCH}.rpm" `
            --define "__strip /bin/true" `
            --define "__debug_install_post %{nil}" `
            --define "debug_package %{nil}" `
            --target "$Architecture-unknown-linux" `
            $SpecPath
        if ($LASTEXITCODE -ne 0) { throw "rpmbuild exited $LASTEXITCODE" }

        $BuiltRpm = Get-ChildItem -Path (Join-Path $RpmTop 'RPMS') -Recurse -Filter '*.rpm' |
                    Select-Object -First 1
        if (-not $BuiltRpm) { throw "rpmbuild produced no .rpm file" }
        Move-Item -Path $BuiltRpm.FullName -Destination $OutputPath -Force
    }
}
finally {
    Remove-Item -Recurse -Force $TmpDir -ErrorAction SilentlyContinue
}

Write-Host "Created: $OutputPath"
