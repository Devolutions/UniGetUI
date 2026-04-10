[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$RepositoryRoot,
    [string]$EnglishFilePath,
    [string]$LanguagesDirectory,
    [string]$InventoryOutputPath,
    [switch]$IncludeEnglish,
    [switch]$CheckOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$scriptPath = Join-Path $repoRoot 'scripts\translation\Set-TranslationBoundaryOrder.ps1'
if (-not (Test-Path -Path $scriptPath -PathType Leaf)) {
    throw "Translation boundary reorder script not found: $scriptPath"
}

& $scriptPath `
    -RepositoryRoot $RepositoryRoot `
    -EnglishFilePath $EnglishFilePath `
    -LanguagesDirectory $LanguagesDirectory `
    -InventoryOutputPath $InventoryOutputPath `
    -IncludeEnglish:$IncludeEnglish.IsPresent `
    -CheckOnly:$CheckOnly.IsPresent