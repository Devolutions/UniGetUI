function Get-NuGetPackagesRoot
{
    if ($env:NUGET_PACKAGES)
    {
        return $env:NUGET_PACKAGES
    }

    $listed = & dotnet nuget locals global-packages --list 2>$null
    if ($LASTEXITCODE -eq 0 -and "$listed" -match 'global-packages:\s*(?<path>.+)$')
    {
        return $Matches.path.Trim()
    }

    return Join-Path $HOME '.nuget' 'packages'
}

function Resolve-NuGetMetadata
{
    param(
        [Parameter(Mandatory)] [pscustomobject] $Component,
        [Parameter(Mandatory)] [string] $PackagesRoot,
        [Parameter(Mandatory)] [hashtable] $UrlMappings
    )

    $packageDirectory = Join-Path $PackagesRoot $Component.PackagePath
    $metadata = [ordered]@{ License = ''; Source = ''; Website = ''; Copyright = ''; LicenseText = ''; LicenseTextOrigin = 'none'; RepositoryCommit = ''; PackageNotice = $null; Found = $false }

    $nuspec = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $nuspec)
    {
        return [pscustomobject]$metadata
    }

    $metadata.Found = $true
    [xml]$document = Get-Content -LiteralPath $nuspec.FullName -Raw
    $node = $document.SelectSingleNode("//*[local-name()='metadata']")
    $read = { param($name) $node.SelectSingleNode("*[local-name()='$name']") }

    $licenseNode = & $read 'license'
    $licenseUrl = [string](& $read 'licenseUrl').InnerText
    $repositoryNode = & $read 'repository'
    $metadata.Source = if ($repositoryNode) { [string]$repositoryNode.GetAttribute('url') } else { '' }
    $metadata.RepositoryCommit = if ($repositoryNode) { [string]$repositoryNode.GetAttribute('commit') } else { '' }
    $metadata.Website = [string](& $read 'projectUrl').InnerText
    $metadata.Copyright = ([string](& $read 'copyright').InnerText).Trim()

    if ($licenseNode -and $licenseNode.GetAttribute('type') -eq 'expression')
    {
        $metadata.License = $licenseNode.InnerText.Trim()
    }
    elseif ($licenseNode -and $licenseNode.GetAttribute('type') -eq 'file')
    {
        $metadata.License = 'Custom'
        $licenseFile = Join-Path $packageDirectory $licenseNode.InnerText.Trim()
        if (Test-Path -LiteralPath $licenseFile)
        {
            $metadata.LicenseText = Get-Content -LiteralPath $licenseFile -Raw
            $metadata.LicenseTextOrigin = 'package'
        }
    }
    elseif ($licenseUrl)
    {
        $metadata.License = Resolve-LicenseFromUrl -Url $licenseUrl -UrlMappings $UrlMappings
    }

    if (-not $metadata.LicenseText)
    {
        # Older Microsoft packages ship their licence as dotnet_library_license.txt (the SqlClient SNI runtimes)
        $licenseFile = Get-ChildItem -LiteralPath $packageDirectory -File |
            Where-Object { $_.Name -match '^((licen[cs]e|copying)(\.(txt|md))?|dotnet_library_license\.txt)$' } |
            Select-Object -First 1
        if ($licenseFile)
        {
            $metadata.LicenseText = Get-Content -LiteralPath $licenseFile.FullName -Raw
            $metadata.LicenseTextOrigin = 'package'
        }
    }

    # Credits for code the package carries (Avalonia.Controls.Markdown's NOTICE.md), which its repository may not hold
    $noticeFile = Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object Name -Match $script:NoticeFilePattern | Select-Object -First 1
    if ($noticeFile)
    {
        $noticeText = "$(Get-Content -LiteralPath $noticeFile.FullName -Raw)"
        if ($noticeText.Trim() -and $noticeFile.Name -match '\.html?$') { $noticeText = "$(Convert-HtmlToText -Html $noticeText)" }
        if ($noticeText.Trim())
        {
            $metadata.PackageNotice = [pscustomobject]@{ Repository = ''; File = $noticeFile.Name; Url = ''; Text = $noticeText.Trim() }
        }
    }

    if (-not $metadata.Copyright)
    {
        $metadata.Copyright = Get-AssemblyCopyright -PackageDirectory $packageDirectory
    }

    if (-not $metadata.Source)
    {
        $metadata.Source = $metadata.Website
    }

    return [pscustomobject]$metadata
}

function Get-AssemblyCopyright
{
    param([Parameter(Mandatory)] [string] $PackageDirectory)

    $binaries = foreach ($folder in 'lib', 'runtimes')
    {
        $path = Join-Path $PackageDirectory $folder
        if (Test-Path -LiteralPath $path)
        {
            # A satellite assembly translates the line ("Alle Rechte vorbehalten"); legal wants the package's wording
            Get-ChildItem -LiteralPath $path -Recurse -File -Include '*.dll', '*.exe' | Where-Object Name -NotLike '*.resources.dll'
        }
    }

    foreach ($binary in $binaries)
    {
        $copyright = ([string](Get-FileVersionProperties -Path $binary.FullName).LegalCopyright).Trim()
        if ($copyright)
        {
            return $copyright
        }
    }

    return ''
}

function Format-Copyright
{
    param([string] $Copyright)

    $Copyright = ([string]$Copyright).Trim()
    if (-not $Copyright -or $Copyright -match '^(?i)(copyright\b|©|\(c\))')
    {
        return $Copyright
    }

    return "Copyright (c) $Copyright"
}

function Test-CopyrightLine
{
    param([string] $Line)

    # "Copyright Holder" is a term the SIL Open Font License defines, not a holder
    if ($Line -cmatch '^[ \t#*/]*Copyright Holder')
    {
        return $false
    }

    if ($Line -cnotmatch '^[ \t#*/]*(Copyright\b|COPYRIGHT[ \t]*(\([cC]\)|©|\d)|©|\([cC]\)[ \t]*\d)')
    {
        return $false
    }

    $holder = $Line -replace '(?i)copyright|\(c\)|©', '' -replace '<[^>]*>|\[[^\]]*\]|\{[^}]*\}', '' -replace '[\p{P}\p{S}\s]', ''
    return [bool]$holder
}

$script:GenericHolderWords = @(
    'copyright', 'rights', 'reserved', 'contributors', 'contributor', 'authors', 'author', 'additions', 'corporation',
    'company', 'limited', 'foundation', 'software', 'development', 'team', 'project', 'group', 'affiliates', 'others',
    'present', 'http', 'https'
)

function Get-CopyrightHolderWords
{
    param([string] $Line)

    $name = $Line.ToLowerInvariant() -replace 'https?://\S+|www\.\S+', '' -replace '\(c\)|©', ''
    return @([regex]::Matches($name, '\p{L}+') | ForEach-Object Value |
        Where-Object { $_.Length -ge 4 -and $_ -notin $script:GenericHolderWords } | Select-Object -Unique)
}

function Get-CopyrightHolderKey
{
    param([string] $Line)

    return $Line.ToLowerInvariant() -replace 'https?://\S+|www\.\S+', '' -replace 'copyright|\(c\)|©|all rights reserved', '' -replace '[^\p{L}]', ''
}

function Get-CopyrightLines
{
    param([string] $LicenseText)

    $textLines = @(($LicenseText -replace "`r", '') -split "`n")
    $lines = for ($index = 0; $index -lt $textLines.Count; $index++)
    {
        if (-not (Test-CopyrightLine -Line $textLines[$index])) { continue }

        # The GNU licences' own Free Software Foundation line names no holder of the component
        $next = $textLines[($index + 1)..($index + 3)] -join "`n"
        if ($next -match 'permitted to copy and distribute verbatim copies') { continue }

        ($textLines[$index] -replace '^[ \t#*/]+', '').Trim()
    }

    return @($lines | Select-Object -Unique)
}


function Resolve-DotnetInstallMetadata
{
    param([Parameter(Mandatory)] [string] $Framework)

    $dotnetRoot = if ($env:DOTNET_ROOT) { $env:DOTNET_ROOT } else { Split-Path -Parent (Get-Command dotnet -ErrorAction SilentlyContinue).Source }

    $repository = switch -Wildcard ($Framework)
    {
        'Microsoft.AspNetCore.*' { 'https://github.com/dotnet/aspnetcore' }
        'Microsoft.WindowsDesktop.*' { 'https://github.com/dotnet/windowsdesktop' }
        default { 'https://github.com/dotnet/runtime' }
    }

    $licenseFile = Get-ChildItem -LiteralPath $dotnetRoot -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^license(\.txt)?$' } | Select-Object -First 1
    $licenseText = if ($licenseFile) { Get-Content -LiteralPath $licenseFile.FullName -Raw } else { '' }
    [pscustomobject]@{
        License           = 'MIT'
        Source            = $repository
        Website           = 'https://dot.net'
        Copyright         = '.NET Foundation and Contributors'
        LicenseText       = $licenseText
        LicenseTextOrigin = if ($licenseText) { 'package' } else { 'none' }
        Found             = [bool]$dotnetRoot
    }
}

function Resolve-LicenseFromUrl
{
    param(
        [Parameter(Mandatory)] [string] $Url,
        [Parameter(Mandatory)] [hashtable] $UrlMappings
    )

    foreach ($entry in $UrlMappings.exact.GetEnumerator())
    {
        if ($entry.Key -ieq $Url) { return $entry.Value }
    }

    foreach ($entry in $UrlMappings.contains.GetEnumerator())
    {
        if ($Url.IndexOf($entry.Key, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $entry.Value }
    }

    return $Url
}

# A licence expression's alternatives with its parentheses applied ("(A OR B) AND C" gives "A AND C" and "B AND C"), so
# the text printed and the LGPL notice read an expression the same way
function Get-LicenseAlternatives
{
    param([string] $License)

    $groups = @{}
    $expand = {
        param([string] $Expression)

        foreach ($alternative in ($Expression -split '\s+OR\s+'))
        {
            $conjunctions = @('')
            foreach ($term in ($alternative -split '\s+AND\s+' | ForEach-Object { $_.Trim() } | Where-Object { $_ }))
            {
                $options = if ($groups.ContainsKey($term)) { $groups[$term] } else { $term }
                $conjunctions = @(foreach ($left in $conjunctions) { foreach ($right in $options) { if ($left) { "$left AND $right" } else { $right } } })
            }

            $conjunctions | Where-Object { $_ }
        }
    }

    # Innermost group first, as Test-LicenseAllowed reads them; an unmatched parenthesis is dropped
    $expression = $License
    while ($expression -match '\(([^()]*)\)')
    {
        $key = "<$($groups.Count)>"
        $groups[$key] = @(& $expand $Matches[1])
        $expression = $expression.Replace($Matches[0], " $key ")
    }

    return @(& $expand ($expression -replace '[()]', ' '))
}

# The choice whose standard texts can all be read: its texts are printed, and the LGPL notice names its version
function Get-PrintedLicenseAlternative
{
    param(
        [string] $License
    )

    if (-not $License)
    {
        return ''
    }

    # A choice that offers another licence than the LGPL-3.0 is taken under that one, as Get-LgplVersion reads it,
    # unless no such licence has a standard text: then the LGPL-3.0 choice is printed, with its notice
    $alternatives = @(Get-LicenseAlternatives -License $License)
    $alternatives = @($alternatives | Where-Object { $_ -notmatch 'LGPL-3\.0' }) + @($alternatives | Where-Object { $_ -match 'LGPL-3\.0' })

    foreach ($alternative in $alternatives)
    {
        $ids = $alternative -split '\s+(?:AND|WITH)\s+' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
        $texts = @($ids | ForEach-Object { Get-SpdxLicenseText -Id $_ | Where-Object { $_ } })
        if ($ids -and $texts.Count -eq @($ids).Count)
        {
            return $alternative
        }
    }

    return ''
}

function Get-StandardLicenseText
{
    param(
        [string] $License
    )

    $alternative = Get-PrintedLicenseAlternative -License $License
    if (-not $alternative)
    {
        return ''
    }

    return @($alternative -split '\s+(?:AND|WITH)\s+' | ForEach-Object { Get-SpdxLicenseText -Id $_.Trim() }) -join "`n`n"
}

# Legal (2026-10-14): a template copyright line never ships; the Apache appendix and GPL How-to-Apply are licence text
function Update-TemplateCopyrightLine
{
    param(
        [string] $LicenseText,
        [string] $Copyright
    )

    $licenceLine = '^\s*Copyright (\[yyyy\] \[name of copyright owner\]|\{yyyy\} \{name of copyright owner\}|\(C\) <year>\s+<name of author>)\s*$'
    $changed = $false
    $lines = [Collections.Generic.List[string]]::new([string[]]($LicenseText -split '\r?\n'))
    for ($index = $lines.Count - 1; $index -ge 0; $index--)
    {
        # A holder is a letter outside placeholders (an email in <> counts): "Copyright (c) <year> <owner>" has none
        $line = $lines[$index]
        $holder = $line -replace '(?i)copyright|\(c\)|all rights reserved', '' -replace '<[^>@]*>|\[[^\]]*\]|\{[^}]*\}', '' -replace '[^\p{L}]', ''
        if ($line -notmatch '<[^>]+>|\[[^\]]+\]|\{[^}]+\}' -or $line -cnotmatch '^[ \t#*/]*(Copyright\b|©|\([cC]\))' -or $holder -or $line -match $licenceLine) { continue }

        $changed = $true
        $lines.RemoveAt($index)
        if ($Copyright)
        {
            $lines.InsertRange($index, [string[]]($Copyright -split '\r?\n'))
        }
        elseif ($index -gt 0 -and $index -lt $lines.Count -and -not $lines[$index - 1].Trim() -and -not $lines[$index].Trim())
        {
            $lines.RemoveAt($index)
        }
    }

    if (-not $changed) { return $LicenseText }
    return ($lines -join "`n").Trim()
}
