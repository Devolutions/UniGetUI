[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$TranslationFilePath,
    [string]$BeforeRef = 'HEAD~1',
    [string]$AfterRef = 'HEAD',
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Languages\TranslationJsonTools.ps1')
. (Join-Path $PSScriptRoot 'Languages\TranslationSourceTools.ps1')

$resolvedRepositoryRoot = Resolve-TranslationSyncRepositoryRoot -RepositoryRoot $RepositoryRoot
$resolvedTranslationFilePath = if ([string]::IsNullOrWhiteSpace($TranslationFilePath)) {
    Join-Path $resolvedRepositoryRoot 'src\UniGetUI.Core.LanguageEngine\Assets\Languages\lang_en.json'
}
else {
    Get-FullPath -Path $TranslationFilePath
}

if (-not (Test-Path -Path $resolvedTranslationFilePath -PathType Leaf)) {
    throw "Translation file not found: $resolvedTranslationFilePath"
}

$relativePath = Get-RepoRelativePath -RepositoryRoot $resolvedRepositoryRoot -FilePath $resolvedTranslationFilePath

function Get-TranslationMap {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,

        [Parameter(Mandatory = $true)]
        [string]$Ref,

        [Parameter(Mandatory = $true)]
        [string]$RepoRelativePath,

        [Parameter(Mandatory = $true)]
        [string]$ResolvedFilePath
    )

    if ($Ref -in @('WORKTREE', 'CURRENT', 'FILE')) {
        return Read-OrderedJsonMap -Path $ResolvedFilePath
    }

    $gitObject = '{0}:{1}' -f $Ref, $RepoRelativePath
    $content = & git -C $RepoRoot show $gitObject 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to read '$RepoRelativePath' from git ref '$Ref'."
    }

    return Convert-JsonContentToOrderedMap -Content ([string]::Join([Environment]::NewLine, @($content))) -Path $gitObject -DetectDuplicates
}

$beforeMap = Get-TranslationMap -RepoRoot $resolvedRepositoryRoot -Ref $BeforeRef -RepoRelativePath $relativePath -ResolvedFilePath $resolvedTranslationFilePath
$afterMap = Get-TranslationMap -RepoRoot $resolvedRepositoryRoot -Ref $AfterRef -RepoRelativePath $relativePath -ResolvedFilePath $resolvedTranslationFilePath

$removedKeys = New-Object System.Collections.Generic.List[string]
foreach ($entry in $beforeMap.GetEnumerator()) {
    $key = [string]$entry.Key
    if (-not $afterMap.Contains($key)) {
        $removedKeys.Add($key)
    }
}

$addedKeys = New-Object System.Collections.Generic.List[string]
foreach ($entry in $afterMap.GetEnumerator()) {
    $key = [string]$entry.Key
    if (-not $beforeMap.Contains($key)) {
        $addedKeys.Add($key)
    }
}

$changedValues = New-Object System.Collections.Generic.List[object]
foreach ($entry in $beforeMap.GetEnumerator()) {
    $key = [string]$entry.Key
    if ($afterMap.Contains($key)) {
        $beforeValue = [string]$entry.Value
        $afterValue = [string]$afterMap[$key]
        if ($beforeValue -cne $afterValue) {
            $changedValues.Add([pscustomobject]@{
                    Key = $key
                    Before = $beforeValue
                    After = $afterValue
                })
        }
    }
}

$removedKeysArray = $removedKeys.ToArray()
$addedKeysArray = $addedKeys.ToArray()
$changedValuesArray = $changedValues.ToArray()

$report = [pscustomobject]@{
    generatedAt = (Get-Date).ToString('o')
    repositoryRoot = $resolvedRepositoryRoot
    translationFile = $relativePath
    beforeRef = $BeforeRef
    afterRef = $AfterRef
    removedKeyCount = $removedKeys.Count
    addedKeyCount = $addedKeys.Count
    changedValueCount = $changedValues.Count
    removedKeys = $removedKeysArray
    addedKeys = $addedKeysArray
    changedValues = $changedValuesArray
}

$resolvedOutputPath = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Join-Path $resolvedRepositoryRoot ('artifacts\translation\{0}-{1}-to-{2}.json' -f ([IO.Path]::GetFileNameWithoutExtension($relativePath)), (Get-SafeLabel -Value $BeforeRef), (Get-SafeLabel -Value $AfterRef))
}
else {
    Get-FullPath -Path $OutputPath
}

New-Utf8File -Path $resolvedOutputPath -Content ($report | ConvertTo-Json -Depth 6)

Write-Output "Translation key diff summary"
Write-Output "Repository root: $resolvedRepositoryRoot"
Write-Output "Translation file: $relativePath"
Write-Output "Before ref: $BeforeRef"
Write-Output "After ref: $AfterRef"
Write-Output "Removed keys: $($removedKeys.Count)"
Write-Output "Added keys: $($addedKeys.Count)"
Write-Output "Changed values: $($changedValues.Count)"
Write-Output "Report: $resolvedOutputPath"