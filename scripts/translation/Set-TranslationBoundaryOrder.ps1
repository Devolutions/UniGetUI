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
    throw 'The English translation file must contain the legacy boundary marker before locale files can be reordered.'
}

$englishActiveOrder = @($englishSections.ActiveMap.Keys)
$englishLegacyOrder = @($englishSections.LegacyMap.Keys)
$englishKeySet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($key in $englishActiveOrder) {
    [void]$englishKeySet.Add([string]$key)
}
foreach ($key in $englishLegacyOrder) {
    [void]$englishKeySet.Add([string]$key)
}

$filesToProcess = Get-ChildItem -Path $resolvedLanguagesDirectory -Filter 'lang_*.json' | Sort-Object Name
if (-not $IncludeEnglish) {
    $filesToProcess = @($filesToProcess | Where-Object { $_.FullName -ine $resolvedEnglishFilePath })
}

$changedFiles = New-Object System.Collections.Generic.List[string]
$reports = New-Object System.Collections.Generic.List[object]

foreach ($languageFile in $filesToProcess) {
    $languageMap = Read-OrderedJsonMapPermissive -Path $languageFile.FullName

    $activeOutput = New-OrderedStringMap
    foreach ($key in $englishActiveOrder) {
        if ($languageMap.Contains($key)) {
            $activeOutput[$key] = [string]$languageMap[$key]
        }
    }

    $legacyOutput = New-OrderedStringMap
    foreach ($key in $englishLegacyOrder) {
        if ($languageMap.Contains($key)) {
            $legacyOutput[$key] = [string]$languageMap[$key]
        }
    }

    $unmappedKeys = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $languageMap.GetEnumerator()) {
        $key = [string]$entry.Key
        if ($key -ceq (Get-TranslationLegacyBoundaryKey)) {
            continue
        }

        if (-not $englishKeySet.Contains($key)) {
            $legacyOutput[$key] = [string]$entry.Value
            $unmappedKeys.Add($key)
        }
    }

    $updatedMap = Join-TranslationMapWithBoundary -ActiveMap $activeOutput -LegacyMap $legacyOutput -IncludeBoundary
    $hasChanges = -not (Test-OrderedStringMapsEqual -Left $languageMap -Right $updatedMap)

    if ($hasChanges) {
        $changedFiles.Add((Get-RepoRelativePath -RepositoryRoot $resolvedRepositoryRoot -FilePath $languageFile.FullName))
        if (-not $CheckOnly -and $PSCmdlet.ShouldProcess($languageFile.FullName, 'Reorder translation file to the English legacy boundary layout')) {
            Write-OrderedJsonMap -Path $languageFile.FullName -Map $updatedMap
        }
    }

    $reports.Add([pscustomobject]@{
            languageFile = (Get-RepoRelativePath -RepositoryRoot $resolvedRepositoryRoot -FilePath $languageFile.FullName)
            changed = $hasChanges
            activeKeyCount = $activeOutput.Count
            legacyKeyCount = $legacyOutput.Count
            unmappedKeyCount = $unmappedKeys.Count
            unmappedKeys = $unmappedKeys.ToArray()
        })
}

if (-not [string]::IsNullOrWhiteSpace($InventoryOutputPath)) {
    $resolvedInventoryOutputPath = Get-FullPath -Path $InventoryOutputPath
    $report = [pscustomobject]@{
        generatedAt = (Get-Date).ToString('o')
        repositoryRoot = $resolvedRepositoryRoot
        englishFile = (Get-RepoRelativePath -RepositoryRoot $resolvedRepositoryRoot -FilePath $resolvedEnglishFilePath)
        languagesDirectory = (Get-RepoRelativePath -RepositoryRoot $resolvedRepositoryRoot -FilePath $resolvedLanguagesDirectory)
        includeEnglish = [bool]$IncludeEnglish
        changedFileCount = $changedFiles.Count
        changedFiles = $changedFiles.ToArray()
        files = $reports.ToArray()
    }

    New-Utf8File -Path $resolvedInventoryOutputPath -Content ($report | ConvertTo-Json -Depth 6)
}

Write-Output 'Translation boundary reorder summary'
Write-Output "Repository root: $resolvedRepositoryRoot"
Write-Output "Languages directory: $resolvedLanguagesDirectory"
Write-Output "Include English: $([bool]$IncludeEnglish)"
Write-Output "Files processed: $(@($filesToProcess).Count)"
Write-Output "Files changed: $($changedFiles.Count)"

if ($changedFiles.Count -gt 0) {
    Write-Output ''
    Write-Output 'Changed files:'
    foreach ($path in $changedFiles) {
        Write-Output ("  * {0}" -f $path)
    }
}

if ($CheckOnly -and $changedFiles.Count -gt 0) {
    throw 'One or more translation files are not aligned to the English legacy boundary layout.'
}