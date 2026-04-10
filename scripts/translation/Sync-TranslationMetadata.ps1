[CmdletBinding(DefaultParameterSetName = 'Selected', SupportsShouldProcess = $true)]
param(
    [Parameter(ParameterSetName = 'Selected', Mandatory = $true)]
    [string[]]$LanguageCodes,

    [Parameter(ParameterSetName = 'All')]
    [switch]$AllLanguages,

    [switch]$CheckOnly,

    [switch]$UpdateTranslationDoc,

    [switch]$UpdateReadme,

    [string]$ReadmePath = (Join-Path $PSScriptRoot '..\..\README.md'),

    [string]$TranslationDocPath = (Join-Path $PSScriptRoot '..\..\TRANSLATION.md'),

    [string]$TranslatorsPath = (Join-Path $PSScriptRoot '..\..\src\UniGetUI.Core.LanguageEngine\Assets\Data\Translators.json'),

    [string]$TranslatedPercentagesPath = (Join-Path $PSScriptRoot '..\..\src\UniGetUI.Core.LanguageEngine\Assets\Data\TranslatedPercentages.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'Languages\LanguageData.psm1') -Force
. (Join-Path $PSScriptRoot 'Languages\TranslationJsonTools.ps1')

$script:TranslatorCreditsKey = '0 0 0 Contributors, please add your names/usernames separated by comas (for credit purposes). DO NOT Translate this entry'

function Read-JsonObject {
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

function ConvertTo-NormalizedJson {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$Value
    )

    return ($Value | ConvertTo-Json -Depth 10)
}

function ConvertTo-TranslatorMetadataJson {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Map
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('{')

    $rootEntries = @($Map.GetEnumerator())
    for ($index = 0; $index -lt $rootEntries.Count; $index++) {
        $entry = $rootEntries[$index]
        $key = ConvertTo-JsonStringLiteral -Value ([string]$entry.Key)
        $translators = @($entry.Value)
        $isLastRootEntry = $index -eq ($rootEntries.Count - 1)

        if ($translators.Count -eq 0) {
            $line = '  {0}: []' -f $key
            if (-not $isLastRootEntry) {
                $line += ','
            }

            $lines.Add($line)
            continue
        }

        $lines.Add('  {0}: [' -f $key)
        for ($translatorIndex = 0; $translatorIndex -lt $translators.Count; $translatorIndex++) {
            $translator = $translators[$translatorIndex]
            $isLastTranslator = $translatorIndex -eq ($translators.Count - 1)
            $name = ConvertTo-JsonStringLiteral -Value ([string]$translator['name'])
            $link = ConvertTo-JsonStringLiteral -Value ([string]$translator['link'])

            $lines.Add('    {')
            $lines.Add('      "name": {0},' -f $name)
            $lines.Add('      "link": {0}' -f $link)

            $translatorClosing = '    }'
            if (-not $isLastTranslator) {
                $translatorClosing += ','
            }

            $lines.Add($translatorClosing)
        }

        $rootClosing = '  ]'
        if (-not $isLastRootEntry) {
            $rootClosing += ','
        }

        $lines.Add($rootClosing)
    }

    $lines.Add('}')
    return (($lines -join "`r`n") + "`r`n")
}

function Get-RequestedLanguageCodes {
    $languageReference = Get-LanguageReference
    $codesToUpdate = @()

    if ($AllLanguages.IsPresent) {
        $codesToUpdate = @(
            $languageReference.Keys |
                Where-Object { $_ -ne 'default' } |
                Sort-Object
        )
    }
    else {
        $codesToUpdate = @(
            $LanguageCodes |
                ForEach-Object { $_.Trim() } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                Sort-Object -Unique
        )
    }

    if ($codesToUpdate.Count -eq 0) {
        throw 'No language codes were provided.'
    }

    foreach ($code in $codesToUpdate) {
        if (-not $languageReference.Contains($code)) {
            throw "Unknown language code '$code'."
        }
    }

    return $codesToUpdate
}

function Get-ExpectedTranslationPercentages {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$CodesToUpdate,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $includeEnglish = $AllLanguages.IsPresent -or ($CodesToUpdate -contains 'en')
    $statusJson = & (Join-Path $PSScriptRoot 'Get-TranslationStatus.ps1') -OutputFormat Json -IncludeEnglish:$includeEnglish
    $statusRows = $statusJson | ConvertFrom-Json -AsHashtable
    if ($null -eq $statusRows) {
        throw 'Could not load translation status data.'
    }

    $statusByCode = @{}
    foreach ($row in $statusRows) {
        $statusByCode[[string]$row.Code] = $row
    }

    $storedPercentages = Read-JsonObject -Path $Path
    $updatedPercentages = [ordered]@{}
    foreach ($entry in $storedPercentages.GetEnumerator()) {
        $updatedPercentages[[string]$entry.Key] = [string]$entry.Value
    }

    foreach ($code in $CodesToUpdate) {
        if (-not $statusByCode.ContainsKey($code)) {
            throw "Language code '$code' was not found in the computed translation status."
        }

        $updatedPercentages[$code] = [string]$statusByCode[$code].Completion
    }

    return $updatedPercentages
}

function Get-ExpectedTranslatorMetadata {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$CodesToUpdate,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $languagesDirectory = Get-LanguagesDirectoryPath
    $englishLanguageFilePath = Join-Path $languagesDirectory 'lang_en.json'
    $storedTranslators = Read-JsonObject -Path $Path
    $updatedTranslators = [ordered]@{}
    foreach ($entry in $storedTranslators.GetEnumerator()) {
        $updatedTranslators[[string]$entry.Key] = @($entry.Value)
    }

    $englishLanguageMap = Read-OrderedJsonMap -Path $englishLanguageFilePath
    if (-not $englishLanguageMap.Contains($script:TranslatorCreditsKey)) {
        throw "Translator credits entry was not found in $englishLanguageFilePath"
    }

    $englishCreditsSignature = Get-TranslatorCreditsSignature -Credits ([string]$englishLanguageMap[$script:TranslatorCreditsKey])

    foreach ($code in $CodesToUpdate) {
        $languageFilePath = Join-Path $languagesDirectory ("lang_{0}.json" -f $code)
        if (-not (Test-Path -Path $languageFilePath -PathType Leaf)) {
            throw "Language file not found: $languageFilePath"
        }

        $languageMap = Read-OrderedJsonMap -Path $languageFilePath
        if (-not $languageMap.Contains($script:TranslatorCreditsKey)) {
            throw "Translator credits entry was not found in $languageFilePath"
        }

        $credits = [string]$languageMap[$script:TranslatorCreditsKey]
        $creditsSignature = Get-TranslatorCreditsSignature -Credits $credits
        $shouldPreserveExisting = $code -ne 'en' -and $creditsSignature -eq $englishCreditsSignature

        if ($shouldPreserveExisting) {
            if (-not $updatedTranslators.Contains($code)) {
                $updatedTranslators[$code] = @()
            }

            Write-Warning "Preserving existing translator metadata for '$code' because its locale credits currently match the English fallback credits."
            continue
        }

        $updatedTranslators[$code] = @(ConvertTo-TranslatorMetadataEntries -Credits $credits)
    }

    return $updatedTranslators
}

function Get-TranslatorCreditsSignature {
    param(
        [AllowNull()]
        [string]$Credits
    )

    $entries = @(Get-TranslatorsFromCredits -Credits $Credits)
    if ($entries.Count -eq 0) {
        return ''
    }

    return @(
        $entries |
            ForEach-Object { ([string]$_.name).Trim().ToLowerInvariant() }
    ) -join "`n"
}

function ConvertTo-TranslatorMetadataEntries {
    param(
        [AllowNull()]
        [string]$Credits
    )

    return @(
        Get-TranslatorsFromCredits -Credits $Credits |
            ForEach-Object {
                [ordered]@{
                    name = [string]$_.name
                    link = [string]$_.link
                }
            }
    )
}

function Write-TranslationDocumentation {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    New-Utf8File -Path $Path -Content (Get-TranslationDocumentationMarkdown)
}

$shouldUpdateTranslationDoc = $UpdateTranslationDoc.IsPresent
if ($UpdateReadme.IsPresent) {
    Write-Warning 'The -UpdateReadme switch is deprecated. Updating TRANSLATION.md instead.'
    $shouldUpdateTranslationDoc = $true
}

$codesToUpdate = Get-RequestedLanguageCodes
$expectedPercentages = Get-ExpectedTranslationPercentages -CodesToUpdate $codesToUpdate -Path $TranslatedPercentagesPath
$expectedTranslators = Get-ExpectedTranslatorMetadata -CodesToUpdate $codesToUpdate -Path $TranslatorsPath

$actualPercentages = Read-JsonObject -Path $TranslatedPercentagesPath
$actualTranslators = Read-JsonObject -Path $TranslatorsPath

$driftedFiles = New-Object System.Collections.Generic.List[string]
if ((ConvertTo-NormalizedJson -Value $actualPercentages) -ne (ConvertTo-NormalizedJson -Value $expectedPercentages)) {
    $driftedFiles.Add((Get-TranslatedPercentagesJsonPath))
}

if ((ConvertTo-NormalizedJson -Value $actualTranslators) -ne (ConvertTo-NormalizedJson -Value $expectedTranslators)) {
    $driftedFiles.Add((Get-TranslatorsJsonPath))
}

Write-Output ('Translation metadata scope: ' + ($codesToUpdate -join ', '))
Write-Output ('Metadata drift detected: ' + $driftedFiles.Count)

if ($driftedFiles.Count -gt 0) {
    foreach ($path in $driftedFiles) {
        Write-Output ('  * ' + $path)
    }
}

if ($CheckOnly.IsPresent) {
    if ($driftedFiles.Count -gt 0) {
        throw 'Translation metadata is out of date. Run scripts/translation/Sync-TranslationMetadata.ps1 to refresh it.'
    }

    Write-Output 'Translation metadata is up to date.'
    return
}

if ($driftedFiles.Contains((Get-TranslatedPercentagesJsonPath)) -and $PSCmdlet.ShouldProcess($TranslatedPercentagesPath, 'Update translated percentages metadata')) {
    New-Utf8File -Path $TranslatedPercentagesPath -Content ((ConvertTo-NormalizedJson -Value $expectedPercentages) + "`r`n")
}

if ($driftedFiles.Contains((Get-TranslatorsJsonPath)) -and $PSCmdlet.ShouldProcess($TranslatorsPath, 'Update translator metadata')) {
    New-Utf8File -Path $TranslatorsPath -Content (ConvertTo-TranslatorMetadataJson -Map $expectedTranslators)
}

if ($shouldUpdateTranslationDoc -and $PSCmdlet.ShouldProcess($TranslationDocPath, 'Update translation documentation')) {
    Write-TranslationDocumentation -Path $TranslationDocPath
}

Write-Output 'Translation metadata sync completed.'