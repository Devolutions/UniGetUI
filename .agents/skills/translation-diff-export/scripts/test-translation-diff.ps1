param(
    [string]$Language = 'fr',

    [string]$OutputRoot = 'generated/translation-diff-export-demo'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'TranslationDiff.JsonTools.ps1')

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$neutralPath = Join-Path $repoRoot 'src\Languages\lang_en.json'
$targetPath = Join-Path $repoRoot ('src\Languages\lang_{0}.json' -f $Language)
$exportScript = Join-Path $PSScriptRoot 'export-translation-diff.ps1'
$importScript = Join-Path $repoRoot '.agents\skills\translation-diff-import\scripts\import-translation-diff.ps1'
$validateScript = Join-Path $repoRoot '.agents\skills\translation-diff-import\scripts\validate-language-file.ps1'

if (-not (Test-Path -Path $neutralPath -PathType Leaf)) {
    throw "Neutral language file not found: $neutralPath"
}

& $exportScript -NeutralJson $neutralPath -TargetJson $targetPath -Language $Language -OutputDir $OutputRoot

$baseName = Get-PatchBaseName -NeutralJsonPath $neutralPath
$sourcePatchPath = Join-Path (Get-FullPath -Path $OutputRoot) ('{0}.diff.{1}.source.json' -f $baseName, $Language)
$translatedPatchPath = Join-Path (Get-FullPath -Path $OutputRoot) ('{0}.diff.{1}.translated.json' -f $baseName, $Language)
$mergedTargetPath = Join-Path (Get-FullPath -Path $OutputRoot) ('lang_{0}.merged.smoke.json' -f $Language)

$sourcePatchMap = Read-OrderedJsonMap -Path $sourcePatchPath
if ($sourcePatchMap.Count -eq 0) {
    throw 'Smoke test patch is empty. Pick a target language with untranslated entries to exercise the workflow.'
}

$translatedPatchMap = New-OrderedStringMap
$firstEntry = $sourcePatchMap.GetEnumerator() | Select-Object -First 1
$translatedPatchMap[[string]$firstEntry.Key] = '[smoke {0}] {1}' -f $Language, [string]$firstEntry.Value
Write-OrderedJsonMap -Path $translatedPatchPath -Map $translatedPatchMap

& $importScript -TranslatedPatch $translatedPatchPath -SourcePatch $sourcePatchPath -TargetJson $targetPath -NeutralJson $neutralPath -OutputJson $mergedTargetPath

& $validateScript -NeutralJson $neutralPath -TargetJson $mergedTargetPath -PatchJson $sourcePatchPath

Write-Output "Smoke test completed successfully. Merged output: $mergedTargetPath"