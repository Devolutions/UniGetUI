[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$RepositoryRoot,
    [string]$EnglishFilePath,
    [string]$InventoryOutputPath,
    [switch]$UseLegacyBoundary,
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

$snapshot = Get-TranslationSourceSnapshot -RepositoryRoot $resolvedRepositoryRoot
$currentMap = Read-OrderedJsonMapPermissive -Path $resolvedEnglishFilePath
$currentSections = Split-TranslationMapAtBoundary -Map $currentMap
$preserveLegacyBoundary = $UseLegacyBoundary -or $currentSections.HasBoundary

$extractedKeySet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($key in $snapshot.KeyOrder) {
    [void]$extractedKeySet.Add([string]$key)
}

$currentKeySet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($entry in $currentMap.GetEnumerator()) {
    $key = [string]$entry.Key
    if ($key -ceq (Get-TranslationLegacyBoundaryKey)) {
        continue
    }

    [void]$currentKeySet.Add($key)
}

$missingKeys = New-Object System.Collections.Generic.List[string]
foreach ($key in $snapshot.KeyOrder) {
    if (-not $currentKeySet.Contains($key)) {
        $missingKeys.Add($key)
    }
}

$unusedKeys = New-Object System.Collections.Generic.List[string]
foreach ($entry in $currentMap.GetEnumerator()) {
    $key = [string]$entry.Key
    if ($key -ceq (Get-TranslationLegacyBoundaryKey)) {
        continue
    }

    if (-not $extractedKeySet.Contains($key)) {
        $unusedKeys.Add($key)
    }
}

$preservedLegacyMap = New-OrderedStringMap
$misplacedLegacyKeys = New-Object System.Collections.Generic.List[string]
foreach ($entry in $currentSections.ActiveMap.GetEnumerator()) {
    $key = [string]$entry.Key
    if (-not $extractedKeySet.Contains($key)) {
        $misplacedLegacyKeys.Add($key)
    }
}

$activeKeysBelowBoundary = New-Object System.Collections.Generic.List[string]
foreach ($entry in $currentSections.LegacyMap.GetEnumerator()) {
    $key = [string]$entry.Key
    if ($extractedKeySet.Contains($key)) {
        $activeKeysBelowBoundary.Add($key)
    }
}

foreach ($entry in $currentMap.GetEnumerator()) {
    $key = [string]$entry.Key
    if ($key -ceq (Get-TranslationLegacyBoundaryKey)) {
        continue
    }

    if (-not $extractedKeySet.Contains($key)) {
        $preservedLegacyMap[$key] = [string]$entry.Value
    }
}

$syncedActiveMap = New-OrderedStringMap
foreach ($key in $snapshot.KeyOrder) {
    $syncedActiveMap[$key] = if ($currentKeySet.Contains($key)) { [string]$currentMap[$key] } else { $key }
}

$syncedMap = if ($preserveLegacyBoundary) {
    Join-TranslationMapWithBoundary -ActiveMap $syncedActiveMap -LegacyMap $preservedLegacyMap -IncludeBoundary
}
else {
    $syncedActiveMap
}

$hasChanges = -not (Test-OrderedStringMapsEqual -Left $currentMap -Right $syncedMap)

if (-not [string]::IsNullOrWhiteSpace($InventoryOutputPath)) {
    $resolvedInventoryOutputPath = Get-FullPath -Path $InventoryOutputPath
    $inventory = [pscustomobject]@{
        generatedAt = (Get-Date).ToString('o')
        repositoryRoot = $resolvedRepositoryRoot
        englishFile = (Get-RepoRelativePath -RepositoryRoot $resolvedRepositoryRoot -FilePath $resolvedEnglishFilePath)
        legacyBoundaryEnabled = $preserveLegacyBoundary
        legacyBoundaryPresent = $currentSections.HasBoundary
        legacyBoundaryKey = Get-TranslationLegacyBoundaryKey
        scannedSourceFileCount = @($snapshot.SourceFiles).Count
        extractedKeyCount = @($snapshot.KeyOrder).Count
        activeKeyCount = $syncedActiveMap.Count
        legacyKeyCount = $preservedLegacyMap.Count
        missingKeyCount = $missingKeys.Count
        unusedKeyCount = $unusedKeys.Count
        misplacedLegacyKeyCount = $misplacedLegacyKeys.Count
        activeKeysBelowBoundaryCount = $activeKeysBelowBoundary.Count
        warningCount = @($snapshot.Warnings).Count
        missingKeys = $missingKeys.ToArray()
        unusedKeys = $unusedKeys.ToArray()
        legacyKeys = @($preservedLegacyMap.Keys)
        misplacedLegacyKeys = $misplacedLegacyKeys.ToArray()
        activeKeysBelowBoundary = $activeKeysBelowBoundary.ToArray()
        warnings = @($snapshot.Warnings | Sort-Object Path, Line, Type | ForEach-Object {
                [pscustomobject]@{
                    Path = $_.Path
                    Line = $_.Line
                    Type = $_.Type
                    Message = $_.Message
                }
            })
    }

    New-Utf8File -Path $resolvedInventoryOutputPath -Content ($inventory | ConvertTo-Json -Depth 6)
}

Write-Output "Translation source synchronization summary"
Write-Output "Repository root: $resolvedRepositoryRoot"
Write-Output "English file: $resolvedEnglishFilePath"
Write-Output "Legacy boundary enabled: $preserveLegacyBoundary"
Write-Output "Legacy boundary present: $($currentSections.HasBoundary)"
Write-Output "Scanned source files: $(@($snapshot.SourceFiles).Count)"
Write-Output "Extracted keys: $(@($snapshot.KeyOrder).Count)"
Write-Output "Missing English keys: $($missingKeys.Count)"
Write-Output "Unused English keys: $($unusedKeys.Count)"
Write-Output "Legacy English keys: $($preservedLegacyMap.Count)"
Write-Output "Misplaced legacy keys: $($misplacedLegacyKeys.Count)"
Write-Output "Active keys below boundary: $($activeKeysBelowBoundary.Count)"
Write-Output "Warnings: $(@($snapshot.Warnings).Count)"

if ($missingKeys.Count -gt 0) {
    Write-Output ''
    Write-Output 'Missing keys to add:'
    foreach ($key in $missingKeys) {
        Write-Output ("  + {0}" -f $key)
    }
}

if ((-not $preserveLegacyBoundary) -and $unusedKeys.Count -gt 0) {
    Write-Output ''
    Write-Output 'Unused keys to remove:'
    foreach ($key in $unusedKeys) {
        Write-Output ("  - {0}" -f $key)
    }
}

if ($misplacedLegacyKeys.Count -gt 0) {
    Write-Output ''
    Write-Output 'Legacy keys that should move below the boundary:'
    foreach ($key in $misplacedLegacyKeys) {
        Write-Output ("  v {0}" -f $key)
    }
}

if ($activeKeysBelowBoundary.Count -gt 0) {
    Write-Output ''
    Write-Output 'Active keys currently below the boundary:'
    foreach ($key in $activeKeysBelowBoundary) {
        Write-Output ("  ^ {0}" -f $key)
    }
}

if (@($snapshot.Warnings).Count -gt 0) {
    Write-Output ''
    Write-Output 'Warnings:'
    foreach ($warning in $snapshot.Warnings | Sort-Object Path, Line, Type) {
        Write-Output ("  ! {0}:{1} [{2}] {3}" -f $warning.Path, $warning.Line, $warning.Type, $warning.Message)
    }
}

if ($CheckOnly) {
    if ($hasChanges) {
        throw 'lang_en.json is out of sync with extracted translation sources.'
    }

    return
}

if (-not $hasChanges) {
    Write-Output ''
    Write-Output 'lang_en.json is already synchronized with extracted source usage.'
    return
}

if ($PSCmdlet.ShouldProcess($resolvedEnglishFilePath, 'Synchronize English translation keys from source usage')) {
    Write-OrderedJsonMap -Path $resolvedEnglishFilePath -Map $syncedMap
    Write-Output ''
    Write-Output 'lang_en.json was synchronized successfully.'
}