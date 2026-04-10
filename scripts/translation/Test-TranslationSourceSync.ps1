Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Languages\TranslationJsonTools.ps1')
. (Join-Path $PSScriptRoot 'Languages\TranslationSourceTools.ps1')

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$syncScript = Join-Path $repoRoot 'scripts\translation\Sync-TranslationSources.ps1'

if (-not (Test-Path -Path $syncScript -PathType Leaf)) {
    throw "Sync script not found: $syncScript"
}

$tempRoot = Join-Path $env:TEMP ('translation-source-sync-{0}' -f [System.Guid]::NewGuid().ToString('N'))

try {
    $sourceDir = Join-Path $tempRoot 'src\Sample'
    $languageDir = Join-Path $tempRoot 'src\UniGetUI.Core.LanguageEngine\Assets\Languages'
    New-Item -Path $sourceDir -ItemType Directory -Force | Out-Null
    New-Item -Path $languageDir -ItemType Directory -Force | Out-Null

    New-Utf8File -Path (Join-Path $sourceDir 'Strings.cs') -Content @'
using UniGetUI.Core.Tools;

namespace Sample;

internal static class Strings
{
    public static void Use(int value)
    {
                _ = CoreTools.Translate("Literal from code");
                _ = CoreTools.AutoTranslated("Auto translated literal");
                _ = CoreTools.Translate($"Interpolated {value}");
    }
}
'@

    New-Utf8File -Path (Join-Path $sourceDir 'View.xaml') -Content @'
<Page xmlns:widgets="using:UniGetUI.Interface.Widgets">
    <widgets:TranslatedTextBlock Text="WinUI label" />
</Page>
'@

    New-Utf8File -Path (Join-Path $sourceDir 'View.axaml') -Content @'
<UserControl xmlns:t="clr-namespace:UniGetUI.Avalonia.MarkupExtensions"
                         xmlns:settings="clr-namespace:UniGetUI.Avalonia.Views.Controls.Settings">
  <StackPanel>
        <TextBlock Text="{t:Translate About UniGetUI}" />
        <TextBlock Text="{t:Translate Text='A, B, C'}" />
        <settings:TranslatedTextBlock Text="Avalonia header" />
  </StackPanel>
</UserControl>
'@

    $englishPath = Join-Path $languageDir 'lang_en.json'
    $initialMap = New-OrderedStringMap
    $initialMap['Literal from code'] = 'Literal from code'
    $initialMap['Obsolete key'] = 'Obsolete key'
    Write-OrderedJsonMap -Path $englishPath -Map $initialMap

    $snapshot = Get-TranslationSourceSnapshot -RepositoryRoot $tempRoot
    if (@($snapshot.Warnings).Count -ne 1) {
        throw "Expected exactly one warning for interpolated CoreTools.Translate, found $(@($snapshot.Warnings).Count)."
    }

    $threwOnCheck = $false
    try {
        & $syncScript -RepositoryRoot $tempRoot -EnglishFilePath $englishPath -CheckOnly | Out-Null
    }
    catch {
        if ($_.Exception.Message -notlike '*out of sync*') {
            throw
        }

        $threwOnCheck = $true
    }

    if (-not $threwOnCheck) {
        throw 'Expected check-only mode to detect synchronization differences.'
    }

    & $syncScript -RepositoryRoot $tempRoot -EnglishFilePath $englishPath | Out-Null

    $updatedMap = Read-OrderedJsonMap -Path $englishPath
    $expectedKeys = @(
        'Literal from code',
        'Auto translated literal',
        'WinUI label',
        'Avalonia header',
        'About UniGetUI',
        'A, B, C'
    )

    if ($updatedMap.Count -ne $expectedKeys.Count) {
        throw "Expected $($expectedKeys.Count) synchronized keys, found $($updatedMap.Count)."
    }

    foreach ($expectedKey in $expectedKeys) {
        if (-not $updatedMap.Contains($expectedKey)) {
            throw "Expected synchronized English file to contain key '$expectedKey'."
        }
    }

    if ($updatedMap.Contains('Obsolete key')) {
        throw 'Obsolete key should have been removed during synchronization.'
    }

    & $syncScript -RepositoryRoot $tempRoot -EnglishFilePath $englishPath -CheckOnly | Out-Null

    $boundaryKey = Get-TranslationLegacyBoundaryKey
    $boundaryMap = New-OrderedStringMap
    $boundaryMap['Literal from code'] = 'Literal from code'
    $boundaryMap['Obsolete key'] = 'Obsolete key'
    Write-OrderedJsonMap -Path $englishPath -Map $boundaryMap

    & $syncScript -RepositoryRoot $tempRoot -EnglishFilePath $englishPath -UseLegacyBoundary | Out-Null

    $boundaryUpdatedMap = Read-OrderedJsonMap -Path $englishPath
    $boundarySections = Split-TranslationMapAtBoundary -Map $boundaryUpdatedMap

    if (-not $boundarySections.HasBoundary) {
        throw 'Expected the synchronized English file to include the legacy boundary marker.'
    }

    if (-not $boundarySections.LegacyMap.Contains('Obsolete key')) {
        throw 'Obsolete key should have been preserved below the legacy boundary.'
    }

    if ($boundarySections.ActiveMap.Contains('Obsolete key')) {
        throw 'Obsolete key should not remain in the active key section.'
    }

    if (-not $boundaryUpdatedMap.Contains($boundaryKey)) {
        throw 'Expected the legacy boundary marker key to exist in the synchronized English file.'
    }

    & $syncScript -RepositoryRoot $tempRoot -EnglishFilePath $englishPath -UseLegacyBoundary -CheckOnly | Out-Null

    Write-Output 'Translation source sync smoke test completed successfully.'
}
finally {
    Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}