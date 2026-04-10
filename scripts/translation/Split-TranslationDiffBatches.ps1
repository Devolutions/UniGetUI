[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePatch,

    [Parameter(Mandatory = $true)]
    [string]$OutputDir,

    [string]$TranslatedPatch,

    [string]$ReferencePatch,

    [ValidateRange(1, 100)]
    [int]$BatchSize = 100,

    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Languages\TranslationJsonTools.ps1')

function New-BatchFileName {
    param(
        [Parameter(Mandatory = $true)]
        [int]$BatchNumber,

        [Parameter(Mandatory = $true)]
        [string]$Kind
    )

    return ('batch.{0:D2}.{1}.json' -f $BatchNumber, $Kind)
}

$sourcePatchPath = Get-FullPath -Path $SourcePatch
$outputRoot = Get-FullPath -Path $OutputDir
$translatedPatchPath = if ([string]::IsNullOrWhiteSpace($TranslatedPatch)) { $null } else { Get-FullPath -Path $TranslatedPatch }
$referencePatchPath = if ([string]::IsNullOrWhiteSpace($ReferencePatch)) { $null } else { Get-FullPath -Path $ReferencePatch }

if (-not (Test-Path -Path $sourcePatchPath -PathType Leaf)) {
    throw "Source patch not found: $sourcePatchPath"
}

if ($null -ne $translatedPatchPath -and -not (Test-Path -Path $translatedPatchPath -PathType Leaf)) {
    throw "Translated patch not found: $translatedPatchPath"
}

if ($null -ne $referencePatchPath -and -not (Test-Path -Path $referencePatchPath -PathType Leaf)) {
    throw "Reference patch not found: $referencePatchPath"
}

if ($Clean -and (Test-Path -Path $outputRoot)) {
    Remove-Item -Path $outputRoot -Recurse -Force
}

New-Item -Path $outputRoot -ItemType Directory -Force | Out-Null

$sourceMap = Read-OrderedJsonMap -Path $sourcePatchPath
$translatedMap = if ($null -eq $translatedPatchPath) { New-OrderedStringMap } else { Read-OrderedJsonMap -Path $translatedPatchPath }
$referenceMap = if ($null -eq $referencePatchPath) { New-OrderedStringMap } else { Read-OrderedJsonMap -Path $referencePatchPath }

$entries = @($sourceMap.GetEnumerator())
$batchCount = if ($entries.Count -eq 0) { 0 } else { [int][Math]::Ceiling($entries.Count / $BatchSize) }
$manifestBatches = New-Object System.Collections.Generic.List[object]

for ($batchIndex = 0; $batchIndex -lt $batchCount; $batchIndex += 1) {
    $batchNumber = $batchIndex + 1
    $startIndex = $batchIndex * $BatchSize
    $endIndexExclusive = [Math]::Min($startIndex + $BatchSize, $entries.Count)

    $batchSourceMap = New-OrderedStringMap
    $batchTranslatedMap = New-OrderedStringMap
    $batchReferenceMap = New-OrderedStringMap
    $batchKeys = New-Object System.Collections.Generic.List[string]

    for ($entryIndex = $startIndex; $entryIndex -lt $endIndexExclusive; $entryIndex += 1) {
        $entry = $entries[$entryIndex]
        $key = [string]$entry.Key
        $value = [string]$entry.Value

        $batchSourceMap[$key] = $value
        $batchKeys.Add($key)

        if ($translatedMap.Contains($key)) {
            $batchTranslatedMap[$key] = [string]$translatedMap[$key]
        }

        if ($referenceMap.Contains($key)) {
            $batchReferenceMap[$key] = [string]$referenceMap[$key]
        }
    }

    $sourceFileName = New-BatchFileName -BatchNumber $batchNumber -Kind 'source'
    $translatedFileName = New-BatchFileName -BatchNumber $batchNumber -Kind 'translated'
    $referenceFileName = New-BatchFileName -BatchNumber $batchNumber -Kind 'reference'

    Write-OrderedJsonMap -Path (Join-Path $outputRoot $sourceFileName) -Map $batchSourceMap
    Write-OrderedJsonMap -Path (Join-Path $outputRoot $translatedFileName) -Map $batchTranslatedMap
    Write-OrderedJsonMap -Path (Join-Path $outputRoot $referenceFileName) -Map $batchReferenceMap

    $manifestBatches.Add([pscustomobject]@{
            batchNumber = $batchNumber
            keyCount = $batchSourceMap.Count
            source = $sourceFileName
            translated = $translatedFileName
            reference = $referenceFileName
            firstKey = if ($batchKeys.Count -gt 0) { $batchKeys[0] } else { $null }
            lastKey = if ($batchKeys.Count -gt 0) { $batchKeys[$batchKeys.Count - 1] } else { $null }
        }) | Out-Null
}

$manifest = [pscustomobject]@{
    generatedAt = (Get-Date).ToString('o')
    sourcePatch = $sourcePatchPath
    translatedPatch = $translatedPatchPath
    referencePatch = $referencePatchPath
    batchSize = $BatchSize
    totalKeys = $sourceMap.Count
    batchCount = $batchCount
    batches = $manifestBatches.ToArray()
}

New-Utf8File -Path (Join-Path $outputRoot 'manifest.json') -Content (($manifest | ConvertTo-Json -Depth 6) + "`r`n")

Write-Output "Created $batchCount translation batch(es) in $outputRoot"
