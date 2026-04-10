[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BatchDir,

    [Parameter(Mandatory = $true)]
    [string]$OutputTranslatedPatch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Languages\TranslationJsonTools.ps1')

$batchRoot = Get-FullPath -Path $BatchDir
$outputPath = Get-FullPath -Path $OutputTranslatedPatch
$manifestPath = Join-Path $batchRoot 'manifest.json'

if (-not (Test-Path -Path $manifestPath -PathType Leaf)) {
    throw "Batch manifest not found: $manifestPath"
}

$manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
$mergedMap = New-OrderedStringMap

foreach ($batch in @($manifest.batches | Sort-Object batchNumber)) {
    $translatedPath = Join-Path $batchRoot ([string]$batch.translated)
    if (-not (Test-Path -Path $translatedPath -PathType Leaf)) {
        throw "Translated batch file not found: $translatedPath"
    }

    $translatedMap = Read-OrderedJsonMap -Path $translatedPath
    foreach ($entry in $translatedMap.GetEnumerator()) {
        $mergedMap[[string]$entry.Key] = [string]$entry.Value
    }
}

Write-OrderedJsonMap -Path $outputPath -Map $mergedMap
Write-Output "Merged $($manifest.batchCount) translation batch(es) into $outputPath"