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
    'ar' = 'sa'
    'bs' = 'ba'
    'ca' = 'ad'
    'cs' = 'cz'
    'da' = 'dk'
    'el' = 'gr'
    'en' = 'gb'
    'et' = 'ee'
    'fa' = 'ir'
    'he' = 'il'
    'hi' = 'in'
    'ja' = 'jp'
    'ko' = 'kr'
    'nb' = 'no'
    'nn' = 'no'
    'pt_BR' = 'br'
    'pt_PT' = 'pt'
    'si' = 'lk'
    'sr' = 'rs'
    'sv' = 'se'
    'sl' = 'si'
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
    $languageReference = Get-LanguageReference
    $translationPercentages = Get-TranslatedPercentages
    $languageCredits = Get-LanguageCredits
    $flagRemap = Get-LanguageFlagsRemap
    $languagesDirectory = Get-LanguagesDirectoryPath

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('| Language | Translated | Translator(s) |')
    $lines.Add('| :-- | :-- | --- |')

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
        if ($percentage -eq '0%') {
            continue
        }

        $languageName = [string]$entry.Value
        $flag = if ($flagRemap.Contains($languageCode)) { [string]$flagRemap[$languageCode] } else { $languageCode }
        $credits = if ($languageCredits.Contains($languageCode)) {
            ConvertTo-TranslatorMarkdown -Translators $languageCredits[$languageCode]
        }
        else {
            ''
        }

        $lines.Add("| <img src='https://flagcdn.com/$flag.svg' width=20> &nbsp; $languageName | $percentage | $credits |")
    }

    $lines.Add('')
    return ($lines -join [Environment]::NewLine)
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
    'Get-LanguageFilePathMap'
)