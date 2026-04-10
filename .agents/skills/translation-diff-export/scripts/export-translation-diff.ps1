param(
    [Parameter(Mandatory = $true)]
    [string]$NeutralJson,

    [Parameter(Mandatory = $true)]
    [string]$TargetJson,

    [Parameter(Mandatory = $true)]
    [string]$Language,

    [string]$BaseRef,

    [string]$OutputDir = 'generated/translation-diff-export',

    [switch]$ActiveOnly,

    [switch]$KeepIntermediate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'TranslationDiff.JsonTools.ps1')

function Get-GitJsonMap {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory = $true)]
        [string]$BaseRef,

        [Parameter(Mandatory = $true)]
        [string]$RepoRelativePath
    )

    $gitSpec = '{0}:{1}' -f $BaseRef, $RepoRelativePath
    $rawContent = & git -C $RepositoryRoot show $gitSpec 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    $jsonText = [string]::Join([Environment]::NewLine, @($rawContent))
    if ([string]::IsNullOrWhiteSpace($jsonText)) {
        return $null
    }

    $tempPath = Join-Path $env:TEMP ('translation-diff-{0}.json' -f [System.Guid]::NewGuid().ToString('N'))
    try {
        New-Utf8File -Path $tempPath -Content $jsonText
        return Read-OrderedJsonMap -Path $tempPath
    }
    finally {
        Remove-Item -Path $tempPath -Force -ErrorAction SilentlyContinue
    }
}

function Get-CirupKeySet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $rows = Invoke-CirupJson -Arguments $Arguments
    $keys = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::Ordinal)
    foreach ($row in $rows) {
        if (-not ($row -is [System.Collections.IDictionary]) -or -not $row.Contains('name')) {
            throw 'cirup returned an unexpected JSON payload.'
        }

        [void]$keys.Add([string]$row['name'])
    }

    return $keys
}

function Get-EmptyTargetKeys {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$SourceMap,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$TargetMap
    )

    $keys = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::Ordinal)
    foreach ($entry in $SourceMap.GetEnumerator()) {
        $key = [string]$entry.Key
        if (-not $TargetMap.Contains($key)) {
            continue
        }

        if ([string]::IsNullOrWhiteSpace([string]$TargetMap[$key])) {
            [void]$keys.Add($key)
        }
    }

    return $keys
}

function Test-IntentionalSourceEqualValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LanguageCode,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$SourceValue,

        [Parameter(Mandatory = $false)]
        [AllowEmptyString()]
        [string]$TargetValue = ''
    )

    if ($SourceValue -in @(
        'OK',
        'UniGetUI',
        'UniGetUI - {0} {1}'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'it' -and $SourceValue -ceq 'No' -and $TargetValue -ceq 'No') {
        return $true
    }

    if ($LanguageCode -ceq 'ca' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        '1 - Errors',
        'Error',
        'Global',
        'Local',
        'Manifest',
        'Manifests',
        'No',
        'Notes:',
        'Text'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'pt_PT' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Global',
        'Local'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'hr' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Manifest',
        'Status',
        'URL'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'es' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Error',
        'Global',
        'Local',
        'No',
        'URL'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'fr' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Ascendant',
        'Descendant',
        'Global',
        'Local',
        'Portable',
        'Source',
        'Sources',
        'Verbose',
        'Version',
        'installation',
        'option',
        'version {0}',
        '{0} minutes'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'nl' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        '1 week',
        'Filters',
        'Manifest',
        'Status',
        'Updates',
        'URL',
        'update',
        'website',
        '{0} status'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'sk' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Manifest',
        'Text'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'de' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Global',
        'Manifest',
        'Name',
        'Repository',
        'Start',
        'Status',
        'Text',
        'Updates',
        'URL',
        'Verbose',
        'Version',
        'Version:',
        'optional',
        '{package} Installation',
        '{package} Update'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'sv' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Global',
        'Manifest',
        'Start',
        'Status',
        'Text',
        'Version',
        'Version:',
        'installation',
        'version {0}',
        '{0} installation',
        '{0} status',
        '{pm} version:'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'id' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Global',
        'Grid',
        'Manifest',
        'Status',
        'URL',
        'Verbose',
        '{0} status'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'fil' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Android Subsystem',
        'Default',
        'Error',
        'Global',
        'Grid',
        'Machine | Global',
        'Manifest',
        'OK',
        'Ok',
        'Package',
        'Password',
        'Portable',
        'Portable mode',
        'PreRelease',
        'Repository',
        'Source',
        'Source:',
        'Telemetry',
        'Text',
        'URL',
        'UniGetUI',
        'UniGetUI - {0} {1}',
        'User',
        'Username',
        'Verbose',
        'website',
        'library'
    )) {
        return $true
    }

    return $false
}

function Sync-TranslatedWorkingCopy {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$CurrentSourceMap,

        [Parameter(Mandatory = $true)]
        [string]$TranslatedPatchPath,

        [System.Collections.IDictionary]$PreviousSourceMap
    )

    $translatedMap = Read-OrderedJsonMap -Path $TranslatedPatchPath
    $syncedMap = New-OrderedStringMap

    foreach ($entry in $CurrentSourceMap.GetEnumerator()) {
        $key = [string]$entry.Key
        $sourceValue = [string]$entry.Value
        if (-not $translatedMap.Contains($key)) {
            continue
        }

        $translatedValue = [string]$translatedMap[$key]
        if ([string]::IsNullOrWhiteSpace($translatedValue)) {
            continue
        }

        if ($translatedValue -ceq $sourceValue) {
            continue
        }

        if ($null -ne $PreviousSourceMap -and $PreviousSourceMap.Contains($key)) {
            if ($translatedValue -ceq [string]$PreviousSourceMap[$key]) {
                continue
            }
        }

        $syncedMap[$key] = $translatedValue
    }

    Write-OrderedJsonMap -Path $TranslatedPatchPath -Map $syncedMap
}

function Get-FilteredPatchMap {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Map,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$NeutralMap,

        [Parameter(Mandatory = $true)]
        [switch]$ActiveOnly
    )

    if (-not $ActiveOnly.IsPresent) {
        return $Map
    }

    $activeKeys = Split-TranslationMapAtBoundary -Map $NeutralMap
    $filteredMap = New-OrderedStringMap
    foreach ($entry in $activeKeys.ActiveMap.GetEnumerator()) {
        $key = [string]$entry.Key
        if ($Map.Contains($key)) {
            $filteredMap[$key] = [string]$Map[$key]
        }
    }

    return $filteredMap
}

Assert-Command -Name 'git'
Assert-Command -Name 'cirup'

$neutralPath = Get-FullPath -Path $NeutralJson
$targetPath = Get-FullPath -Path $TargetJson
if (-not (Test-Path -Path $neutralPath -PathType Leaf)) {
    throw "Neutral JSON file not found: $neutralPath"
}

$languageCode = $Language.Trim()
if ([string]::IsNullOrWhiteSpace($languageCode)) {
    throw 'Language must not be empty.'
}

$languagesReferencePath = Get-LanguagesReferencePath -NeutralJsonPath $neutralPath
Assert-LanguageCodeKnown -LanguagesReferencePath $languagesReferencePath -LanguageCode $languageCode

$outputRoot = Get-FullPath -Path $OutputDir
$tmpRoot = Join-Path $outputRoot 'tmp'
New-Item -Path $outputRoot -ItemType Directory -Force | Out-Null
New-Item -Path $tmpRoot -ItemType Directory -Force | Out-Null

$cirupTargetPath = $targetPath
if (-not (Test-Path -Path $targetPath -PathType Leaf)) {
    $cirupTargetPath = Join-Path $tmpRoot 'target.empty.json'
    Write-OrderedJsonMap -Path $cirupTargetPath -Map (New-OrderedStringMap)
}

$baseName = Get-PatchBaseName -NeutralJsonPath $neutralPath
$sourcePatchPath = Join-Path $outputRoot ('{0}.diff.{1}.source.json' -f $baseName, $languageCode)
$translatedPatchPath = Join-Path $outputRoot ('{0}.diff.{1}.translated.json' -f $baseName, $languageCode)
$referencePatchPath = Join-Path $outputRoot ('{0}.diff.{1}.reference.json' -f $baseName, $languageCode)
$promptPath = Join-Path $outputRoot ('{0}.diff.{1}.prompt.md' -f $baseName, $languageCode)

$neutralMap = Read-OrderedJsonMap -Path $neutralPath
$targetMap = Read-OrderedJsonMap -Path $targetPath
$baseSummary = 'No git baseline was provided. The patch contains untranslated UniGetUI entries only.'
$changedSourceKeys = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::Ordinal)
$missingTargetKeys = Get-CirupKeySet -Arguments @('file-diff', $neutralPath, $cirupTargetPath)
$rawEqualEnglishKeys = Get-CirupKeySet -Arguments @('file-intersect', $neutralPath, $cirupTargetPath)
$equalEnglishKeys = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::Ordinal)
$rawEqualEnglishKeys | ForEach-Object {
    $key = [string]$_
    if (-not (Test-IntentionalSourceEqualValue -LanguageCode $Language -SourceValue ([string]$neutralMap[$key]) -TargetValue ([string]$targetMap[$key]))) {
        [void]$equalEnglishKeys.Add($key)
    }
}
$emptyTargetKeys = Get-EmptyTargetKeys -SourceMap $neutralMap -TargetMap $targetMap

if (-not [string]::IsNullOrWhiteSpace($BaseRef)) {
    $repoRoot = Get-RepositoryRoot -WorkingDirectory (Split-Path -Path $neutralPath -Parent)
    $repoRelativeNeutralPath = Get-RepoRelativePath -RepositoryRoot $repoRoot -FilePath $neutralPath
    $baseSourceMap = Get-GitJsonMap -RepositoryRoot $repoRoot -BaseRef $BaseRef -RepoRelativePath $repoRelativeNeutralPath
    $safeBaseRef = Get-SafeLabel -Value $BaseRef
    $baseSnapshotPath = Join-Path $tmpRoot ('{0}.baseline.{1}.json' -f $baseName, $safeBaseRef)

    if ($null -ne $baseSourceMap) {
        Write-OrderedJsonMap -Path $baseSnapshotPath -Map $baseSourceMap
        $baseSummary = "Git baseline '$BaseRef' was used to include keys that are new or whose English value changed since that revision."
    }
    else {
        Write-OrderedJsonMap -Path $baseSnapshotPath -Map (New-OrderedStringMap)
        $baseSummary = "Git baseline '$BaseRef' did not contain the neutral JSON file, so all current English entries were treated as new for the changed-source delta."
    }

    $changedSourceKeys = Get-CirupKeySet -Arguments @('diff-with-base', $baseSnapshotPath, $neutralPath, $neutralPath)
}

$sourcePatchMap = New-OrderedStringMap
$referencePatchMap = New-OrderedStringMap

$keysToTranslate = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::Ordinal)
foreach ($key in $missingTargetKeys) {
    [void]$keysToTranslate.Add([string]$key)
}

foreach ($key in $equalEnglishKeys) {
    [void]$keysToTranslate.Add([string]$key)
}

foreach ($key in $emptyTargetKeys) {
    [void]$keysToTranslate.Add([string]$key)
}

foreach ($key in $changedSourceKeys) {
    [void]$keysToTranslate.Add([string]$key)
}

foreach ($entry in $neutralMap.GetEnumerator()) {
    $key = [string]$entry.Key
    $sourceValue = [string]$entry.Value

    if ($keysToTranslate.Contains($key)) {
        $sourcePatchMap[$key] = $sourceValue
        continue
    }

    if ($targetMap.Contains($key)) {
        $targetValue = [string]$targetMap[$key]
        if (-not [string]::IsNullOrWhiteSpace($targetValue) -and $targetValue -cne $sourceValue) {
            $referencePatchMap[$key] = $targetValue
        }
    }
}

$sourcePatchMap = Get-FilteredPatchMap -Map $sourcePatchMap -NeutralMap $neutralMap -ActiveOnly:$ActiveOnly
$referencePatchMap = Get-FilteredPatchMap -Map $referencePatchMap -NeutralMap $neutralMap -ActiveOnly:$ActiveOnly

if (Test-Path -Path $sourcePatchPath -PathType Leaf) {
    $previousSourceMap = Read-OrderedJsonMap -Path $sourcePatchPath
}
else {
    $previousSourceMap = $null
}

Write-OrderedJsonMap -Path $sourcePatchPath -Map $sourcePatchMap
Write-OrderedJsonMap -Path $referencePatchPath -Map $referencePatchMap
Sync-TranslatedWorkingCopy -CurrentSourceMap $sourcePatchMap -TranslatedPatchPath $translatedPatchPath -PreviousSourceMap $previousSourceMap

$mergedTargetPath = Join-Path ([System.IO.Path]::GetDirectoryName($targetPath)) ('{0}.merged.json' -f [System.IO.Path]::GetFileNameWithoutExtension($targetPath))
$skillsRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$translateHandoffScript = Join-Path $skillsRoot 'translation-diff-translate\scripts\write-translation-handoff.ps1'
$validationScript = Join-Path $skillsRoot 'translation-diff-import\scripts\validate-language-file.ps1'
if (-not (Test-Path -Path $translateHandoffScript -PathType Leaf)) {
    throw "Translation handoff script not found: $translateHandoffScript"
}

& $translateHandoffScript -BaseName $baseName -Language $languageCode -SourcePatch $sourcePatchPath -TranslatedPatch $translatedPatchPath -ReferencePatch $referencePatchPath -TargetJson $targetPath -NeutralJson $neutralPath -MergedTargetJson $mergedTargetPath -ValidationScript $validationScript -OutputPrompt $promptPath -BaseSummary $baseSummary

if (-not $KeepIntermediate.IsPresent) {
    Remove-Item -Path $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output "Created source patch: $sourcePatchPath"
Write-Output "Refreshed translated working copy: $translatedPatchPath"
Write-Output "Created reference patch: $referencePatchPath"
Write-Output "Created translation handoff prompt: $promptPath"
if ($ActiveOnly.IsPresent) {
    Write-Output 'Patch scope: active translation keys only'
}
if ($KeepIntermediate.IsPresent) {
    Write-Output "Kept intermediate files under: $tmpRoot"
}