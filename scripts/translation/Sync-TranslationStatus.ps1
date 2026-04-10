[CmdletBinding(DefaultParameterSetName = 'Selected')]
param(
    [Parameter(ParameterSetName = 'Selected', Mandatory = $true)]
    [string[]]$LanguageCodes,

    [Parameter(ParameterSetName = 'All')]
    [switch]$AllLanguages,

    [switch]$UpdateTranslationDoc,

    [switch]$UpdateReadme,

    [string]$ReadmePath = (Join-Path $PSScriptRoot '..\..\README.md'),

    [string]$TranslationDocPath = (Join-Path $PSScriptRoot '..\..\TRANSLATION.md'),

    [string]$TranslatedPercentagesPath = (Join-Path $PSScriptRoot '..\..\src\UniGetUI.Core.LanguageEngine\Assets\Data\TranslatedPercentages.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'Languages\LanguageData.psm1') -Force

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

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Content
    )

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Write-TranslationDocumentation {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, (Get-TranslationDocumentationMarkdown), $encoding)
}

$shouldUpdateTranslationDoc = $UpdateTranslationDoc.IsPresent
if ($UpdateReadme.IsPresent) {
    Write-Warning 'The -UpdateReadme switch is deprecated. Updating TRANSLATION.md instead.'
    $shouldUpdateTranslationDoc = $true
}

$statusJson = & (Join-Path $PSScriptRoot 'Get-TranslationStatus.ps1') -OutputFormat Json
$statusRows = $statusJson | ConvertFrom-Json -AsHashtable
if ($null -eq $statusRows) {
    throw 'Could not load translation status data.'
}

$statusByCode = @{}
foreach ($row in $statusRows) {
    $statusByCode[[string]$row.Code] = $row
}

$codesToUpdate = @()
if ($AllLanguages.IsPresent) {
    $codesToUpdate = @($statusByCode.Keys | Sort-Object)
}
else {
    $codesToUpdate = @($LanguageCodes | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
}

if ($codesToUpdate.Count -eq 0) {
    throw 'No language codes were provided.'
}

$storedPercentages = Read-JsonObject -Path $TranslatedPercentagesPath
$updatedPercentages = [ordered]@{}
foreach ($entry in $storedPercentages.GetEnumerator()) {
    $updatedPercentages[[string]$entry.Key] = [string]$entry.Value
}

foreach ($code in $codesToUpdate) {
    if (-not $statusByCode.ContainsKey($code)) {
        throw "Language code '$code' was not found in the computed translation status."
    }

    $updatedPercentages[$code] = [string]$statusByCode[$code].Completion
}

$percentagesJson = ($updatedPercentages | ConvertTo-Json -Depth 5)
Write-Utf8File -Path $TranslatedPercentagesPath -Content $percentagesJson

if ($shouldUpdateTranslationDoc) {
    Write-TranslationDocumentation -Path $TranslationDocPath
}

Write-Output ('Updated translation status metadata for: ' + ($codesToUpdate -join ', '))