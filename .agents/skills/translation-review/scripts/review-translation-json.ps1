[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$TargetJson,

    [Parameter(Mandatory)]
    [string]$Language,

    [string]$NeutralJson,
    [string]$OutputPath,
    [string[]]$ComparisonLanguages,

    [ValidateSet('Markdown', 'Json')]
    [string]$OutputFormat = 'Markdown',

    [switch]$FlaggedOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
. (Join-Path $repoRoot 'scripts\translation\Languages\TranslationJsonTools.ps1')

function Get-TranslationReviewLanguageReferencePath {
    return Join-Path $repoRoot 'src\UniGetUI.Core.LanguageEngine\Assets\Data\LanguagesReference.json'
}

function Get-TranslationReviewLanguagesDirectory {
    return Join-Path $repoRoot 'src\UniGetUI.Core.LanguageEngine\Assets\Languages'
}

function Get-TranslationReviewLanguageReference {
    return Read-OrderedJsonMap -Path (Get-TranslationReviewLanguageReferencePath)
}

function Get-TranslationReviewDisplayLanguage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LanguageCode,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$LanguageReference
    )

    if ($LanguageReference.Contains($LanguageCode)) {
        return [string]$LanguageReference[$LanguageCode]
    }

    return $LanguageCode
}

function Test-TranslationReviewTechnicalToken {
    param(
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    if ($Value -match '^\d[\d\s%:./-]*$') { return $true }
    if ($Value -match '^[A-Z0-9]{1,12}$') { return $true }
    if ($Value -match '^https?://') { return $true }
    if ($Value -match '^[A-Z][a-zA-Z0-9]+$' -and $Value.Length -le 30) { return $true }
    if ($Value.Length -le 2) { return $true }

    return $false
}

function Get-TranslationReviewScriptInfo {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LanguageCode
    )

    switch ($LanguageCode) {
        'ar'    { return @{ Name = 'Arabic'; Pattern = '[\u0600-\u06ff]' } }
        'be'    { return @{ Name = 'Cyrillic'; Pattern = '[\u0400-\u04ff]' } }
        'bg'    { return @{ Name = 'Cyrillic'; Pattern = '[\u0400-\u04ff]' } }
        'bn'    { return @{ Name = 'Bengali'; Pattern = '[\u0980-\u09ff]' } }
        'el'    { return @{ Name = 'Greek'; Pattern = '[\u0370-\u03ff]' } }
        'fa'    { return @{ Name = 'Arabic'; Pattern = '[\u0600-\u06ff]' } }
        'he'    { return @{ Name = 'Hebrew'; Pattern = '[\u0590-\u05ff]' } }
        'hi'    { return @{ Name = 'Devanagari'; Pattern = '[\u0900-\u097f]' } }
        'ja'    { return @{ Name = 'Japanese'; Pattern = '[\u3040-\u30ff\u4e00-\u9fff]' } }
        'ka'    { return @{ Name = 'Georgian'; Pattern = '[\u10a0-\u10ff]' } }
        'kn'    { return @{ Name = 'Kannada'; Pattern = '[\u0c80-\u0cff]' } }
        'ko'    { return @{ Name = 'Korean'; Pattern = '[\uac00-\ud7a3]' } }
        'mk'    { return @{ Name = 'Cyrillic'; Pattern = '[\u0400-\u04ff]' } }
        'mr'    { return @{ Name = 'Devanagari'; Pattern = '[\u0900-\u097f]' } }
        'ru'    { return @{ Name = 'Cyrillic'; Pattern = '[\u0400-\u04ff]' } }
        'sa'    { return @{ Name = 'Devanagari'; Pattern = '[\u0900-\u097f]' } }
        'si'    { return @{ Name = 'Sinhala'; Pattern = '[\u0d80-\u0dff]' } }
        'ta'    { return @{ Name = 'Tamil'; Pattern = '[\u0b80-\u0bff]' } }
        'th'    { return @{ Name = 'Thai'; Pattern = '[\u0e00-\u0e7f]' } }
        'ua'    { return @{ Name = 'Cyrillic'; Pattern = '[\u0400-\u04ff]' } }
        'ur'    { return @{ Name = 'Arabic'; Pattern = '[\u0600-\u06ff]' } }
        'zh_CN' { return @{ Name = 'CJK'; Pattern = '[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]' } }
        'zh_TW' { return @{ Name = 'CJK'; Pattern = '[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]' } }
        default { return $null }
    }
}

function Test-TranslationReviewExpectedScript {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LanguageCode,

        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $scriptInfo = Get-TranslationReviewScriptInfo -LanguageCode $LanguageCode
    if ($null -eq $scriptInfo) {
        return $null
    }

    return [pscustomobject]@{
        ExpectedScript = $scriptInfo.Name
        HasExpectedScript = [bool]($Value -match $scriptInfo.Pattern)
    }
}

function Get-TranslationReviewParityIssues {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceValue,

        [Parameter(Mandatory = $true)]
        [string]$TranslatedValue
    )

    $issues = New-Object System.Collections.Generic.List[string]

    $sourcePlaceholderSignature = (Get-PlaceholderTokens -Value $SourceValue) -join "`n"
    $translatedPlaceholderSignature = (Get-PlaceholderTokens -Value $TranslatedValue) -join "`n"
    if ($sourcePlaceholderSignature -ne $translatedPlaceholderSignature) {
        $issues.Add('Placeholder mismatch')
    }

    $sourceHtmlSignature = (Get-HtmlTokens -Value $SourceValue) -join "`n"
    $translatedHtmlSignature = (Get-HtmlTokens -Value $TranslatedValue) -join "`n"
    if ($sourceHtmlSignature -ne $translatedHtmlSignature) {
        $issues.Add('HTML fragment mismatch')
    }

    if ((Get-NewlineCount -Value $SourceValue) -ne (Get-NewlineCount -Value $TranslatedValue)) {
        $issues.Add('Line-break mismatch')
    }

    return @($issues)
}

function Format-TranslationReviewMarkdownCell {
    param(
        [AllowNull()]
        [string]$Value
    )

    if ($null -eq $Value) {
        return '_-_'
    }

    $escapedValue = $Value -replace '\|', '\|'
    if ($escapedValue.Length -gt 120) {
        $escapedValue = $escapedValue.Substring(0, 117) + '...'
    }

    return $escapedValue
}

if (-not $NeutralJson) {
    $NeutralJson = Join-Path (Get-TranslationReviewLanguagesDirectory) 'lang_en.json'
}

$languageReference = Get-TranslationReviewLanguageReference
if (-not $languageReference.Contains($Language)) {
    throw "Unknown language code '$Language'."
}

if (-not $ComparisonLanguages) {
    $ComparisonLanguages = @()
    foreach ($entry in $languageReference.GetEnumerator()) {
        $code = [string]$entry.Key
        if ($code -in @('default', 'en', $Language)) {
            continue
        }

        $comparisonPath = Join-Path (Get-TranslationReviewLanguagesDirectory) ("lang_{0}.json" -f $code)
        if (Test-Path -Path $comparisonPath -PathType Leaf) {
            $ComparisonLanguages += $code
        }
    }
}

if (-not $OutputPath) {
    $extension = if ($OutputFormat -eq 'Json') { 'json' } else { 'md' }
    $OutputPath = Join-Path $repoRoot ("generated\translation-review\review.{0}.{1}" -f $Language, $extension)
}

$neutralMap = Read-OrderedJsonMap -Path $NeutralJson
$targetMap = Read-OrderedJsonMap -Path $TargetJson

$comparisonMaps = [ordered]@{}
foreach ($code in $ComparisonLanguages) {
    $comparisonPath = Join-Path (Get-TranslationReviewLanguagesDirectory) ("lang_{0}.json" -f $code)
    if (Test-Path -Path $comparisonPath -PathType Leaf) {
        $comparisonMaps[$code] = Read-OrderedJsonMap -Path $comparisonPath
    }
}

$parityIssues = [System.Collections.Generic.List[object]]::new()
$englishEqualEntries = [System.Collections.Generic.List[object]]::new()
$wrongScriptEntries = [System.Collections.Generic.List[object]]::new()
$crossLanguageEntries = [System.Collections.Generic.List[object]]::new()

foreach ($entry in $neutralMap.GetEnumerator()) {
    $sourceText = [string]$entry.Key
    $englishValue = [string]$entry.Value

    if (-not $targetMap.Contains($sourceText)) {
        continue
    }

    $targetValue = [string]$targetMap[$sourceText]
    if ([string]::IsNullOrWhiteSpace($targetValue)) {
        continue
    }

    $crossLanguage = [ordered]@{}
    foreach ($code in $comparisonMaps.Keys) {
        $crossLanguage[$code] = if ($comparisonMaps[$code].Contains($sourceText)) {
            [string]$comparisonMaps[$code][$sourceText]
        }
        else {
            $null
        }
    }

    if ($Language -ne 'en' -and $targetValue -ceq $englishValue -and -not (Test-TranslationReviewTechnicalToken -Value $englishValue)) {
        $englishEqualEntries.Add([pscustomobject]@{
            SourceText = $sourceText
            English = $englishValue
            Translation = $targetValue
            CrossLanguage = $crossLanguage
        })
    }
    else {
        $issues = @(Get-TranslationReviewParityIssues -SourceValue $englishValue -TranslatedValue $targetValue)
        if ($issues.Count -gt 0) {
            $parityIssues.Add([pscustomobject]@{
                SourceText = $sourceText
                English = $englishValue
                Translation = $targetValue
                Issues = $issues
                CrossLanguage = $crossLanguage
            })
        }

        $scriptCheck = Test-TranslationReviewExpectedScript -LanguageCode $Language -Value $targetValue
        if ($null -ne $scriptCheck -and -not $scriptCheck.HasExpectedScript -and -not (Test-TranslationReviewTechnicalToken -Value $englishValue)) {
            $wrongScriptEntries.Add([pscustomobject]@{
                SourceText = $sourceText
                English = $englishValue
                Translation = $targetValue
                ExpectedScript = $scriptCheck.ExpectedScript
                CrossLanguage = $crossLanguage
            })
        }
    }

    $crossLanguageEntries.Add([pscustomobject]@{
        SourceText = $sourceText
        English = $englishValue
        Translation = $targetValue
        CrossLanguage = $crossLanguage
    })
}

$displayName = Get-TranslationReviewDisplayLanguage -LanguageCode $Language -LanguageReference $languageReference

$output = if ($OutputFormat -eq 'Json') {
    [pscustomobject]@{
        Language = $Language
        DisplayName = $displayName
        GeneratedAt = (Get-Date -Format 'o')
        TargetJson = $TargetJson
        TotalReviewed = $crossLanguageEntries.Count
        ParityIssueCount = $parityIssues.Count
        EnglishEqualCount = $englishEqualEntries.Count
        WrongScriptCount = $wrongScriptEntries.Count
        ParityIssues = $parityIssues.ToArray()
        EnglishEqual = $englishEqualEntries.ToArray()
        WrongScript = $wrongScriptEntries.ToArray()
        CrossLanguage = if (-not $FlaggedOnly) { $crossLanguageEntries.ToArray() } else { @() }
    } | ConvertTo-Json -Depth 10
}
else {
    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.AppendLine("# Translation Review: $displayName ($Language)")
    [void]$builder.AppendLine()
    [void]$builder.AppendLine("**File**: ``$TargetJson``")
    [void]$builder.AppendLine("**Generated**: $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
    [void]$builder.AppendLine("**Comparison languages**: $($comparisonMaps.Keys -join ', ')")
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('## Summary')
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('| Category | Count |')
    [void]$builder.AppendLine('|---|---|')
    [void]$builder.AppendLine("| Reviewed translated entries | $($crossLanguageEntries.Count) |")
    [void]$builder.AppendLine("| Parity issues | $($parityIssues.Count) |")
    [void]$builder.AppendLine("| English-equal entries | $($englishEqualEntries.Count) |")
    [void]$builder.AppendLine("| Wrong script entries | $($wrongScriptEntries.Count) |")
    [void]$builder.AppendLine()

    [void]$builder.AppendLine('## Parity Issues')
    [void]$builder.AppendLine()
    if ($parityIssues.Count -eq 0) {
        [void]$builder.AppendLine('_No parity issues found._')
    }
    else {
        foreach ($issue in $parityIssues) {
            [void]$builder.AppendLine("### ``$($issue.SourceText)``")
            [void]$builder.AppendLine()
            [void]$builder.AppendLine("- **English**: $($issue.English)")
            [void]$builder.AppendLine("- **$Language**: $($issue.Translation)")
            foreach ($problem in $issue.Issues) {
                [void]$builder.AppendLine("- **Issue**: $problem")
            }
            foreach ($code in $issue.CrossLanguage.Keys) {
                $value = $issue.CrossLanguage[$code]
                if (-not [string]::IsNullOrWhiteSpace($value) -and $value -ne $issue.English) {
                    [void]$builder.AppendLine("- **$code**: $value")
                }
            }
            [void]$builder.AppendLine()
        }
    }
    [void]$builder.AppendLine()

    [void]$builder.AppendLine('## English-Equal Entries')
    [void]$builder.AppendLine()
    if ($englishEqualEntries.Count -eq 0) {
        [void]$builder.AppendLine('_No suspicious English-equal entries found._')
    }
    else {
        foreach ($item in $englishEqualEntries) {
            [void]$builder.AppendLine("### ``$($item.SourceText)``")
            [void]$builder.AppendLine()
            [void]$builder.AppendLine("- **English**: $($item.English)")
            [void]$builder.AppendLine("- **$Language**: $($item.Translation)")
            foreach ($code in $item.CrossLanguage.Keys) {
                $value = $item.CrossLanguage[$code]
                if (-not [string]::IsNullOrWhiteSpace($value) -and $value -ne $item.English) {
                    [void]$builder.AppendLine("- **$code**: $value")
                }
            }
            [void]$builder.AppendLine()
        }
    }
    [void]$builder.AppendLine()

    [void]$builder.AppendLine('## Wrong Script')
    [void]$builder.AppendLine()
    if ($wrongScriptEntries.Count -eq 0) {
        $scriptInfo = Get-TranslationReviewScriptInfo -LanguageCode $Language
        if ($null -eq $scriptInfo) {
            [void]$builder.AppendLine('_Not applicable for Latin-script languages._')
        }
        else {
            [void]$builder.AppendLine('_No wrong-script entries found._')
        }
    }
    else {
        foreach ($item in $wrongScriptEntries) {
            [void]$builder.AppendLine("### ``$($item.SourceText)``")
            [void]$builder.AppendLine()
            [void]$builder.AppendLine("- **English**: $($item.English)")
            [void]$builder.AppendLine("- **$Language**: $($item.Translation)")
            [void]$builder.AppendLine("- **Expected script**: $($item.ExpectedScript)")
            foreach ($code in $item.CrossLanguage.Keys) {
                $value = $item.CrossLanguage[$code]
                if (-not [string]::IsNullOrWhiteSpace($value) -and $value -ne $item.English) {
                    [void]$builder.AppendLine("- **$code**: $value")
                }
            }
            [void]$builder.AppendLine()
        }
    }
    [void]$builder.AppendLine()

    if (-not $FlaggedOnly) {
        $comparisonCodes = @($comparisonMaps.Keys)
        [void]$builder.AppendLine('## Cross-Language Comparison')
        [void]$builder.AppendLine()
        [void]$builder.AppendLine('Use this table for spot-checking suspicious wording, wrong language, and unusual differences against related languages.')
        [void]$builder.AppendLine()
        $header = '| Source | English | ' + $Language + ' | ' + ($comparisonCodes -join ' | ') + ' |'
        $divider = '|---|---|' + ('---|' * ($comparisonCodes.Count + 1))
        [void]$builder.AppendLine($header)
        [void]$builder.AppendLine($divider)
        foreach ($row in $crossLanguageEntries) {
            $cells = New-Object System.Collections.Generic.List[string]
            $cells.Add((Format-TranslationReviewMarkdownCell -Value $row.SourceText))
            $cells.Add((Format-TranslationReviewMarkdownCell -Value $row.English))
            $cells.Add((Format-TranslationReviewMarkdownCell -Value $row.Translation))
            foreach ($code in $comparisonCodes) {
                $cells.Add((Format-TranslationReviewMarkdownCell -Value $row.CrossLanguage[$code]))
            }

            [void]$builder.AppendLine('| ' + ($cells -join ' | ') + ' |')
        }
    }

    $builder.ToString()
}

$outputDirectory = Split-Path -Path $OutputPath -Parent
if (-not (Test-Path -Path $outputDirectory -PathType Container)) {
    New-Item -Path $outputDirectory -ItemType Directory -Force | Out-Null
}

[System.IO.File]::WriteAllText($OutputPath, [string]$output, [System.Text.UTF8Encoding]::new($false))

Write-Host "Review written to: $OutputPath"
Write-Host "  Parity issues:     $($parityIssues.Count)"
Write-Host "  English-equal:     $($englishEqualEntries.Count)"
Write-Host "  Wrong script:      $($wrongScriptEntries.Count)"
Write-Host "  Entries reviewed:  $($crossLanguageEntries.Count)"

[pscustomobject]@{
    OutputPath = $OutputPath
    ParityIssueCount = $parityIssues.Count
    EnglishEqualCount = $englishEqualEntries.Count
    WrongScriptCount = $wrongScriptEntries.Count
    ReviewedEntryCount = $crossLanguageEntries.Count
}