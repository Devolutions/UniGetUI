[CmdletBinding()]
param(
    [ValidateSet('Table', 'Json', 'Markdown')]
    [string]$OutputFormat = 'Table',

    [switch]$IncludeEnglish,

    [switch]$OnlyIncomplete,

    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'Languages\LanguageData.psm1') -Force
. (Join-Path $PSScriptRoot 'Languages\TranslationJsonTools.ps1')

function Read-LanguageMap {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -Path $Path -PathType Leaf)) {
        return (New-OrderedStringMap)
    }

    return (Read-OrderedJsonMap -Path $Path)
}

function Get-LanguageMapSections {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Map
    )

    $sections = Split-TranslationMapAtBoundary -Map $Map
    $fullMap = Join-TranslationMapWithBoundary -ActiveMap $sections.ActiveMap -LegacyMap $sections.LegacyMap

    return [pscustomobject]@{
        HasBoundary = $sections.HasBoundary
        FullMap = $fullMap
        ActiveMap = $sections.ActiveMap
        LegacyMap = $sections.LegacyMap
    }
}

function Get-PercentageNumber {
    param(
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value) {
        return $null
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    $trimmed = $text.Trim()
    if ($trimmed.EndsWith('%', [System.StringComparison]::Ordinal)) {
        $trimmed = $trimmed.Substring(0, $trimmed.Length - 1)
    }

    $result = 0
    if ([int]::TryParse($trimmed, [ref]$result)) {
        return $result
    }

    return $null
}

function Get-CompletionPercentage {
    param(
        [int]$Completed,
        [int]$Total
    )

    if ($Total -le 0) {
        return 0
    }

    return [int][Math]::Round(($Completed / $Total) * 100, 0, [MidpointRounding]::AwayFromZero)
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

function Get-TranslationStatusRow {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LanguageCode,

        [Parameter(Mandatory = $true)]
        [string]$LanguageName,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$NeutralMap,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$NeutralLegacyMap,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$TargetMap,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$TargetLegacyMap,

        [AllowNull()]
        [Nullable[int]]$StoredPercentage,

        [Parameter(Mandatory = $true)]
        [bool]$HasFile,

        [Parameter(Mandatory = $true)]
        [bool]$HasBoundary
    )

    $totalKeys = $NeutralMap.Count
    $missingKeys = 0
    $emptyKeys = 0
    $sourceEqualKeys = 0
    $translatedKeys = 0
    $extraKeys = 0

    foreach ($entry in $NeutralMap.GetEnumerator()) {
        $key = [string]$entry.Key
        $sourceValue = if ($null -eq $entry.Value) { '' } else { [string]$entry.Value }

        if (-not $TargetMap.Contains($key)) {
            $missingKeys += 1
            continue
        }

        $targetValue = if ($null -eq $TargetMap[$key]) { '' } else { [string]$TargetMap[$key] }
        if ([string]::IsNullOrWhiteSpace($targetValue)) {
            $emptyKeys += 1
            continue
        }

        if ($LanguageCode -ne 'en' -and $targetValue -ceq $sourceValue) {
            if (Test-IntentionalSourceEqualValue -LanguageCode $LanguageCode -SourceValue $sourceValue -TargetValue $targetValue) {
                $translatedKeys += 1
                continue
            }

            $sourceEqualKeys += 1
            continue
        }

        $translatedKeys += 1
    }

    $knownKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($key in $NeutralMap.Keys) {
        [void]$knownKeys.Add([string]$key)
    }

    foreach ($key in $NeutralLegacyMap.Keys) {
        [void]$knownKeys.Add([string]$key)
    }

    foreach ($key in $TargetMap.Keys) {
        if (-not $knownKeys.Contains([string]$key)) {
            $extraKeys += 1
        }
    }

    $completion = Get-CompletionPercentage -Completed $translatedKeys -Total $totalKeys
    $storedText = if ($null -eq $StoredPercentage) { '' } else { '{0}%' -f $StoredPercentage }
    $delta = if ($null -eq $StoredPercentage) { $null } else { $completion - $StoredPercentage }

    return [pscustomobject]@{
        Code = $LanguageCode
        Language = $LanguageName
        HasFile = $HasFile
        HasBoundary = $HasBoundary
        TotalKeys = $totalKeys
        ActiveKeys = $totalKeys
        Legacy = $TargetLegacyMap.Count
        Translated = $translatedKeys
        Missing = $missingKeys
        Empty = $emptyKeys
        SourceEqual = $sourceEqualKeys
        Untranslated = $missingKeys + $emptyKeys + $sourceEqualKeys
        Extra = $extraKeys
        Completion = '{0}%' -f $completion
        CompletionValue = $completion
        Stored = $storedText
        StoredValue = $StoredPercentage
        Delta = $delta
    }
}

function Convert-RowsToMarkdown {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Rows
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('| Code | Language | Completion | Stored | Delta | Translated | Missing | Empty | Source Equal | Extra |')
    $lines.Add('| :-- | :-- | --: | --: | --: | --: | --: | --: | --: | --: |')

    foreach ($row in $Rows) {
        $deltaText = if ($null -eq $row.Delta) { '' } elseif ($row.Delta -gt 0) { '+{0}' -f $row.Delta } else { [string]$row.Delta }
        $storedText = if ([string]::IsNullOrWhiteSpace([string]$row.Stored)) { '' } else { [string]$row.Stored }
        $lines.Add("| $($row.Code) | $($row.Language) | $($row.Completion) | $storedText | $deltaText | $($row.Translated) | $($row.Missing) | $($row.Empty) | $($row.SourceEqual) | $($row.Extra) |")
    }

    return ($lines -join [Environment]::NewLine)
}

function Write-OutputContent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        Write-Output $Content
        return
    }

    $resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    $directory = Split-Path -Path $resolvedOutputPath -Parent
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -Path $directory -ItemType Directory -Force | Out-Null
    }

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($resolvedOutputPath, $Content + [Environment]::NewLine, $encoding)
    Write-Output "Wrote translation status summary to: $resolvedOutputPath"
}

function Get-OverviewLines {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Rows
    )

    if ($Rows.Count -eq 0) {
        return @('Languages: 0', 'Incomplete: 0', 'Fully translated: 0')
    }

    $incompleteCount = @($Rows | Where-Object { $_.Untranslated -gt 0 }).Count
    $completeCount = @($Rows | Where-Object { $_.Untranslated -eq 0 }).Count
    $totalUntranslated = ($Rows | Measure-Object -Property Untranslated -Sum).Sum
    $totalMissing = ($Rows | Measure-Object -Property Missing -Sum).Sum
    $totalEmpty = ($Rows | Measure-Object -Property Empty -Sum).Sum
    $totalSourceEqual = ($Rows | Measure-Object -Property SourceEqual -Sum).Sum

    return @(
        ('Languages: {0}' -f $Rows.Count),
        ('Incomplete: {0}' -f $incompleteCount),
        ('Fully translated: {0}' -f $completeCount),
        ('Outstanding entries: {0} (missing {1}, empty {2}, source-equal {3})' -f $totalUntranslated, $totalMissing, $totalEmpty, $totalSourceEqual)
    )
}

$languagesDirectory = Get-LanguagesDirectoryPath
$neutralPath = Join-Path $languagesDirectory 'lang_en.json'
if (-not (Test-Path -Path $neutralPath -PathType Leaf)) {
    throw "Neutral language file not found: $neutralPath"
}

$neutralSections = Get-LanguageMapSections -Map (Read-LanguageMap -Path $neutralPath)
$neutralMap = $neutralSections.ActiveMap
$neutralLegacyMap = $neutralSections.LegacyMap
$languageReference = Get-LanguageReference
$storedPercentages = Get-TranslatedPercentages
$rows = New-Object System.Collections.Generic.List[object]

foreach ($entry in $languageReference.GetEnumerator()) {
    $languageCode = [string]$entry.Key
    if ($languageCode -eq 'default') {
        continue
    }

    if (-not $IncludeEnglish.IsPresent -and $languageCode -eq 'en') {
        continue
    }

    $languageFilePath = Join-Path $languagesDirectory ("lang_{0}.json" -f $languageCode)
    $hasFile = Test-Path -Path $languageFilePath -PathType Leaf
    $targetSections = if ($hasFile) { Get-LanguageMapSections -Map (Read-LanguageMap -Path $languageFilePath) } else { [pscustomobject]@{ HasBoundary = $false; FullMap = (New-OrderedStringMap); ActiveMap = (New-OrderedStringMap); LegacyMap = (New-OrderedStringMap) } }
    $storedPercentage = if ($storedPercentages.Contains($languageCode)) { Get-PercentageNumber -Value $storedPercentages[$languageCode] } elseif ($languageCode -eq 'en') { 100 } else { $null }

    $row = Get-TranslationStatusRow -LanguageCode $languageCode -LanguageName ([string]$entry.Value) -NeutralMap $neutralMap -NeutralLegacyMap $neutralLegacyMap -TargetMap $targetSections.FullMap -TargetLegacyMap $targetSections.LegacyMap -StoredPercentage $storedPercentage -HasFile:$hasFile -HasBoundary:$targetSections.HasBoundary
    if ($OnlyIncomplete.IsPresent -and $row.Untranslated -eq 0) {
        continue
    }

    $rows.Add($row)
}

$orderedRows = @(
    $rows |
        Sort-Object @{ Expression = 'CompletionValue'; Descending = $false }, @{ Expression = 'Code'; Descending = $false }
)

switch ($OutputFormat) {
    'Json' {
        $json = $orderedRows | ConvertTo-Json -Depth 5
        Write-OutputContent -Content $json
    }
    'Markdown' {
        $overview = Get-OverviewLines -Rows $orderedRows
        $markdownLines = @(
            '## Translation Status Overview',
            ''
        ) + @($overview | ForEach-Object { '- ' + $_ }) + @(
            '',
            (Convert-RowsToMarkdown -Rows $orderedRows)
        )
        $markdown = $markdownLines -join [Environment]::NewLine
        Write-OutputContent -Content $markdown
    }
    default {
        $overview = Get-OverviewLines -Rows $orderedRows
        $tableText = $orderedRows |
            Select-Object Code, Language, Completion, Stored, Delta, Translated, Missing, Empty, SourceEqual, Extra |
            Format-Table -AutoSize |
            Out-String
        $content = (@(
            $overview
        ) + @(
            '',
            $tableText.TrimEnd()
        )) -join [Environment]::NewLine
        Write-OutputContent -Content $content
    }
}