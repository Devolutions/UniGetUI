Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'TranslationJsonTools.ps1')

$script:SupportedSourceExtensions = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
[void]$script:SupportedSourceExtensions.Add('.cs')
[void]$script:SupportedSourceExtensions.Add('.xaml')
[void]$script:SupportedSourceExtensions.Add('.axaml')

$script:ExcludedDirectoryNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in @('.git', '.vs', 'bin', 'obj', 'generated', 'node_modules', 'packages')) {
    [void]$script:ExcludedDirectoryNames.Add($name)
}

function Resolve-TranslationSyncRepositoryRoot {
    param(
        [string]$RepositoryRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        return Get-FullPath -Path $RepositoryRoot
    }

    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
}

function Resolve-EnglishLanguageFilePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedRepositoryRoot,

        [string]$EnglishFilePath
    )

    if (-not [string]::IsNullOrWhiteSpace($EnglishFilePath)) {
        return Get-FullPath -Path $EnglishFilePath
    }

    return Join-Path $ResolvedRepositoryRoot 'src\UniGetUI.Core.LanguageEngine\Assets\Languages\lang_en.json'
}

function Test-TranslationSourceFileIncluded {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$File,

        [Parameter(Mandatory = $true)]
        [string]$ResolvedRepositoryRoot
    )

    if (-not $script:SupportedSourceExtensions.Contains($File.Extension)) {
        return $false
    }

    $relativePath = [System.IO.Path]::GetRelativePath($ResolvedRepositoryRoot, $File.FullName).Replace('\', '/')
    foreach ($segment in $relativePath.Split('/')) {
        if ($script:ExcludedDirectoryNames.Contains($segment)) {
            return $false
        }
    }

    if ($relativePath -like 'src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_*.json') {
        return $false
    }

    return $relativePath.StartsWith('src/', [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-TranslationSourceFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedRepositoryRoot
    )

    return @(
        Get-ChildItem -Path $ResolvedRepositoryRoot -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { Test-TranslationSourceFileIncluded -File $_ -ResolvedRepositoryRoot $ResolvedRepositoryRoot } |
            Sort-Object FullName
    )
}

function Get-LineNumberFromIndex {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [int]$Index
    )

    if ($Index -le 0) {
        return 1
    }

    return ([regex]::Matches($Text.Substring(0, $Index), "`r`n|`n")).Count + 1
}

function Convert-CSharpStringLiteralValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Literal
    )

    if ($Literal.StartsWith('@"', [System.StringComparison]::Ordinal)) {
        return $Literal.Substring(2, $Literal.Length - 3).Replace('""', '"')
    }

    return [regex]::Unescape($Literal.Substring(1, $Literal.Length - 2))
}

function Add-TranslationSourceKey {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Key,

        [Parameter(Mandatory = $true)]
        [string]$SourcePath,

        [Parameter(Mandatory = $true)]
        [int]$LineNumber,

        [Parameter(Mandatory = $true)]
        [System.Collections.IList]$KeyOrder,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$SourcesByKey
    )

    if ([string]::IsNullOrWhiteSpace($Key)) {
        return
    }

    if (-not $SourcesByKey.Contains($Key)) {
        $SourcesByKey[$Key] = New-Object System.Collections.Generic.List[string]
        $KeyOrder.Add($Key)
    }

    $location = '{0}:{1}' -f $SourcePath.Replace('\', '/'), $LineNumber
    $locations = [System.Collections.Generic.List[string]]$SourcesByKey[$Key]
    if (-not $locations.Contains($location)) {
        $locations.Add($location)
    }
}

function Add-TranslationSourceWarning {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Type,

        [Parameter(Mandatory = $true)]
        [string]$SourcePath,

        [Parameter(Mandatory = $true)]
        [int]$LineNumber,

        [Parameter(Mandatory = $true)]
        [string]$Message,

        [Parameter(Mandatory = $true)]
        [System.Collections.IList]$Warnings
    )

    $Warnings.Add([pscustomobject]@{
            Type = $Type
            Path = $SourcePath.Replace('\', '/')
            Line = $LineNumber
            Message = $Message
        })
}

function Get-CSharpTranslationMatches {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string]$Content,

        [Parameter(Mandatory = $true)]
        [System.Collections.IList]$KeyOrder,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$SourcesByKey,

        [Parameter(Mandatory = $true)]
        [System.Collections.IList]$Warnings
    )

    $literalPattern = [regex]'CoreTools\.(?:Translate|AutoTranslated)\(\s*(?<literal>@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*")'
    foreach ($match in $literalPattern.Matches($Content)) {
        $literal = [string]$match.Groups['literal'].Value
        $lineNumber = Get-LineNumberFromIndex -Text $Content -Index $match.Index
        Add-TranslationSourceKey -Key (Convert-CSharpStringLiteralValue -Literal $literal) -SourcePath $FilePath -LineNumber $lineNumber -KeyOrder $KeyOrder -SourcesByKey $SourcesByKey
    }

    $interpolatedPattern = [regex]'CoreTools\.Translate\(\s*\$@?"'
    foreach ($match in $interpolatedPattern.Matches($Content)) {
        $lineNumber = Get-LineNumberFromIndex -Text $Content -Index $match.Index
        Add-TranslationSourceWarning -Type 'InterpolatedTranslateCall' -SourcePath $FilePath -LineNumber $lineNumber -Message 'Interpolated CoreTools.Translate call is not synchronized automatically.' -Warnings $Warnings
    }
}

function Get-TranslatedTextBlockMatches {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string]$Content,

        [Parameter(Mandatory = $true)]
        [System.Collections.IList]$KeyOrder,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$SourcesByKey
    )

    $translatedTextBlockPattern = [regex]'<(?<tag>[A-Za-z0-9_:.\-]+TranslatedTextBlock)\b[^>]*\bText="(?<text>[^"]*)"'
    foreach ($match in $translatedTextBlockPattern.Matches($Content)) {
        $lineNumber = Get-LineNumberFromIndex -Text $Content -Index $match.Index
        $text = [System.Net.WebUtility]::HtmlDecode([string]$match.Groups['text'].Value)
        Add-TranslationSourceKey -Key $text -SourcePath $FilePath -LineNumber $lineNumber -KeyOrder $KeyOrder -SourcesByKey $SourcesByKey
    }
}

function Get-AvaloniaTranslateMarkupMatches {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string]$Content,

        [Parameter(Mandatory = $true)]
        [System.Collections.IList]$KeyOrder,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$SourcesByKey,

        [Parameter(Mandatory = $true)]
        [System.Collections.IList]$Warnings
    )

    $markupPattern = [regex]'\{t:Translate(?<body>[^}]*)\}'
    foreach ($match in $markupPattern.Matches($Content)) {
        $body = [string]$match.Groups['body'].Value
        $trimmedBody = $body.Trim()
        $lineNumber = Get-LineNumberFromIndex -Text $Content -Index $match.Index

        if ([string]::IsNullOrWhiteSpace($trimmedBody)) {
            Add-TranslationSourceWarning -Type 'EmptyMarkupTranslate' -SourcePath $FilePath -LineNumber $lineNumber -Message 'Empty {t:Translate} markup extension was ignored.' -Warnings $Warnings
            continue
        }

        $namedMatch = [regex]::Match($trimmedBody, '^Text\s*=\s*(?:''(?<single>[^'']*)''|"(?<double>[^"]*)")$')
        if ($namedMatch.Success) {
            $value = if ($namedMatch.Groups['single'].Success) { $namedMatch.Groups['single'].Value } else { $namedMatch.Groups['double'].Value }
            Add-TranslationSourceKey -Key ([System.Net.WebUtility]::HtmlDecode($value)) -SourcePath $FilePath -LineNumber $lineNumber -KeyOrder $KeyOrder -SourcesByKey $SourcesByKey
            continue
        }

        Add-TranslationSourceKey -Key ([System.Net.WebUtility]::HtmlDecode($trimmedBody)) -SourcePath $FilePath -LineNumber $lineNumber -KeyOrder $KeyOrder -SourcesByKey $SourcesByKey
    }
}

function Get-TranslationSourceSnapshot {
    param(
        [string]$RepositoryRoot
    )

    $resolvedRepositoryRoot = Resolve-TranslationSyncRepositoryRoot -RepositoryRoot $RepositoryRoot
    $sourceFiles = Get-TranslationSourceFiles -ResolvedRepositoryRoot $resolvedRepositoryRoot
    $keyOrder = New-Object System.Collections.Generic.List[string]
    $sourcesByKey = [ordered]@{}
    $warnings = New-Object System.Collections.Generic.List[object]

    foreach ($file in $sourceFiles) {
        $relativePath = [System.IO.Path]::GetRelativePath($resolvedRepositoryRoot, $file.FullName).Replace('\', '/')
        $content = [System.IO.File]::ReadAllText($file.FullName)

        switch ($file.Extension.ToLowerInvariant()) {
            '.cs' {
                Get-CSharpTranslationMatches -FilePath $relativePath -Content $content -KeyOrder $keyOrder -SourcesByKey $sourcesByKey -Warnings $warnings
            }
            '.xaml' {
                Get-TranslatedTextBlockMatches -FilePath $relativePath -Content $content -KeyOrder $keyOrder -SourcesByKey $sourcesByKey
            }
            '.axaml' {
                Get-TranslatedTextBlockMatches -FilePath $relativePath -Content $content -KeyOrder $keyOrder -SourcesByKey $sourcesByKey
                Get-AvaloniaTranslateMarkupMatches -FilePath $relativePath -Content $content -KeyOrder $keyOrder -SourcesByKey $sourcesByKey -Warnings $warnings
            }
        }
    }

    $orderedKeyMap = New-OrderedStringMap
    foreach ($key in $keyOrder) {
        $orderedKeyMap[$key] = $key
    }

    $sourceFilePaths = New-Object System.Collections.Generic.List[string]
    foreach ($sourceFile in $sourceFiles) {
        $sourceFilePaths.Add($sourceFile.FullName)
    }

    $warningItems = New-Object System.Collections.Generic.List[object]
    foreach ($warning in $warnings) {
        $warningItems.Add($warning)
    }

    $sourceFileArray = $sourceFilePaths.ToArray()
    $keyOrderArray = $keyOrder.ToArray()
    $warningArray = $warningItems.ToArray()

    $snapshot = New-Object -TypeName psobject
    $snapshot | Add-Member -NotePropertyName RepositoryRoot -NotePropertyValue $resolvedRepositoryRoot
    $snapshot | Add-Member -NotePropertyName SourceFiles -NotePropertyValue $sourceFileArray
    $snapshot | Add-Member -NotePropertyName KeyOrder -NotePropertyValue $keyOrderArray
    $snapshot | Add-Member -NotePropertyName Keys -NotePropertyValue $orderedKeyMap
    $snapshot | Add-Member -NotePropertyName SourcesByKey -NotePropertyValue $sourcesByKey
    $snapshot | Add-Member -NotePropertyName Warnings -NotePropertyValue $warningArray

    return $snapshot
}