[CmdletBinding()]
param(
    [switch]$AsJson,
    [switch]$AbsolutePaths
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'LanguageData.psm1') -Force

$languageFileMap = Get-LanguageFilePathMap -AbsolutePaths:$AbsolutePaths.IsPresent

if ($AsJson.IsPresent) {
    $languageFileMap | ConvertTo-Json -Depth 5
    return
}

$languageFileMap