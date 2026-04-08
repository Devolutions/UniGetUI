[CmdletBinding()]
param(
    [string]$LanguagesDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$simplePlaceholderPattern = '^(?<name>[A-Za-z0-9_]+)(?:,[^}:]+)?(?::[^}]+)?$'
$icuControlPattern = '^(?<name>[A-Za-z0-9_]+)\s*,\s*(?<kind>plural|select|selectordinal)\s*,(?<body>[\s\S]*)$'

function Get-RepositoryRoot {
    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}

function Resolve-LanguagesDirectory {
    if (-not [string]::IsNullOrWhiteSpace($LanguagesDirectory)) {
        return [System.IO.Path]::GetFullPath($LanguagesDirectory)
    }

    return Join-Path (Get-RepositoryRoot) 'src\UniGetUI.Core.LanguageEngine\Assets\Languages'
}

function Read-JsonObject {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $content = [System.IO.File]::ReadAllText($Path)
    if ([string]::IsNullOrWhiteSpace($content)) {
        return [ordered]@{}
    }

    Assert-NoDuplicateJsonKeys -Content $content -Path $Path

    $parsed = $content | ConvertFrom-Json -AsHashtable
    if ($null -eq $parsed) {
        return [ordered]@{}
    }

    if (-not ($parsed -is [System.Collections.IDictionary])) {
        throw "JSON root must be an object: $Path"
    }

    return $parsed
}

function Assert-NoDuplicateJsonKeys {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $pattern = [regex]'(?m)^\s*"((?:\\.|[^"])*)"\s*:'
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $duplicates = New-Object System.Collections.Generic.List[string]

    foreach ($match in $pattern.Matches($Content)) {
        $key = [regex]::Unescape($match.Groups[1].Value)
        if (-not $seen.Add($key) -and -not $duplicates.Contains($key)) {
            $duplicates.Add($key)
        }
    }

    if ($duplicates.Count -gt 0) {
        throw "JSON file contains duplicate key(s): $($duplicates -join ', '). Path: $Path"
    }
}

function Get-BraceBlock {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [int]$StartIndex
    )

    if ($Text[$StartIndex] -ne '{') {
        throw "Expected '{' at index $StartIndex."
    }

    $depth = 0
    for ($index = $StartIndex; $index -lt $Text.Length; $index++) {
        if ($Text[$index] -eq '{') {
            $depth += 1
            continue
        }

        if ($Text[$index] -ne '}') {
            continue
        }

        $depth -= 1
        if ($depth -eq 0) {
            return [pscustomobject]@{
                Content = $Text.Substring($StartIndex + 1, $index - $StartIndex - 1)
                EndIndex = $index
            }
        }
    }

    return $null
}

function Add-TokenCount {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Counts,

        [Parameter(Mandatory = $true)]
        [string]$Token,

        [int]$Amount = 1
    )

    if ($Counts.Contains($Token)) {
        $Counts[$Token] = [int]$Counts[$Token] + $Amount
    }
    else {
        $Counts[$Token] = $Amount
    }
}

function Get-SimplePlaceholderToken {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    $trimmed = $Content.Trim()
    if ($trimmed -notmatch $simplePlaceholderPattern) {
        return $null
    }

    return '{' + $matches.name + '}'
}

function Get-IcuBranchTokenCounts {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Body
    )

    $maxCounts = [ordered]@{}
    $index = 0
    while ($index -lt $Body.Length) {
        while ($index -lt $Body.Length -and [char]::IsWhiteSpace($Body[$index])) {
            $index += 1
        }

        if ($index -ge $Body.Length) {
            break
        }

        if ($Body[$index] -eq '{') {
            $messageBlock = Get-BraceBlock -Text $Body -StartIndex $index
            if ($null -eq $messageBlock) {
                $index += 1
                continue
            }

            $branchCounts = Get-TokenCounts -Text $messageBlock.Content
            foreach ($token in $branchCounts.Keys) {
                $branchCount = [int]$branchCounts[$token]
                if ($maxCounts.Contains($token)) {
                    $maxCounts[$token] = [Math]::Max([int]$maxCounts[$token], $branchCount)
                }
                else {
                    $maxCounts[$token] = $branchCount
                }
            }

            $index = $messageBlock.EndIndex + 1
            continue
        }

        while ($index -lt $Body.Length -and -not [char]::IsWhiteSpace($Body[$index]) -and $Body[$index] -ne '{') {
            $index += 1
        }
    }

    return $maxCounts
}

function Get-TokenCounts {
    param(
        [AllowEmptyString()]
        [string]$Text
    )

    $counts = [ordered]@{}
    if ($null -eq $Text) {
        return $counts
    }

    $index = 0
    while ($index -lt $Text.Length) {
        if ($Text[$index] -ne '{') {
            $index += 1
            continue
        }

        $block = Get-BraceBlock -Text $Text -StartIndex $index
        if ($null -eq $block) {
            $index += 1
            continue
        }

        $content = $block.Content
        if ($content.Trim() -match $icuControlPattern) {
            $branchCounts = Get-IcuBranchTokenCounts -Body $matches.body
            foreach ($token in $branchCounts.Keys) {
                Add-TokenCount -Counts $counts -Token $token -Amount ([int]$branchCounts[$token])
            }
        }
        else {
            $token = Get-SimplePlaceholderToken -Content $content
            if ($null -ne $token) {
                Add-TokenCount -Counts $counts -Token $token
            }
        }

        $index = $block.EndIndex + 1
    }

    return $counts
}

function Get-TokenMismatches {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$ExpectedCounts,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$ActualCounts
    )

    $mismatches = New-Object System.Collections.Generic.List[string]
    $allTokens = @($ExpectedCounts.Keys + $ActualCounts.Keys | Sort-Object -Unique)

    foreach ($token in $allTokens) {
        $expected = if ($ExpectedCounts.Contains($token)) { [int]$ExpectedCounts[$token] } else { 0 }
        $actual = if ($ActualCounts.Contains($token)) { [int]$ActualCounts[$token] } else { 0 }

        if ($expected -eq $actual) {
            continue
        }

        if ($actual -lt $expected) {
            $mismatches.Add("missing $token (expected $expected, found $actual)")
        }
        else {
            $mismatches.Add("unexpected $token (expected $expected, found $actual)")
        }
    }

    return @($mismatches)
}

$issues = 0
$validatedFiles = 0

try {
    $resolvedLanguagesDirectory = Resolve-LanguagesDirectory
    if (-not (Test-Path -Path $resolvedLanguagesDirectory -PathType Container)) {
        throw "Languages directory not found: $resolvedLanguagesDirectory"
    }

    $languageFiles = Get-ChildItem -Path $resolvedLanguagesDirectory -Filter 'lang_*.json' -File | Sort-Object Name
    foreach ($languageFile in $languageFiles) {
        try {
            $translations = Read-JsonObject -Path $languageFile.FullName
        }
        catch {
            $issues += 1
            Write-Output "[$($languageFile.Name)] Failed to parse JSON: $($_.Exception.Message)"
            continue
        }

        $validatedFiles += 1
        foreach ($entry in $translations.GetEnumerator()) {
            $sourceText = [string]$entry.Key
            $translatedText = if ($null -eq $entry.Value) { '' } else { [string]$entry.Value }

            if ([string]::IsNullOrWhiteSpace($translatedText)) {
                continue
            }

            $expectedCounts = Get-TokenCounts -Text $sourceText
            $actualCounts = Get-TokenCounts -Text $translatedText
            $mismatches = @(Get-TokenMismatches -ExpectedCounts $expectedCounts -ActualCounts $actualCounts)

            if ($mismatches.Count -eq 0) {
                continue
            }

            $issues += 1
            Write-Output "[$($languageFile.Name)] Placeholder mismatch for key: $sourceText"
            Write-Output "  Translation: $translatedText"
            foreach ($mismatch in $mismatches) {
                Write-Output "  $mismatch"
            }
        }
    }

    if ($issues -gt 0) {
        Write-Output "Translation verification failed with $issues issue(s)."
        exit 1
    }

    Write-Output "Validated $validatedFiles translation file(s); no placeholder issues found."
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
