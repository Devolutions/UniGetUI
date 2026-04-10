[CmdletBinding()]
param(
    [ValidateSet('Table', 'Json', 'Markdown')]
    [string]$OutputFormat = 'Table',

    [switch]$IncludeEnglish,

    [switch]$OnlyIncomplete,

    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$scriptPath = Join-Path $repoRoot 'scripts\translation\Get-TranslationStatus.ps1'
if (-not (Test-Path -Path $scriptPath -PathType Leaf)) {
    throw "Translation status script not found: $scriptPath"
}

& $scriptPath -OutputFormat $OutputFormat -IncludeEnglish:$IncludeEnglish.IsPresent -OnlyIncomplete:$OnlyIncomplete.IsPresent -OutputPath $OutputPath