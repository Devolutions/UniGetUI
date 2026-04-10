Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$scriptPath = Join-Path $repoRoot 'scripts\translation\Test-TranslationSourceSync.ps1'
if (-not (Test-Path -Path $scriptPath -PathType Leaf)) {
    throw "Translation source sync smoke test script not found: $scriptPath"
}

& $scriptPath