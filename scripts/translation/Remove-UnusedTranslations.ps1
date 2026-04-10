[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$EnglishFilePath
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
$englishTranslations = Read-OrderedJsonMapPermissive -Path $resolvedEnglishFilePath
$extractedKeySet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($key in $snapshot.KeyOrder) {
    [void]$extractedKeySet.Add([string]$key)
}

$unusedKeys = New-Object System.Collections.Generic.List[string]
foreach ($entry in $englishTranslations.GetEnumerator()) {
    $key = [string]$entry.Key
    if (-not $extractedKeySet.Contains($key)) {
        $unusedKeys.Add($key)
        Write-Output "Unused key: $key"
    }
}

Write-Output "Scan completed. Checked $(@($snapshot.SourceFiles).Count) file(s); found $($unusedKeys.Count) unused key(s)."