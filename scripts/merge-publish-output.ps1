#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Merges one publish directory into another, allowing only identical file collisions.

.PARAMETER Source
    The publish directory to copy from.

.PARAMETER Destination
    The publish directory to copy into.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Source,

    [Parameter(Mandatory)]
    [string] $Destination
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Source -PathType Container)) {
    throw "Source directory '$Source' does not exist."
}

if (-not (Test-Path $Destination -PathType Container)) {
    throw "Destination directory '$Destination' does not exist."
}

$Source = (Resolve-Path $Source).Path
$Destination = (Resolve-Path $Destination).Path

$sourceWinsConflicts = @{
    'Microsoft.Extensions.DependencyInjection.Abstractions.dll' = $true
    'Microsoft.VisualBasic.dll' = $true
    'Microsoft.Win32.SystemEvents.dll' = $true
    'System.Diagnostics.EventLog.dll' = $true
    'System.Diagnostics.EventLog.Messages.dll' = $true
    'System.Drawing.Common.dll' = $true
    'System.Drawing.dll' = $true
    'System.Private.Windows.Core.dll' = $true
    'System.Security.Cryptography.Pkcs.dll' = $true
    'System.Security.Cryptography.Xml.dll' = $true
    'WindowsBase.dll' = $true
}

$destinationWinsConflicts = @{
    'Microsoft.Windows.SDK.NET.dll' = $true
    'WinRT.Runtime.dll' = $true
}

Get-ChildItem $Source -Recurse -File | ForEach-Object {
    $relativePath = $_.FullName.Substring($Source.Length).TrimStart('\', '/')
    $destinationPath = Join-Path $Destination $relativePath
    $destinationDirectory = Split-Path $destinationPath -Parent

    if (Test-Path $destinationPath -PathType Leaf) {
        $sourceHash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash $destinationPath -Algorithm SHA256).Hash

        if ($sourceHash -ne $destinationHash) {
            $fileName = [System.IO.Path]::GetFileName($relativePath)

            if ($sourceWinsConflicts.ContainsKey($fileName)) {
                Copy-Item $_.FullName -Destination $destinationPath -Force
                return
            }

            if ($destinationWinsConflicts.ContainsKey($fileName)) {
                return
            }

            throw "Publish merge conflict for '$relativePath': source and destination files differ."
        }

        return
    }

    if (-not (Test-Path $destinationDirectory -PathType Container)) {
        New-Item $destinationDirectory -ItemType Directory -Force | Out-Null
    }

    Copy-Item $_.FullName -Destination $destinationPath -Force
}