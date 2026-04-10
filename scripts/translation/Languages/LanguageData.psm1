Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ProjectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$script:LanguageRemap = [ordered]@{
    'pt-BR' = 'pt_BR'
    'pt-PT' = 'pt_PT'
    'nn-NO' = 'nn'
    'uk' = 'ua'
    'zh-Hans' = 'zh_CN'
    'zh-Hant' = 'zh_TW'
}

$script:LanguageFlagsRemap = [ordered]@{
    'af' = 'za'
    'ar' = 'sa'
    'bs' = 'ba'
    'ca' = 'ad'
    'cs' = 'cz'
    'da' = 'dk'
    'el' = 'gr'
    'en' = 'gb'
    'eo' = 'https://upload.wikimedia.org/wikipedia/commons/f/f5/Flag_of_Esperanto.svg'
    'es-MX' = 'mx'
    'et' = 'ee'
    'fa' = 'ir'
    'fil' = 'ph'
    'gl' = 'es'
    'he' = 'il'
    'hi' = 'in'
    'ja' = 'jp'
    'ka' = 'ge'
    'ko' = 'kr'
    'ku' = 'iq'
    'mr' = 'in'
    'nb' = 'no'
    'nn' = 'no'
    'pt_BR' = 'br'
    'pt_PT' = 'pt'
    'si' = 'lk'
    'sr' = 'rs'
    'sv' = 'se'
    'sl' = 'si'
    'ta' = 'in'
    'vi' = 'vn'
    'zh_CN' = 'cn'
    'zh_TW' = 'tw'
    'zh' = 'cn'
    'bn' = 'bd'
    'tg' = 'ph'
    'sq' = 'al'
    'kn' = 'in'
    'sa' = 'in'
    'gu' = 'in'
    'ur' = 'pk'
    'be' = 'by'
}

function Get-ProjectRoot {
    return $script:ProjectRoot
}

function Get-ContributorsListPath {
    return Join-Path (Get-ProjectRoot) 'src\UniGetUI.Core.Data\Assets\Data\Contributors.list'
}

function Get-TranslatorsJsonPath {
    return Join-Path (Get-ProjectRoot) 'src\UniGetUI.Core.LanguageEngine\Assets\Data\Translators.json'
}

function Get-TranslatedPercentagesJsonPath {
    return Join-Path (Get-ProjectRoot) 'src\UniGetUI.Core.LanguageEngine\Assets\Data\TranslatedPercentages.json'
}

function Get-LanguagesReferenceJsonPath {
    return Join-Path (Get-ProjectRoot) 'src\UniGetUI.Core.LanguageEngine\Assets\Data\LanguagesReference.json'
}

function Get-LanguagesDirectoryPath {
    return Join-Path (Get-ProjectRoot) 'src\UniGetUI.Core.LanguageEngine\Assets\Languages'
}

function Read-JsonDictionary {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -Path $Path -PathType Leaf)) {
        return [ordered]@{}
    }

    $content = [System.IO.File]::ReadAllText($Path)
    if ([string]::IsNullOrWhiteSpace($content)) {
        return [ordered]@{}
    }

    $parsed = $content | ConvertFrom-Json -AsHashtable
    if ($null -eq $parsed) {
        return [ordered]@{}
    }

    if (-not ($parsed -is [System.Collections.IDictionary])) {
        throw "JSON root must be an object: $Path"
    }

    return $parsed
}

function Get-ContributorsList {
    $path = Get-ContributorsListPath
    if (-not (Test-Path -Path $path -PathType Leaf)) {
        return @()
    }

    return @(
        Get-Content -Path $path -Encoding UTF8 |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
}

function Get-LanguageCredits {
    return Read-JsonDictionary -Path (Get-TranslatorsJsonPath)
}

function Get-TranslatedPercentages {
    return Read-JsonDictionary -Path (Get-TranslatedPercentagesJsonPath)
}

function Get-LanguageReference {
    return Read-JsonDictionary -Path (Get-LanguagesReferenceJsonPath)
}

function Get-LanguageRemap {
    return [ordered]@{} + $script:LanguageRemap
}

function Get-LanguageFlagsRemap {
    return [ordered]@{} + $script:LanguageFlagsRemap
}

function Get-TranslatorsFromCredits {
    param(
        [AllowNull()]
        [string]$Credits
    )

    if ([string]::IsNullOrWhiteSpace($Credits)) {
        return @()
    }

    $contributors = Get-ContributorsList
    $translatorLookup = @{}
    foreach ($translator in ($Credits -split ',')) {
        $trimmed = $translator.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            continue
        }

        $wasPrefixed = $trimmed.StartsWith('@', [System.StringComparison]::Ordinal)
        if ($wasPrefixed) {
            $trimmed = $trimmed.Substring(1)
        }

        $link = ''
        if ($wasPrefixed -or ($contributors -contains $trimmed)) {
            $link = "https://github.com/$trimmed"
        }

        $translatorLookup[$trimmed] = [pscustomobject]@{
            name = $trimmed
            link = $link
        }
    }

    return @(
        $translatorLookup.Keys |
            Sort-Object { $_.ToLowerInvariant() } |
            ForEach-Object { $translatorLookup[$_] }
    )
}

function ConvertTo-TranslatorMarkdown {
    param(
        [AllowNull()]
        [object]$Translators
    )

    if ($null -eq $Translators) {
        return ''
    }

    $translatorItems = @()
    if ($Translators -is [string]) {
        $translatorItems = Get-TranslatorsFromCredits -Credits $Translators
    }
    else {
        $translatorItems = @($Translators)
    }

    $formatted = foreach ($translator in $translatorItems) {
        $name = [string]$translator.name
        $link = if ($null -eq $translator.link) { '' } else { [string]$translator.link }
        if ([string]::IsNullOrWhiteSpace($link)) {
            $name
        }
        else {
            "[$name]($link)"
        }
    }

    return ($formatted -join ', ')
}

function Get-LanguageFilePathMap {
    param(
        [switch]$AbsolutePaths
    )

    $languageReference = Get-LanguageReference
    $languagesDirectory = Get-LanguagesDirectoryPath
    $result = [ordered]@{}

    foreach ($entry in $languageReference.GetEnumerator()) {
        $code = [string]$entry.Key
        if ($code -eq 'default') {
            continue
        }

        $fileName = "lang_$code.json"
        $result[$code] = if ($AbsolutePaths.IsPresent) { Join-Path $languagesDirectory $fileName } else { $fileName }
    }

    return $result
}

function Get-MarkdownSupportLangs {
    return Get-MarkdownTranslationsTable
}

function Get-MarkdownTranslationsTable {
    param(
        [switch]$IncludeZeroPercent,

        [switch]$IncludeLanguageCode,

        [switch]$IncludeFileColumn,

        [bool]$IncludeTranslatedColumn = $true,

        [bool]$IncludeCreditsColumn = $true,

        [string]$MissingCreditsText = ''
    )

    $languageReference = Get-LanguageReference
    $translationPercentages = Get-TranslatedPercentages
    $languageCredits = Get-LanguageCredits
    $flagRemap = Get-LanguageFlagsRemap
    $languagesDirectory = Get-LanguagesDirectoryPath

    $lines = New-Object System.Collections.Generic.List[string]
    $headers = @('Language')
    $alignments = @(':--')

    if ($IncludeLanguageCode.IsPresent) {
        $headers += 'Code'
        $alignments += ':--'
    }

    if ($IncludeTranslatedColumn) {
        $headers += 'Translated'
        $alignments += ':--'
    }

    if ($IncludeFileColumn.IsPresent) {
        $headers += 'File'
        $alignments += ':--'
    }

    if ($IncludeCreditsColumn) {
        $headers += 'Contributor(s)'
        $alignments += '---'
    }

    $lines.Add('| ' + ($headers -join ' | ') + ' |')
    $lines.Add('| ' + ($alignments -join ' | ') + ' |')

    foreach ($entry in $languageReference.GetEnumerator()) {
        $languageCode = [string]$entry.Key
        if ($languageCode -eq 'default') {
            continue
        }

        $languageFilePath = Join-Path $languagesDirectory ("lang_$languageCode.json")
        if (-not (Test-Path -Path $languageFilePath -PathType Leaf)) {
            continue
        }

        $percentage = if ($translationPercentages.Contains($languageCode)) { [string]$translationPercentages[$languageCode] } else { '100%' }
        if ($percentage -eq '0%' -and -not $IncludeZeroPercent.IsPresent) {
            continue
        }

        $languageName = [string]$entry.Value
        $flag = if ($flagRemap.Contains($languageCode)) { [string]$flagRemap[$languageCode] } else { $languageCode }
        $credits = ''
        if ($IncludeCreditsColumn) {
            $credits = if ($languageCredits.Contains($languageCode)) {
                ConvertTo-TranslatorMarkdown -Translators $languageCredits[$languageCode]
            }
            else {
                ''
            }

            if ([string]::IsNullOrWhiteSpace($credits) -and -not [string]::IsNullOrWhiteSpace($MissingCreditsText)) {
                $credits = $MissingCreditsText
            }
        }

        $flagImageSource = if ([string]$flag -match '^[a-z]+://') { [string]$flag } else { "https://flagcdn.com/$flag.svg" }

        $row = New-Object System.Collections.Generic.List[string]
        $row.Add("<img src='$flagImageSource' width=20> &nbsp; $languageName")

        if ($IncludeLanguageCode.IsPresent) {
            $row.Add(('`{0}`' -f $languageCode))
        }

        if ($IncludeTranslatedColumn) {
            $row.Add($percentage)
        }

        if ($IncludeFileColumn.IsPresent) {
            $relativeLanguageFilePath = (Join-Path 'src/UniGetUI.Core.LanguageEngine/Assets/Languages' ("lang_{0}.json" -f $languageCode)) -replace '\\', '/'
            $row.Add("[lang_$languageCode.json]($relativeLanguageFilePath)")
        }

        if ($IncludeCreditsColumn) {
            $row.Add($credits)
        }

        $lines.Add('| ' + ($row -join ' | ') + ' |')
    }

    $lines.Add('')
    return ($lines -join [Environment]::NewLine)
}

function Get-TranslationDocumentationMarkdown {
    $coverageTable = Get-MarkdownTranslationsTable -IncludeZeroPercent -IncludeLanguageCode -IncludeFileColumn -IncludeCreditsColumn:$false
    $contributorsTable = Get-MarkdownTranslationsTable -IncludeZeroPercent -IncludeLanguageCode -IncludeTranslatedColumn:$false

    $sections = @(
        '# Translations',
        '',
        'UniGetUI includes translations for the languages listed below.',
        '',
        'This page lists the supported languages, each locale file, current completion status, and the credited contributors for each translation.',
        '',
        'If you would like to help improve a translation or report an issue, please open an issue or submit a pull request.',
        '',
        'Translation discussion and coordination also happens in [GitHub discussion #4510](https://github.com/Devolutions/UniGetUI/discussions/4510).',
        '',
        '## Language Coverage',
        '',
        $coverageTable.TrimEnd(),
        '',
        '## Contributors',
        '',
        'We are grateful to everyone who contributes translations to UniGetUI. Contributor credits are sourced from [Translators.json](src/UniGetUI.Core.LanguageEngine/Assets/Data/Translators.json). If you would like to be added to or removed from the list for a particular language, please open a pull request.',
        '',
        $contributorsTable.TrimEnd(),
        ''
    )

    return (($sections -join [Environment]::NewLine) + [Environment]::NewLine)
}

Export-ModuleMember -Function @(
    'Get-ProjectRoot',
    'Get-ContributorsListPath',
    'Get-TranslatorsJsonPath',
    'Get-TranslatedPercentagesJsonPath',
    'Get-LanguagesReferenceJsonPath',
    'Get-LanguagesDirectoryPath',
    'Get-ContributorsList',
    'Get-LanguageCredits',
    'Get-TranslatedPercentages',
    'Get-LanguageReference',
    'Get-LanguageRemap',
    'Get-LanguageFlagsRemap',
    'Get-TranslatorsFromCredits',
    'ConvertTo-TranslatorMarkdown',
    'Get-MarkdownSupportLangs',
    'Get-MarkdownTranslationsTable',
    'Get-TranslationDocumentationMarkdown',
    'Get-LanguageFilePathMap'
)