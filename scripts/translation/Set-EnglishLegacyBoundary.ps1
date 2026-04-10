[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$RepositoryRoot,
    [string]$EnglishFilePath,
    [string]$LegacyRef = 'HEAD~1',
    [switch]$CheckOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Languages\TranslationJsonTools.ps1')
. (Join-Path $PSScriptRoot 'Languages\TranslationSourceTools.ps1')

$resolvedRepositoryRoot = Resolve-TranslationSyncRepositoryRoot -RepositoryRoot $RepositoryRoot
$resolvedEnglishFilePath = Resolve-EnglishLanguageFilePath -ResolvedRepositoryRoot $resolvedRepositoryRoot -EnglishFilePath $EnglishFilePath

if (-not (Test-Path -Path $resolvedEnglishFilePath -PathType Leaf)) {
    throw "English translation file not found: $resolvedEnglishFilePath"
}

$relativeEnglishPath = Get-RepoRelativePath -RepositoryRoot $resolvedRepositoryRoot -FilePath $resolvedEnglishFilePath
$gitObject = '{0}:{1}' -f $LegacyRef, $relativeEnglishPath
$legacyContent = & git -C $resolvedRepositoryRoot show $gitObject 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "Unable to read '$relativeEnglishPath' from git ref '$LegacyRef'."
}

$snapshot = Get-TranslationSourceSnapshot -RepositoryRoot $resolvedRepositoryRoot
$currentMap = Read-OrderedJsonMap -Path $resolvedEnglishFilePath
$legacySourceMap = Convert-JsonContentToOrderedMap -Content ([string]::Join([Environment]::NewLine, @($legacyContent))) -Path $gitObject -DetectDuplicates

$extractedKeySet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($key in $snapshot.KeyOrder) {
    [void]$extractedKeySet.Add([string]$key)
}

$mergedLegacyMap = New-OrderedStringMap
foreach ($entry in $legacySourceMap.GetEnumerator()) {
    $key = [string]$entry.Key
    if ($key -ceq (Get-TranslationLegacyBoundaryKey)) {
        continue
    }

    if (-not $extractedKeySet.Contains($key)) {
        $mergedLegacyMap[$key] = if ($currentMap.Contains($key)) { [string]$currentMap[$key] } else { [string]$entry.Value }
    }
}

$currentSections = Split-TranslationMapAtBoundary -Map $currentMap
foreach ($entry in $currentSections.LegacyMap.GetEnumerator()) {
    $key = [string]$entry.Key
    if (-not $mergedLegacyMap.Contains($key)) {
        $mergedLegacyMap[$key] = [string]$entry.Value
    }
}

$updatedActiveMap = New-OrderedStringMap
foreach ($key in $snapshot.KeyOrder) {
    $updatedActiveMap[$key] = if ($currentMap.Contains($key)) { [string]$currentMap[$key] } else { $key }
}

$updatedMap = Join-TranslationMapWithBoundary -ActiveMap $updatedActiveMap -LegacyMap $mergedLegacyMap -IncludeBoundary
$hasChanges = -not (Test-OrderedStringMapsEqual -Left $currentMap -Right $updatedMap)

Write-Output 'English legacy boundary summary'
Write-Output "Repository root: $resolvedRepositoryRoot"
Write-Output "English file: $resolvedEnglishFilePath"
Write-Output "Legacy ref: $LegacyRef"
Write-Output "Active keys: $($updatedActiveMap.Count)"
Write-Output "Legacy keys restored: $($mergedLegacyMap.Count)"
Write-Output "Needs update: $hasChanges"

if ($CheckOnly) {
    if ($hasChanges) {
        throw 'English legacy boundary layout is not in the expected state.'
    }

    return
}

if (-not $hasChanges) {
    Write-Output ''
    Write-Output 'The English file already matches the expected boundary layout.'
    return
}

if ($PSCmdlet.ShouldProcess($resolvedEnglishFilePath, 'Populate the English legacy translation tail')) {
    Write-OrderedJsonMap -Path $resolvedEnglishFilePath -Map $updatedMap
    Write-Output ''
    Write-Output 'The English legacy boundary layout was updated successfully.'
}