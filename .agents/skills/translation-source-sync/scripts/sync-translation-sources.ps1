[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$RepositoryRoot,
    [string]$EnglishFilePath,
    [switch]$CheckOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$scriptPath = Join-Path $repoRoot 'scripts\translation\Sync-TranslationSources.ps1'
if (-not (Test-Path -Path $scriptPath -PathType Leaf)) {
    throw "Translation source sync script not found: $scriptPath"
}

& $scriptPath -RepositoryRoot $RepositoryRoot -EnglishFilePath $EnglishFilePath -CheckOnly:$CheckOnly.IsPresent