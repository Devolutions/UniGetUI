[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$WorkDirectory,

    [string]$TranslatedDirectory,

    [string]$SummaryPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Languages\TranslationJsonTools.ps1')
. (Join-Path $PSScriptRoot 'Languages\IntentionalSourceEqualValues.ps1')

$repositoryRoot = Get-FullPath -Path (Join-Path $PSScriptRoot '..\..')
$resolvedWorkDirectory = Get-FullPath -Path $WorkDirectory
$resolvedTranslatedDirectory = if ([string]::IsNullOrWhiteSpace($TranslatedDirectory)) { $resolvedWorkDirectory } else { Get-FullPath -Path $TranslatedDirectory }
$planPath = Join-Path $resolvedWorkDirectory 'plan.json'
if (-not (Test-Path -Path $planPath -PathType Leaf)) {
    throw "Translation plan not found: $planPath"
}

$plan = @(Get-Content -Path $planPath -Raw | ConvertFrom-Json)
$results = New-Object System.Collections.Generic.List[object]

foreach ($item in $plan) {
    if ([string]$item.languageFile -notmatch '^src[\\/]Languages[\\/]lang_[A-Za-z_-]+\.json$' -or [string]$item.code -notmatch '^[A-Za-z_-]+$') {
        throw "Unexpected language entry in the translation plan: $($item.code) -> $($item.languageFile)"
    }

    $sourcePatch = Read-OrderedJsonMap -Path $item.sourcePatch
    $rejected = New-Object System.Collections.Generic.List[string]
    $accepted = New-OrderedStringMap

    $translatedPatchFiles = @()
    if (Test-Path -Path $resolvedTranslatedDirectory -PathType Container) {
        $translatedPatchFiles = @(Get-ChildItem -Path $resolvedTranslatedDirectory -File |
                Where-Object { $_.Name -ceq "$($item.code).translated.json" -or $_.Name -cmatch ('^' + [regex]::Escape($item.code) + '\.translated\.\d+\.json$') } |
                Sort-Object -Property @{ Expression = { if ($_.Name -match '\.(\d+)\.json$') { [int]$Matches[1] } else { 0 } } })
    }

    foreach ($translatedPatchFile in $translatedPatchFiles) {
        try {
            $translatedPatch = Read-OrderedJsonMap -Path $translatedPatchFile.FullName
        }
        catch {
            $translatedPatch = New-OrderedStringMap
            $rejected.Add("$($translatedPatchFile.Name) could not be parsed: $($_.Exception.Message)")
        }

        foreach ($entry in $translatedPatch.GetEnumerator()) {
            $key = [string]$entry.Key
            $value = [string]$entry.Value

            if (-not $sourcePatch.Contains($key)) {
                $rejected.Add("``$key``: not part of this batch")
                continue
            }

            if ([string]::IsNullOrWhiteSpace($value)) {
                continue
            }

            try {
                Assert-TranslationStructure -SourceValue ([string]$sourcePatch[$key]) -TranslatedValue $value -Key $key
            }
            catch {
                $rejected.Add("``$key``: $($_.Exception.Message)")
                continue
            }

            $accepted[$key] = $value
        }
    }

    $sameAsEnglish = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $accepted.GetEnumerator()) {
        $sourceValue = [string]$sourcePatch[[string]$entry.Key]
        if ([string]$entry.Value -ceq $sourceValue -and -not (Test-IntentionalSourceEqualValue -LanguageCode $item.code -SourceValue $sourceValue -TargetValue ([string]$entry.Value))) {
            $sameAsEnglish.Add([string]$entry.Key)
        }
    }

    if ($accepted.Count -gt 0) {
        $languageFilePath = Join-Path $repositoryRoot $item.languageFile
        $layout = Get-JsonFileLayout -Path $languageFilePath
        $targetMap = Read-OrderedJsonMap -Path $languageFilePath
        foreach ($entry in $accepted.GetEnumerator()) {
            $targetMap[[string]$entry.Key] = [string]$entry.Value
        }

        Write-OrderedJsonMap -Path $languageFilePath -Map $targetMap -Indent $layout.Indent -Newline $layout.Newline
    }

    $results.Add([pscustomobject]@{
            Code = $item.code
            Language = $item.language
            Exported = [int]$item.exported
            Imported = $accepted.Count
            PendingAfter = [int]$item.pendingTotal - $accepted.Count
            Rejected = $rejected
            SameAsEnglish = $sameAsEnglish
        })
}

$summary = New-Object System.Collections.Generic.List[string]
$summary.Add('| Language | Translated | Same as English | Rejected | Still missing |')
$summary.Add('|---|---:|---:|---:|---:|')
foreach ($result in $results) {
    $summary.Add(('| {0} (`{1}`) | {2} | {3} | {4} | {5} |' -f $result.Language, $result.Code, $result.Imported, $result.SameAsEnglish.Count, $result.Rejected.Count, $result.PendingAfter))
}

foreach ($result in $results | Where-Object { $_.SameAsEnglish.Count -gt 0 }) {
    $summary.Add('')
    $summary.Add("Kept identical to English for ``$($result.Code)`` (check that these are names or words that really do not change):")
    foreach ($key in $result.SameAsEnglish) {
        $summary.Add("- ``$key``")
    }
}

foreach ($result in $results | Where-Object { $_.Rejected.Count -gt 0 }) {
    $summary.Add('')
    $summary.Add("Rejected for ``$($result.Code)``:")
    foreach ($reason in $result.Rejected) {
        $summary.Add("- $reason")
    }
}

$summaryText = ($summary -join "`n") + "`n"
if (-not [string]::IsNullOrWhiteSpace($SummaryPath)) {
    New-Utf8File -Path $SummaryPath -Content $summaryText
}

Write-Output $summaryText
$importedTotal = 0
foreach ($result in $results) {
    $importedTotal += $result.Imported
}

Write-Output "Imported $importedTotal translation(s) into $(@($results | Where-Object { $_.Imported -gt 0 }).Count) language file(s)."
