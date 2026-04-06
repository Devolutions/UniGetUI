#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Exports the icon database to a browsable Markdown gallery.

.DESCRIPTION
    Reads WebBasedData/screenshot-database-v2.json and writes a Markdown file
    that lists every package with embedded remote icon and screenshot images,
    making it easy to review the database in a Markdown preview without
    downloading each asset manually.

.EXAMPLE
    ./scripts/export-icon-database-gallery.ps1

    Writes WebBasedData/screenshot-database-gallery.md.

.EXAMPLE
    ./scripts/export-icon-database-gallery.ps1 -MaxPackages 100 -OutputPath temp/icon-gallery.md

    Writes a gallery for the first 100 package entries to temp/icon-gallery.md.

.EXAMPLE
    ./scripts/export-icon-database-gallery.ps1 -IncludePackageName 'devolutions*'

    Writes a gallery that only includes package names matching the provided
    wildcard pattern.

.EXAMPLE
    ./scripts/export-icon-database-gallery.ps1 -IncludePackageName 'remote','rdm' -ExcludePackageName '*agent*'

    Writes a gallery for packages whose names contain remote or rdm, excluding
    names that contain agent.
#>

[CmdletBinding()]
param(
    [string] $JsonPath = 'WebBasedData/screenshot-database-v2.json',
    [string] $OutputPath = 'WebBasedData/screenshot-database-gallery.md',
    [string[]] $IncludePackageName = @(),
    [string[]] $ExcludePackageName = @(),
    [int] $MaxPackages,
    [int] $IconWidth = 96,
    [int] $ScreenshotWidth = 360
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Resolve-RepoPath {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $Content
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Read-IconDatabase {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Icon database file not found: $Path"
    }

    $database = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    if (-not ($database -is [System.Collections.IDictionary])) {
        throw 'Icon database root must be a JSON object.'
    }

    if (-not $database.Contains('icons_and_screenshots')) {
        throw 'Icon database is missing icons_and_screenshots.'
    }

    if (-not ($database['icons_and_screenshots'] -is [System.Collections.IDictionary])) {
        throw 'icons_and_screenshots must be a JSON object.'
    }

    return $database
}

function Normalize-Entry {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary] $Entry
    )

    if (-not $Entry.Contains('icon') -or $null -eq $Entry['icon']) {
        $Entry['icon'] = ''
    }

    if (-not $Entry.Contains('images') -or $null -eq $Entry['images']) {
        $Entry['images'] = @()
    }

    $images = @()
    foreach ($image in @($Entry['images'])) {
        if ($null -eq $image) {
            continue
        }

        $value = [string] $image
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }

        $images += $value
    }

    $Entry['icon'] = [string] $Entry['icon']
    $Entry['images'] = $images
}

function Get-MarkdownSafeText {
    param(
        [Parameter(Mandatory)]
        [string] $Value
    )

    return $Value.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;')
}

function New-RemoteImageMarkup {
    param(
        [Parameter(Mandatory)]
        [string] $Url,

        [Parameter(Mandatory)]
        [string] $Alt,

        [Parameter(Mandatory)]
        [int] $Width
    )

    $safeAlt = Get-MarkdownSafeText -Value $Alt
    $safeUrl = Get-MarkdownSafeText -Value $Url
    return ('<a href="{0}"><img src="{0}" alt="{1}" width="{2}" /></a>' -f $safeUrl, $safeAlt, $Width)
}

function ConvertTo-WildcardPattern {
    param(
        [Parameter(Mandatory)]
        [string] $Value
    )

    $trimmedValue = $Value.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmedValue)) {
        return $null
    }

    if ($trimmedValue.IndexOfAny(@('*', '?')) -ge 0) {
        return $trimmedValue
    }

    return ('*{0}*' -f $trimmedValue)
}

function Test-PackageNameMatch {
    param(
        [Parameter(Mandatory)]
        [string] $PackageId,

        [Parameter(Mandatory)]
        [string[]] $Patterns
    )

    foreach ($pattern in $Patterns) {
        if ($PackageId -like $pattern) {
            return $true
        }
    }

    return $false
}

$resolvedJsonPath = Resolve-RepoPath -Path $JsonPath
$resolvedOutputPath = Resolve-RepoPath -Path $OutputPath

$database = Read-IconDatabase -Path $resolvedJsonPath
$entries = $database['icons_and_screenshots']

$includePatterns = @(
    $IncludePackageName |
        ForEach-Object { ConvertTo-WildcardPattern -Value $_ } |
        Where-Object { $null -ne $_ }
)
$excludePatterns = @(
    $ExcludePackageName |
        ForEach-Object { ConvertTo-WildcardPattern -Value $_ } |
        Where-Object { $null -ne $_ }
)

$builder = [System.Text.StringBuilder]::new()
$generatedUtc = [DateTime]::UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'")
$exportedPackages = 0

[void] $builder.AppendLine('# Icon Database Gallery')
[void] $builder.AppendLine()
[void] $builder.AppendLine("Generated from $JsonPath on $generatedUtc.")
[void] $builder.AppendLine()
if ($includePatterns.Count -gt 0) {
    [void] $builder.AppendLine(('Included package filters: {0}' -f (($IncludePackageName | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ', ')))
}
if ($excludePatterns.Count -gt 0) {
    [void] $builder.AppendLine(('Excluded package filters: {0}' -f (($ExcludePackageName | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ', ')))
}
[void] $builder.AppendLine()
[void] $builder.AppendLine('This file is intended for Markdown preview. Click any image to open the original URL.')
[void] $builder.AppendLine()

$packageCount = 0
foreach ($packageId in $entries.Keys) {
    if ($MaxPackages -gt 0 -and $packageCount -ge $MaxPackages) {
        break
    }

    if ($packageId -eq '__test_entry_DO_NOT_EDIT_PLEASE') {
        continue
    }

    if ($includePatterns.Count -gt 0 -and -not (Test-PackageNameMatch -PackageId $packageId -Patterns $includePatterns)) {
        continue
    }

    if ($excludePatterns.Count -gt 0 -and (Test-PackageNameMatch -PackageId $packageId -Patterns $excludePatterns)) {
        continue
    }

    $entry = $entries[$packageId]
    Normalize-Entry -Entry $entry

    $iconUrl = [string] $entry['icon']
    $images = @($entry['images'])

    [void] $builder.AppendLine(('## {0}' -f $packageId))
    [void] $builder.AppendLine()

    if ([string]::IsNullOrWhiteSpace($iconUrl)) {
        [void] $builder.AppendLine('Icon: none')
    }
    else {
        [void] $builder.AppendLine('Icon:')
        [void] $builder.AppendLine()
        [void] $builder.AppendLine((New-RemoteImageMarkup -Url $iconUrl -Alt "$packageId icon" -Width $IconWidth))
        [void] $builder.AppendLine()
        [void] $builder.AppendLine($iconUrl)
    }

    [void] $builder.AppendLine()

    if ($images.Count -eq 0) {
        [void] $builder.AppendLine('Screenshots: none')
    }
    else {
        [void] $builder.AppendLine(('Screenshots: {0}' -f $images.Count))
        [void] $builder.AppendLine()

        $screenshotIndex = 1
        foreach ($imageUrl in $images) {
            [void] $builder.AppendLine(('Screenshot {0}:' -f $screenshotIndex))
            [void] $builder.AppendLine()
            [void] $builder.AppendLine((New-RemoteImageMarkup -Url $imageUrl -Alt ('{0} screenshot {1}' -f $packageId, $screenshotIndex) -Width $ScreenshotWidth))
            [void] $builder.AppendLine()
            [void] $builder.AppendLine($imageUrl)
            [void] $builder.AppendLine()
            $screenshotIndex++
        }
    }

    [void] $builder.AppendLine('---')
    [void] $builder.AppendLine()
    $packageCount++
    $exportedPackages++
}

[void] $builder.Insert(0, ("Packages exported: {0}`r`n`r`n" -f $exportedPackages))

Write-Utf8File -Path $resolvedOutputPath -Content $builder.ToString()
Write-Host "Wrote icon database gallery to $resolvedOutputPath"