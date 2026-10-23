[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [ValidateRange(1, [int]::MaxValue)]
    [int]$MaxLanguages = [int]::MaxValue,

    [ValidateRange(1, [int]::MaxValue)]
    [int]$MaxKeysPerLanguage = [int]::MaxValue,

    [ValidateRange(0, 500)]
    [int]$ReferenceEntryCount = 60,

    [string[]]$LanguageCodes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Languages\TranslationJsonTools.ps1')

$LanguageCodes = @($LanguageCodes | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ })

$repositoryRoot = Get-FullPath -Path (Join-Path $PSScriptRoot '..\..')
$languagesDirectory = Join-Path $repositoryRoot 'src\Languages'
$englishFilePath = Join-Path $languagesDirectory 'lang_en.json'
$languageNames = Read-OrderedJsonMap -Path (Join-Path $languagesDirectory 'Data\LanguagesReference.json')

$englishSections = Split-TranslationMapAtBoundary -Map (Read-OrderedJsonMap -Path $englishFilePath)
$englishMap = $englishSections.ActiveMap

$candidates = New-Object System.Collections.Generic.List[object]
foreach ($languageFile in Get-ChildItem -Path $languagesDirectory -Filter 'lang_*.json' -File | Sort-Object Name) {
    $code = $languageFile.BaseName.Substring('lang_'.Length)
    if ($code -ceq 'en') {
        continue
    }

    if ($LanguageCodes -and $code -notin $LanguageCodes) {
        continue
    }

    $targetMap = Read-OrderedJsonMap -Path $languageFile.FullName
    $pendingKeys = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $englishMap.GetEnumerator()) {
        $key = [string]$entry.Key
        if (-not $targetMap.Contains($key) -or [string]::IsNullOrWhiteSpace([string]$targetMap[$key])) {
            $pendingKeys.Add($key)
        }
    }

    if ($pendingKeys.Count -eq 0) {
        continue
    }

    $candidates.Add([pscustomobject]@{
            Code = $code
            Path = $languageFile.FullName
            TargetMap = $targetMap
            PendingKeys = $pendingKeys
        })
}

$selected = @($candidates | Sort-Object -Property @{ Expression = { $_.PendingKeys.Count }; Descending = $true }, @{ Expression = { $_.Code }; Descending = $false } | Select-Object -First $MaxLanguages)

New-Item -Path $OutputDirectory -ItemType Directory -Force | Out-Null
$resolvedOutputDirectory = Get-FullPath -Path $OutputDirectory

$plan = New-Object System.Collections.Generic.List[object]
foreach ($candidate in $selected) {
    $sourcePatch = New-OrderedStringMap
    foreach ($key in ($candidate.PendingKeys | Select-Object -First $MaxKeysPerLanguage)) {
        $sourcePatch[$key] = [string]$englishMap[$key]
    }

    $referencePatch = New-OrderedStringMap
    foreach ($entry in $candidate.TargetMap.GetEnumerator()) {
        if ($referencePatch.Count -ge $ReferenceEntryCount) {
            break
        }

        $key = [string]$entry.Key
        $value = [string]$entry.Value
        if ($englishMap.Contains($key) -and -not [string]::IsNullOrWhiteSpace($value) -and $value -cne $key) {
            $referencePatch[$key] = $value
        }
    }

    $sourcePath = Join-Path $resolvedOutputDirectory "$($candidate.Code).source.json"
    $referencePath = Join-Path $resolvedOutputDirectory "$($candidate.Code).reference.json"
    Write-OrderedJsonMap -Path $sourcePath -Map $sourcePatch -Newline "`n"
    Write-OrderedJsonMap -Path $referencePath -Map $referencePatch -Newline "`n"

    $languageName = if ($languageNames.Contains($candidate.Code)) { [string]$languageNames[$candidate.Code] } else { $candidate.Code }
    $plan.Add([pscustomobject]@{
            code = $candidate.Code
            language = $languageName
            languageFile = Get-RepoRelativePath -RepositoryRoot $repositoryRoot -FilePath $candidate.Path
            pendingTotal = $candidate.PendingKeys.Count
            exported = $sourcePatch.Count
            sourcePatch = $sourcePath
            referencePatch = $referencePath
            translatedPatch = Join-Path $resolvedOutputDirectory "$($candidate.Code).translated.json"
        })
}

New-Utf8File -Path (Join-Path $resolvedOutputDirectory 'plan.json') -Content (ConvertTo-Json -InputObject $plan.ToArray() -Depth 4)

$totalPending = 0
foreach ($candidate in $candidates) {
    $totalPending += $candidate.PendingKeys.Count
}

Write-Output "Languages with pending keys: $($candidates.Count)"
Write-Output "Pending entries across all languages: $totalPending"
Write-Output "Selected for this batch: $($plan.Count)"
foreach ($item in $plan) {
    Write-Output ("  {0} ({1}): {2} of {3} pending keys exported" -f $item.code, $item.language, $item.exported, $item.pendingTotal)
}
