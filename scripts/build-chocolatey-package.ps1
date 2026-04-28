#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds a local UniGetUI Chocolatey package.

.DESCRIPTION
    Reproduces the Chocolatey packaging portion of Devolutions/release-notes CI.
    By default it sources the installer and checksum from GitHub Releases instead
    of OneDrive. It can also build a fresh local x64 installer from the current
    checkout and embed that installer into the nupkg for local test-environment
    debugging.

.PARAMETER Version
    UniGetUI version to package, for example 2026.1.6. If omitted, the latest
    GitHub release is used.

.PARAMETER OutputPath
    Directory where the downloaded assets, generated package files, and final
    .nupkg are written. Default: ./output/chocolatey

.PARAMETER BuildLocalInstaller
    Build a fresh local x64 installer from the current checkout and package that
    installer into the nupkg instead of using a GitHub release asset.

.PARAMETER InstallerPath
    Use an existing local installer file instead of downloading from GitHub.
    The installer is embedded into the generated nupkg.

.PARAMETER SkipLocalBuildTests
    When BuildLocalInstaller is set, skip running tests during the local build.

.EXAMPLE
    ./scripts/build-chocolatey-package.ps1

.EXAMPLE
    ./scripts/build-chocolatey-package.ps1 -Version 2026.1.6

.EXAMPLE
    ./scripts/build-chocolatey-package.ps1 -BuildLocalInstaller -Version 2026.1.6 -SkipLocalBuildTests
#>

[CmdletBinding()]
param(
    [string] $Version,
    [string] $OutputPath = (Join-Path $PSScriptRoot ".." "output" "chocolatey"),
    [switch] $BuildLocalInstaller,
    [string] $InstallerPath,
    [switch] $SkipLocalBuildTests
)

$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$TemplateDir = Join-Path $RepoRoot "package" "chocolatey" "unigetui"
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$DownloadDir = Join-Path $OutputPath "downloads"
$StagingRoot = Join-Path $OutputPath "staging"
$StagingDir = Join-Path $StagingRoot "unigetui"

function Invoke-GitHubApi {
    param(
        [Parameter(Mandatory)]
        [string] $Uri
    )

    $headers = @{
        'Accept' = 'application/vnd.github+json'
        'User-Agent' = 'UniGetUI-Chocolatey-Local-Pack'
    }

    if ($env:GITHUB_TOKEN) {
        $headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)"
    }

    Invoke-RestMethod -Uri $Uri -Headers $headers
}

function Download-File {
    param(
        [Parameter(Mandatory)]
        [string] $Uri,

        [Parameter(Mandatory)]
        [string] $DestinationPath
    )

    $headers = @{ 'User-Agent' = 'UniGetUI-Chocolatey-Local-Pack' }
    if ($env:GITHUB_TOKEN) {
        $headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)"
    }

    Invoke-WebRequest -Uri $Uri -Headers $headers -OutFile $DestinationPath
}

function Get-Release {
    param(
        [string] $RequestedVersion
    )

    if ([string]::IsNullOrWhiteSpace($RequestedVersion)) {
        return Invoke-GitHubApi -Uri 'https://api.github.com/repos/Devolutions/UniGetUI/releases/latest'
    }

    $tag = if ($RequestedVersion.StartsWith('v')) { $RequestedVersion } else { "v$RequestedVersion" }
    $escapedTag = [System.Uri]::EscapeDataString($tag)
    return Invoke-GitHubApi -Uri "https://api.github.com/repos/Devolutions/UniGetUI/releases/tags/$escapedTag"
}

function Get-CurrentPackageVersion {
    $assemblyInfoPath = Join-Path $RepoRoot 'src' 'SharedAssemblyInfo.cs'
    $versionMatch = Select-String -Path $assemblyInfoPath -Pattern 'AssemblyInformationalVersion\("([^"]+)"\)'
    if (-not $versionMatch) {
        throw "Could not determine the current package version from $assemblyInfoPath"
    }

    return $versionMatch.Matches[0].Groups[1].Value
}

function Get-ChecksumFromFile {
    param(
        [Parameter(Mandatory)]
        [string] $ChecksumsPath,

        [Parameter(Mandatory)]
        [string] $AssetName
    )

    $pattern = "^(?<hash>[A-Fa-f0-9]{64})\s+$([regex]::Escape($AssetName))$"
    $line = Select-String -Path $ChecksumsPath -Pattern $pattern | Select-Object -First 1
    if (-not $line) {
        throw "Could not find a SHA256 for '$AssetName' in $ChecksumsPath"
    }

    return $line.Matches[0].Groups['hash'].Value.ToUpperInvariant()
}

function Require-Command {
    param(
        [Parameter(Mandatory)]
        [string] $Name
    )

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found in PATH."
    }
}

Require-Command -Name 'choco'

if (-not (Test-Path $TemplateDir)) {
    throw "Chocolatey template folder not found: $TemplateDir"
}

if ($BuildLocalInstaller -and $InstallerPath) {
    throw 'BuildLocalInstaller and InstallerPath cannot be used together.'
}

$useEmbeddedInstaller = $BuildLocalInstaller -or -not [string]::IsNullOrWhiteSpace($InstallerPath)
$installerAssetName = 'UniGetUI.Installer.x64.exe'
$installerUrl = $null
$release = $null

if ($BuildLocalInstaller) {
    $localBuildOutputPath = Join-Path $OutputPath 'local-build'
    $buildArgs = @{
        Platform = 'x64'
        OutputPath = $localBuildOutputPath
    }

    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        $buildArgs['Version'] = $Version
    }

    if ($SkipLocalBuildTests) {
        $buildArgs['SkipTests'] = $true
    }

    Write-Host 'Building local UniGetUI installer from the current checkout'
    & (Join-Path $PSScriptRoot 'build.ps1') @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Local installer build failed with exit code $LASTEXITCODE"
    }

    $InstallerPath = Join-Path $localBuildOutputPath $installerAssetName
    if (-not (Test-Path $InstallerPath)) {
        throw "Local installer was not produced at $InstallerPath"
    }

    $packageVersion = if ([string]::IsNullOrWhiteSpace($Version)) { Get-CurrentPackageVersion } else { $Version }
    Write-Host "Packaging UniGetUI Chocolatey package from local installer $InstallerPath"
}
elseif (-not [string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = [System.IO.Path]::GetFullPath($InstallerPath)
    if (-not (Test-Path $InstallerPath)) {
        throw "InstallerPath was not found: $InstallerPath"
    }

    $packageVersion = if ([string]::IsNullOrWhiteSpace($Version)) { Get-CurrentPackageVersion } else { $Version }
    Write-Host "Packaging UniGetUI Chocolatey package from local installer $InstallerPath"
}
else {
    $release = Get-Release -RequestedVersion $Version
    $packageVersion = $release.tag_name.TrimStart('v')

    $installerAsset = $release.assets | Where-Object { $_.name -eq $installerAssetName } | Select-Object -First 1
    if (-not $installerAsset) {
        throw "Could not find asset '$installerAssetName' in release $($release.tag_name)"
    }

    $checksumsAsset = $release.assets | Where-Object { $_.name -eq 'checksums.txt' } | Select-Object -First 1
    if (-not $checksumsAsset) {
        throw "Could not find asset 'checksums.txt' in release $($release.tag_name)"
    }

    Write-Host "Packaging UniGetUI Chocolatey package for release $($release.tag_name)"
    Write-Host "Installer asset: $($installerAsset.browser_download_url)"

    $installerPath = Join-Path $DownloadDir $installerAsset.name
    $checksumsPath = Join-Path $DownloadDir $checksumsAsset.name

    Download-File -Uri $installerAsset.browser_download_url -DestinationPath $installerPath
    Download-File -Uri $checksumsAsset.browser_download_url -DestinationPath $checksumsPath

    $sha256 = Get-ChecksumFromFile -ChecksumsPath $checksumsPath -AssetName $installerAsset.name
    $installerUrl = $installerAsset.browser_download_url
}

New-Item -Path $OutputPath -ItemType Directory -Force | Out-Null
New-Item -Path $DownloadDir -ItemType Directory -Force | Out-Null
New-Item -Path $StagingRoot -ItemType Directory -Force | Out-Null

if (Test-Path $StagingRoot) {
    Remove-Item $StagingRoot -Recurse -Force
}

New-Item -Path $StagingRoot -ItemType Directory -Force | Out-Null
Copy-Item -Path $TemplateDir -Destination $StagingDir -Recurse

if ($useEmbeddedInstaller) {
    $embeddedInstallerName = Split-Path $InstallerPath -Leaf
    $embeddedInstallerPath = Join-Path $StagingDir 'tools' $embeddedInstallerName
    Copy-Item -Path $InstallerPath -Destination $embeddedInstallerPath -Force
    $sha256 = (Get-FileHash -Path $InstallerPath -Algorithm SHA256).Hash.ToUpperInvariant()
}

$nuspecTemplatePath = Join-Path $StagingDir 'unigetui.template.nuspec'
$installTemplatePath = Join-Path $StagingDir 'tools' 'chocolateyInstall.template.ps1'
$nuspecPath = Join-Path $StagingDir 'unigetui.nuspec'
$installScriptPath = Join-Path $StagingDir 'tools' 'chocolateyInstall.ps1'

$nuspecContent = (Get-Content $nuspecTemplatePath -Raw).Replace('$VAR1$', $packageVersion)

if ($useEmbeddedInstaller) {
    $nuspecContent = $nuspecContent.Replace(
        '    <file src="tools\chocolateyUninstall.ps1" target="tools" />',
        ('    <file src="tools\chocolateyUninstall.ps1" target="tools" />' + "`r`n" + ('    <file src="tools\{0}" target="tools" />' -f $embeddedInstallerName))
    )
}

$installContent = Get-Content $installTemplatePath -Raw

if ($useEmbeddedInstaller) {
    $installContent = $installContent.Replace(
        '$Url = ''https://cdn.devolutions.net/download/Devolutions.UniGetUI.win-x64.$VAR1$.exe''',
        ('$InstallerPath = Join-Path $PSScriptRoot ''{0}''' -f $embeddedInstallerName)
    )
    $installContent = $installContent.Replace('  url           = $Url', '  file          = $InstallerPath')
    $installContent = $installContent -replace "(?m)^\s*checksum\s*=.*\r?\n", ''
    $installContent = $installContent -replace "(?m)^\s*checksumType\s*=.*\r?\n", ''
}
else {
    $installContent = $installContent.Replace('$VAR2$', $sha256)
    $installContent = $installContent.Replace(
        "'https://cdn.devolutions.net/download/Devolutions.UniGetUI.win-x64.`$VAR1$.exe'",
        "'$installerUrl'"
    )
}

$installContent = $installContent.Replace('$VAR1$', $packageVersion)

Set-Content -Path $nuspecPath -Value $nuspecContent -Encoding utf8NoBOM
Set-Content -Path $installScriptPath -Value $installContent -Encoding utf8NoBOM

Push-Location $StagingDir
try {
    & choco pack 'unigetui.nuspec' --outputdirectory $OutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "choco pack failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$packagePath = Join-Path $OutputPath "unigetui.$packageVersion.nupkg"
if (-not (Test-Path $packagePath)) {
    $packagePath = Get-ChildItem -Path $OutputPath -Filter 'unigetui.*.nupkg' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}

Write-Host "SHA256 (installer): $sha256"
Write-Host "Package written to: $packagePath"
