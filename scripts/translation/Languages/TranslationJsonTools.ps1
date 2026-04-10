Set-StrictMode -Version Latest

$script:TranslationLegacyBoundaryKey = '__LEGACY_TRANSLATION_KEYS_BELOW__'
$script:TranslationLegacyBoundaryValue = 'Legacy translation keys below are kept for backward compatibility with older UniGetUI builds. Do not translate or remove yet.'

function Assert-Command {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if (-not (Get-Command -Name $Name -ErrorAction SilentlyContinue)) {
        throw "Required command not found on PATH: $Name"
    }
}

function Get-FullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return [System.IO.Path]::GetFullPath($Path)
}

function New-Utf8File {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Content
    )

    $directory = Split-Path -Path $Path -Parent
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -Path $directory -ItemType Directory -Force | Out-Null
    }

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function New-OrderedStringMap {
    return [System.Collections.Specialized.OrderedDictionary]::new([System.StringComparer]::Ordinal)
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

function Read-OrderedJsonMap {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -Path $Path -PathType Leaf)) {
        return (New-OrderedStringMap)
    }

    $content = [System.IO.File]::ReadAllText((Get-FullPath -Path $Path))
    return Convert-JsonContentToOrderedMap -Content $content -Path $Path -DetectDuplicates
}

function Read-OrderedJsonMapPermissive {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -Path $Path -PathType Leaf)) {
        return (New-OrderedStringMap)
    }

    $content = [System.IO.File]::ReadAllText((Get-FullPath -Path $Path))
    return Convert-JsonContentToOrderedMap -Content $content -Path $Path
}

function Convert-JsonContentToOrderedMap {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Content,

        [Parameter(Mandatory = $true)]
        [string]$Path,

        [switch]$DetectDuplicates
    )

    $result = New-OrderedStringMap
    if ([string]::IsNullOrWhiteSpace($Content)) {
        return $result
    }

    if ($DetectDuplicates) {
        Assert-NoDuplicateJsonKeys -Content $Content -Path $Path
    }

    $document = [System.Text.Json.JsonDocument]::Parse($Content)
    try {
        if ($document.RootElement.ValueKind -eq [System.Text.Json.JsonValueKind]::Null) {
            return $result
        }

        if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw "JSON file must contain a flat object: $Path"
        }

        foreach ($property in $document.RootElement.EnumerateObject()) {
            if ($property.Value.ValueKind -eq [System.Text.Json.JsonValueKind]::Object -or $property.Value.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
                throw "JSON file must contain a flat object with string values only: $Path"
            }

            if ($property.Value.ValueKind -eq [System.Text.Json.JsonValueKind]::String) {
                $result[$property.Name] = $property.Value.GetString()
                continue
            }

            if ($property.Value.ValueKind -eq [System.Text.Json.JsonValueKind]::Null) {
                $result[$property.Name] = ''
                continue
            }

            $result[$property.Name] = $property.Value.ToString()
        }
    }
    finally {
        $document.Dispose()
    }

    return $result
}

function ConvertTo-JsonStringLiteral {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    return ($Value | ConvertTo-Json -Compress)
}

function Write-OrderedJsonMap {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Map
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('{')

    $index = 0
    $count = $Map.Count
    foreach ($entry in $Map.GetEnumerator()) {
        $index += 1
        $line = '  {0}: {1}' -f (ConvertTo-JsonStringLiteral -Value ([string]$entry.Key)), (ConvertTo-JsonStringLiteral -Value ([string]$entry.Value))
        if ($index -lt $count) {
            $line += ','
        }

        $lines.Add($line)
    }

    $lines.Add('}')
    New-Utf8File -Path $Path -Content (($lines -join "`r`n") + "`r`n")
}

function Get-TranslationLegacyBoundaryKey {
    return $script:TranslationLegacyBoundaryKey
}

function Get-TranslationLegacyBoundaryValue {
    return $script:TranslationLegacyBoundaryValue
}

function Split-TranslationMapAtBoundary {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Map
    )

    $boundaryKey = Get-TranslationLegacyBoundaryKey
    $activeMap = New-OrderedStringMap
    $legacyMap = New-OrderedStringMap
    $hasBoundary = $false
    $boundaryReached = $false

    foreach ($entry in $Map.GetEnumerator()) {
        $key = [string]$entry.Key
        $value = [string]$entry.Value

        if ($key -ceq $boundaryKey) {
            $hasBoundary = $true
            $boundaryReached = $true
            continue
        }

        if ($boundaryReached) {
            $legacyMap[$key] = $value
        }
        else {
            $activeMap[$key] = $value
        }
    }

    return [pscustomobject]@{
        HasBoundary = $hasBoundary
        ActiveMap = $activeMap
        LegacyMap = $legacyMap
    }
}

function Join-TranslationMapWithBoundary {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$ActiveMap,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$LegacyMap,

        [switch]$IncludeBoundary
    )

    $result = New-OrderedStringMap
    foreach ($entry in $ActiveMap.GetEnumerator()) {
        $result[[string]$entry.Key] = [string]$entry.Value
    }

    if ($IncludeBoundary) {
        $result[(Get-TranslationLegacyBoundaryKey)] = Get-TranslationLegacyBoundaryValue
    }

    foreach ($entry in $LegacyMap.GetEnumerator()) {
        $result[[string]$entry.Key] = [string]$entry.Value
    }

    return $result
}

function Test-OrderedStringMapsEqual {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Left,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Right
    )

    if ($Left.Count -ne $Right.Count) {
        return $false
    }

    $leftEntries = @($Left.GetEnumerator())
    $rightEntries = @($Right.GetEnumerator())
    for ($index = 0; $index -lt $leftEntries.Count; $index += 1) {
        if ([string]$leftEntries[$index].Key -cne [string]$rightEntries[$index].Key) {
            return $false
        }

        if ([string]$leftEntries[$index].Value -cne [string]$rightEntries[$index].Value) {
            return $false
        }
    }

    return $true
}

function Invoke-CirupJson {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Assert-Command -Name 'cirup'

    $output = & cirup @Arguments --output-format json
    if ($LASTEXITCODE -ne 0) {
        throw "cirup command failed: cirup $($Arguments -join ' ')"
    }

    $jsonText = [string]::Join([Environment]::NewLine, @($output)).Trim()
    if ([string]::IsNullOrWhiteSpace($jsonText)) {
        return @()
    }

    $parsed = $jsonText | ConvertFrom-Json -AsHashtable
    return @($parsed)
}

function Get-RepositoryRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    $repoRoot = & git -C $WorkingDirectory rev-parse --show-toplevel
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRoot)) {
        throw "Unable to resolve git repository root from '$WorkingDirectory'."
    }

    return $repoRoot.Trim()
}

function Get-RepoRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )

    $repoRootFull = Get-FullPath -Path $RepositoryRoot
    $filePathFull = Get-FullPath -Path $FilePath
    $repoRootWithSeparator = $repoRootFull.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    if (-not $filePathFull.StartsWith($repoRootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "File '$filePathFull' is not located under repository root '$repoRootFull'."
    }

    return [System.IO.Path]::GetRelativePath($repoRootFull, $filePathFull).Replace('\', '/')
}

function Get-SafeLabel {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $safe = $Value -replace '[^A-Za-z0-9._-]+', '-'
    $safe = $safe.Trim('-')
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return 'base'
    }

    return $safe
}

function Get-PatchBaseName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$NeutralJsonPath
    )

    $stem = [System.IO.Path]::GetFileNameWithoutExtension($NeutralJsonPath)
    if ($stem -match '^(.*)_en$' -and -not [string]::IsNullOrWhiteSpace($matches[1])) {
        return $matches[1]
    }

    return $stem
}

function Get-LanguagesReferencePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$NeutralJsonPath
    )

    $languagesDirectory = Split-Path -Path (Get-FullPath -Path $NeutralJsonPath) -Parent
    $assetsDirectory = Split-Path -Path $languagesDirectory -Parent
    return Join-Path $assetsDirectory 'Data\LanguagesReference.json'
}

function Assert-LanguageCodeKnown {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LanguagesReferencePath,

        [Parameter(Mandatory = $true)]
        [string]$LanguageCode
    )

    $referenceMap = Read-OrderedJsonMap -Path $LanguagesReferencePath
    if (-not $referenceMap.Contains($LanguageCode)) {
        throw "Unknown language code '$LanguageCode'. Expected one of the language codes from $LanguagesReferencePath"
    }
}

function Test-NeedsTranslation {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Key,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$SourceMap,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$TargetMap
    )

    if (-not $TargetMap.Contains($Key)) {
        return $true
    }

    $targetValue = [string]$TargetMap[$Key]
    if ([string]::IsNullOrWhiteSpace($targetValue)) {
        return $true
    }

    return $targetValue -ceq [string]$SourceMap[$Key]
}

function Get-PlaceholderTokens {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    $tokens = foreach ($match in [regex]::Matches($Value, '\{[A-Za-z0-9_]+(?:,[^}:]+)?(?::[^}]+)?\}')) {
        $match.Value
    }

    return @($tokens | Sort-Object)
}

function Get-HtmlTokens {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    $tokens = foreach ($match in [regex]::Matches($Value, '</?[^>]+?>')) {
        $match.Value
    }

    return @($tokens | Sort-Object)
}

function Get-NewlineCount {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    return ([regex]::Matches($Value, "`r`n|`n")).Count
}

function Assert-TranslationStructure {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$SourceValue,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$TranslatedValue,

        [Parameter(Mandatory = $true)]
        [string]$Key
    )

    $sourcePlaceholderSignature = (Get-PlaceholderTokens -Value $SourceValue) -join "`n"
    $translatedPlaceholderSignature = (Get-PlaceholderTokens -Value $TranslatedValue) -join "`n"
    if ($sourcePlaceholderSignature -ne $translatedPlaceholderSignature) {
        throw "Placeholder mismatch for key '$Key'."
    }

    $sourceHtmlSignature = (Get-HtmlTokens -Value $SourceValue) -join "`n"
    $translatedHtmlSignature = (Get-HtmlTokens -Value $TranslatedValue) -join "`n"
    if ($sourceHtmlSignature -ne $translatedHtmlSignature) {
        throw "HTML fragment mismatch for key '$Key'."
    }

    if ((Get-NewlineCount -Value $SourceValue) -ne (Get-NewlineCount -Value $TranslatedValue)) {
        throw "Line-break mismatch for key '$Key'."
    }
}