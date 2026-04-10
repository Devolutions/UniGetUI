[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$EnglishFilePath,
    [string]$LanguagesDirectory,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Languages\TranslationJsonTools.ps1')
. (Join-Path $PSScriptRoot 'Languages\TranslationSourceTools.ps1')

$resolvedRepositoryRoot = Resolve-TranslationSyncRepositoryRoot -RepositoryRoot $RepositoryRoot
$resolvedEnglishFilePath = Resolve-EnglishLanguageFilePath -ResolvedRepositoryRoot $resolvedRepositoryRoot -EnglishFilePath $EnglishFilePath
$resolvedLanguagesDirectory = if ([string]::IsNullOrWhiteSpace($LanguagesDirectory)) {
    Split-Path -Path $resolvedEnglishFilePath -Parent
}
else {
    Get-FullPath -Path $LanguagesDirectory
}

if (-not (Test-Path -Path $resolvedLanguagesDirectory -PathType Container)) {
    throw "Languages directory not found: $resolvedLanguagesDirectory"
}

$englishMap = Read-OrderedJsonMap -Path $resolvedEnglishFilePath
$englishSections = Split-TranslationMapAtBoundary -Map $englishMap
if (-not $englishSections.HasBoundary) {
    throw 'The English translation file must contain the legacy boundary marker before locale alignment can be exported.'
}

$englishActiveOrder = @($englishSections.ActiveMap.Keys)
$englishLegacyOrder = @($englishSections.LegacyMap.Keys)
$englishActiveSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($key in $englishActiveOrder) {
    [void]$englishActiveSet.Add([string]$key)
}

$englishLegacySet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($key in $englishLegacyOrder) {
    [void]$englishLegacySet.Add([string]$key)
}

$reports = New-Object System.Collections.Generic.List[object]
$languageFiles = Get-ChildItem -Path $resolvedLanguagesDirectory -Filter 'lang_*.json' | Sort-Object Name
foreach ($languageFile in $languageFiles) {
    if ($languageFile.FullName -ieq $resolvedEnglishFilePath) {
        continue
    }

    $languageMap = Read-OrderedJsonMapPermissive -Path $languageFile.FullName
    $languageSections = Split-TranslationMapAtBoundary -Map $languageMap

    $missingActiveKeys = New-Object System.Collections.Generic.List[string]
    foreach ($key in $englishActiveOrder) {
        if (-not $languageMap.Contains($key)) {
            $missingActiveKeys.Add([string]$key)
        }
    }

    $presentActiveInExpectedOrder = New-Object System.Collections.Generic.List[string]
    foreach ($key in $englishActiveOrder) {
        if ($languageMap.Contains($key)) {
            $presentActiveInExpectedOrder.Add([string]$key)
        }
    }

    $currentActiveInEnglishSet = New-Object System.Collections.Generic.List[string]
    $extraActiveKeys = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $languageSections.ActiveMap.GetEnumerator()) {
        $key = [string]$entry.Key
        if ($englishActiveSet.Contains($key)) {
            $currentActiveInEnglishSet.Add($key)
        }
        elseif ($key -cne (Get-TranslationLegacyBoundaryKey)) {
            $extraActiveKeys.Add($key)
        }
    }

    $presentLegacyInExpectedOrder = New-Object System.Collections.Generic.List[string]
    foreach ($key in $englishLegacyOrder) {
        if ($languageMap.Contains($key)) {
            $presentLegacyInExpectedOrder.Add([string]$key)
        }
    }

    $currentLegacyInEnglishSet = New-Object System.Collections.Generic.List[string]
    $extraLegacyKeys = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $languageSections.LegacyMap.GetEnumerator()) {
        $key = [string]$entry.Key
        if ($englishLegacySet.Contains($key)) {
            $currentLegacyInEnglishSet.Add($key)
        }
        else {
            $extraLegacyKeys.Add($key)
        }
    }

    $reportEntry = [pscustomobject]@{
        languageFile = (Get-RepoRelativePath -RepositoryRoot $resolvedRepositoryRoot -FilePath $languageFile.FullName)
        hasBoundary = $languageSections.HasBoundary
        activeOrderAligned = [System.Linq.Enumerable]::SequenceEqual($currentActiveInEnglishSet.ToArray(), $presentActiveInExpectedOrder.ToArray())
        legacyOrderAligned = [System.Linq.Enumerable]::SequenceEqual($currentLegacyInEnglishSet.ToArray(), $presentLegacyInExpectedOrder.ToArray())
        activeKeyCount = $currentActiveInEnglishSet.Count
        legacyKeyCount = $currentLegacyInEnglishSet.Count
        missingActiveKeyCount = $missingActiveKeys.Count
        extraActiveKeyCount = $extraActiveKeys.Count
        extraLegacyKeyCount = $extraLegacyKeys.Count
        missingActiveKeys = $missingActiveKeys.ToArray()
        extraActiveKeys = $extraActiveKeys.ToArray()
        extraLegacyKeys = $extraLegacyKeys.ToArray()
    }

    $reports.Add($reportEntry)
}

$report = [pscustomobject]@{
    generatedAt = (Get-Date).ToString('o')
    repositoryRoot = $resolvedRepositoryRoot
    englishFile = (Get-RepoRelativePath -RepositoryRoot $resolvedRepositoryRoot -FilePath $resolvedEnglishFilePath)
    boundaryKey = Get-TranslationLegacyBoundaryKey
    englishActiveKeyCount = $englishActiveOrder.Count
    englishLegacyKeyCount = $englishLegacyOrder.Count
    languageCount = $reports.Count
    languages = $reports.ToArray()
}

$resolvedOutputPath = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Join-Path $resolvedRepositoryRoot 'artifacts\translation\translation-boundary-alignment.json'
}
else {
    Get-FullPath -Path $OutputPath
}

New-Utf8File -Path $resolvedOutputPath -Content ($report | ConvertTo-Json -Depth 6)

Write-Output 'Translation boundary alignment summary'
Write-Output "Repository root: $resolvedRepositoryRoot"
Write-Output "English file: $resolvedEnglishFilePath"
Write-Output "Language files analyzed: $($reports.Count)"
Write-Output "English active keys: $($englishActiveOrder.Count)"
Write-Output "English legacy keys: $($englishLegacyOrder.Count)"
Write-Output "Report: $resolvedOutputPath"