#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publishes UniGetUI as a NativeAOT self-contained Windows build.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER Platform
    Target platform. Default: x64. Supported: x64, arm64.

.PARAMETER OutputPath
    Directory for the published output. Default: ./artifacts/nativeaot/win-<platform>.

.PARAMETER PublishProfileName
    NativeAOT publish profile name. Default: Win-<platform>-NativeAot.
#>
[CmdletBinding()]
param(
    [string] $Configuration = "Release",
    [ValidateSet("x64", "arm64")]
    [string] $Platform = "x64",
    [string] $OutputPath = (Join-Path (Join-Path $PSScriptRoot "..") "artifacts/nativeaot/win-$Platform"),
    [string] $PublishProfileName = "Win-$Platform-NativeAot"
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$ProjectPath = Join-Path $RepoRoot "src/UniGetUI.Avalonia/UniGetUI.Avalonia.csproj"

if (Test-Path $OutputPath) {
    Remove-Item $OutputPath -Recurse -Force
}

New-Item $OutputPath -ItemType Directory -Force | Out-Null

Write-Host "Publishing NativeAOT build for win-$Platform to $OutputPath" -ForegroundColor Cyan

dotnet publish $ProjectPath `
    --configuration $Configuration `
    --runtime "win-$Platform" `
    --self-contained true `
    --output $OutputPath `
    /m:1 `
    /p:Platform=$Platform `
    /p:PublishProfile=$PublishProfileName `
    /p:TrimmerSingleWarn=false `
    /p:UseSharedCompilation=false `
    --nologo `
    --verbosity minimal

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "NativeAOT publish complete." -ForegroundColor Green
