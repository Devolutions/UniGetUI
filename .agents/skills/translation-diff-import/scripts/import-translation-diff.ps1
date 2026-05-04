param(
    [Parameter(Mandatory = $true)]
    [string]$TranslatedPatch,

    [string]$SourcePatch,

    [Parameter(Mandatory = $true)]
    [string]$TargetJson,

    [string]$NeutralJson,

    [string]$OutputJson,

    [string]$OutputDir = 'generated/translation-diff-import',

    [switch]$AllowUnchangedValues,

    [switch]$KeepIntermediate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\..\translation-diff-export\scripts\TranslationDiff.JsonTools.ps1')
. ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..\scripts\translation\Languages\IntentionalSourceEqualValues.ps1')))

function Assert-PatchCompatibility {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$SourceMap,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$TranslatedMap,

        [Parameter(Mandatory = $true)]
        [string]$LanguageCode,

        [Parameter(Mandatory = $true)]
        [bool]$AllowUnchanged
    )

    foreach ($entry in $TranslatedMap.GetEnumerator()) {
        $key = [string]$entry.Key
        $translatedValue = [string]$entry.Value
        if (-not $SourceMap.Contains($key)) {
            throw "Translated patch contains unexpected key '$key'."
        }

        if ([string]::IsNullOrWhiteSpace($translatedValue)) {
            throw "Translated patch contains an empty value for key '$key'."
        }

        $sourceValue = [string]$SourceMap[$key]
        Assert-TranslationStructure -SourceValue $sourceValue -TranslatedValue $translatedValue -Key $key

        if (-not $AllowUnchanged -and $translatedValue -ceq $sourceValue -and -not (Test-IntentionalSourceEqualValue -LanguageCode $LanguageCode -SourceValue $sourceValue -TargetValue $translatedValue)) {
            throw "Translated patch still contains the English source value for key '$key'. Use -AllowUnchangedValues only when that is intentional."
        }
    }
}

function Merge-LanguageMaps {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$NeutralMap,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$TargetMap,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$TranslatedPatchMap
    )

    $mergedMap = New-OrderedStringMap

    foreach ($entry in $NeutralMap.GetEnumerator()) {
        $key = [string]$entry.Key
        if ($TranslatedPatchMap.Contains($key)) {
            $mergedMap[$key] = [string]$TranslatedPatchMap[$key]
            continue
        }

        if ($TargetMap.Contains($key)) {
            $mergedMap[$key] = [string]$TargetMap[$key]
        }
    }

    foreach ($entry in $TargetMap.GetEnumerator()) {
        $key = [string]$entry.Key
        if (-not $NeutralMap.Contains($key) -and -not $mergedMap.Contains($key)) {
            $mergedMap[$key] = [string]$entry.Value
        }
    }

    return $mergedMap
}

$translatedPatchPath = Get-FullPath -Path $TranslatedPatch
if (-not (Test-Path -Path $translatedPatchPath -PathType Leaf)) {
    throw "Translated patch file not found: $translatedPatchPath"
}

$targetPath = Get-FullPath -Path $TargetJson
$targetMap = Read-OrderedJsonMap -Path $targetPath
$translatedPatchMap = Read-OrderedJsonMap -Path $translatedPatchPath
$targetBaseName = [System.IO.Path]::GetFileNameWithoutExtension($targetPath)
$languageCode = if ($targetBaseName.StartsWith('lang_', [System.StringComparison]::Ordinal)) {
    $targetBaseName.Substring(5)
}
else {
    ''
}

if ([string]::IsNullOrWhiteSpace($NeutralJson)) {
    $NeutralJson = Join-Path (Split-Path -Path $targetPath -Parent) 'lang_en.json'
}

$neutralPath = Get-FullPath -Path $NeutralJson
if (-not (Test-Path -Path $neutralPath -PathType Leaf)) {
    throw "Neutral JSON file not found: $neutralPath"
}

$neutralMap = Read-OrderedJsonMap -Path $neutralPath
$sourceMap = $null
if (-not [string]::IsNullOrWhiteSpace($SourcePatch)) {
    $sourcePatchPath = Get-FullPath -Path $SourcePatch
    if (-not (Test-Path -Path $sourcePatchPath -PathType Leaf)) {
        throw "Source patch file not found: $sourcePatchPath"
    }

    $sourceMap = Read-OrderedJsonMap -Path $sourcePatchPath
    Assert-PatchCompatibility -SourceMap $sourceMap -TranslatedMap $translatedPatchMap -LanguageCode $languageCode -AllowUnchanged:$AllowUnchangedValues.IsPresent
}

foreach ($entry in $translatedPatchMap.GetEnumerator()) {
    $key = [string]$entry.Key
    if (-not $neutralMap.Contains($key)) {
        throw "Translated patch key '$key' does not exist in the neutral language file."
    }
}

$outputRoot = Get-FullPath -Path $OutputDir
$tmpRoot = Join-Path $outputRoot 'tmp'
New-Item -Path $outputRoot -ItemType Directory -Force | Out-Null
New-Item -Path $tmpRoot -ItemType Directory -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($OutputJson)) {
    if (Test-Path -Path $targetPath -PathType Leaf) {
        $targetName = [System.IO.Path]::GetFileNameWithoutExtension($targetPath)
        $OutputJson = Join-Path (Split-Path -Path $targetPath -Parent) ('{0}.merged.json' -f $targetName)
    }
    else {
        $OutputJson = $targetPath
    }
}

$outputJsonPath = Get-FullPath -Path $OutputJson
$mergedMap = Merge-LanguageMaps -NeutralMap $neutralMap -TargetMap $targetMap -TranslatedPatchMap $translatedPatchMap
Write-OrderedJsonMap -Path $outputJsonPath -Map $mergedMap

if (-not $KeepIntermediate.IsPresent) {
    Remove-Item -Path $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output "Merged translated patch into: $outputJsonPath"
if ($null -ne $sourceMap) {
    Write-Output "Validated translated patch against: $SourcePatch"
}
