#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Maintains and validates the checked-in icon database JSON.

.DESCRIPTION
    Treats WebBasedData/screenshot-database-v2.json as the source of truth,
    recalculates package_count from the JSON payload, and can validate icon and
    screenshot URLs while honoring invalid_urls.txt.

.EXAMPLE
    ./scripts/update-icons.ps1

    Recomputes package_count and rewrites screenshot-database-v2.json.

.EXAMPLE
    ./scripts/update-icons.ps1 -Validate -MaxPackages 25

    Validates the first 25 package entries from screenshot-database-v2.json.

.EXAMPLE
    ./scripts/update-icons.ps1 -Validate -AppendInvalidUrls

    Validates all icon and screenshot URLs and appends any new failures to
    WebBasedData/invalid_urls.txt.
#>

[CmdletBinding(DefaultParameterSetName = 'Sync')]
param(
    [Parameter(ParameterSetName = 'Sync')]
    [switch] $Sync,

    [Parameter(ParameterSetName = 'Validate')]
    [switch] $Validate,

    [string] $JsonPath = 'WebBasedData/screenshot-database-v2.json',
    [string] $InvalidUrlsPath = 'WebBasedData/invalid_urls.txt',
    [switch] $AppendInvalidUrls,
    [int] $MaxPackages,
    [int] $MaxRetries = 2,
    [int] $RetryDelayMilliseconds = 200,
    [int] $RequestTimeoutSeconds = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

if (-not $PSBoundParameters.ContainsKey('Sync') -and -not $PSBoundParameters.ContainsKey('Validate')) {
    $Sync = $true
}

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

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Get-ForbiddenUrlSet {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $lookup = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    if (-not (Test-Path -LiteralPath $Path)) {
           return ,$lookup
    }

    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        $value = $line.Trim()
        if ($value.Length -gt 0) {
            [void] $lookup.Add($value)
        }
    }

    return ,$lookup
}

function ConvertTo-ForbiddenUrlSet {
    param(
        [AllowNull()]
        [object] $Value
    )

    $lookup = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    if ($null -eq $Value) {
        return ,$lookup
    }

    foreach ($item in @($Value)) {
        if ($null -eq $item) {
            continue
        }

        $text = [string] $item
        if ([string]::IsNullOrWhiteSpace($text)) {
            continue
        }

        [void] $lookup.Add($text)
    }

    return ,$lookup
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

    if (-not $database.Contains('package_count')) {
        $database['package_count'] = [ordered]@{}
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
    $seenImages = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($image in @($Entry['images'])) {
        if ($null -eq $image) {
            continue
        }

        $trimmedImage = [string] $image
        if ([string]::IsNullOrWhiteSpace($trimmedImage)) {
            continue
        }

        if ($seenImages.Add($trimmedImage)) {
            $images += $trimmedImage
        }
    }

    $Entry['icon'] = [string] $Entry['icon']
    $Entry['images'] = $images
}

function Update-PackageCount {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary] $Database,

        [AllowNull()]
        [object] $ForbiddenUrls
    )

    $ForbiddenUrls = ConvertTo-ForbiddenUrlSet -Value $ForbiddenUrls

    $entries = $Database['icons_and_screenshots']

    $total = 0
    $done = 0
    $packagesWithIcon = 0
    $packagesWithScreenshot = 0
    $totalScreenshots = 0

    foreach ($packageId in $entries.Keys) {
        $total++
        $entry = $entries[$packageId]
        Normalize-Entry -Entry $entry

        $iconUrl = [string] $entry['icon']
        $validIcon = -not [string]::IsNullOrWhiteSpace($iconUrl) -and -not $ForbiddenUrls.Contains($iconUrl)

        if ($validIcon) {
            $done++
            $packagesWithIcon++
        }

        $validScreenshots = @(
            $entry['images'] | Where-Object {
                -not [string]::IsNullOrWhiteSpace($_) -and -not $ForbiddenUrls.Contains($_)
            }
        )
        if ($validScreenshots.Count -gt 0) {
            $packagesWithScreenshot++
            $totalScreenshots += $validScreenshots.Count
        }
    }

    $Database['package_count'] = [ordered]@{
        total = $total
        done = $done
        packages_with_icon = $packagesWithIcon
        packages_with_screenshot = $packagesWithScreenshot
        total_screenshots = $totalScreenshots
    }
}

function Write-IconDatabase {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary] $Database,

        [Parameter(Mandatory)]
        [string] $Path
    )

    $json = $Database | ConvertTo-Json -Depth 100
    Write-Utf8File -Path $Path -Content $json
}

function Invoke-UrlRequest {
    param(
        [Parameter(Mandatory)]
        [uri] $Uri,

        [Parameter(Mandatory)]
        [System.Net.Http.HttpClient] $Client,

        [Parameter(Mandatory)]
        [int] $MaxRetries,

        [Parameter(Mandatory)]
        [int] $RetryDelayMilliseconds
    )

    $attempt = 0
    do {
        try {
            $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $Uri)
            $response = $Client.Send($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead)
            return $response
        }
        catch {
            if ($attempt -ge $MaxRetries) {
                throw
            }

            $attempt++
            Start-Sleep -Milliseconds $RetryDelayMilliseconds
        }
    } while ($true)
}

function Test-IconUrls {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary] $Database,

        [AllowNull()]
        [object] $ForbiddenUrls,

        [Parameter(Mandatory)]
        [string] $InvalidUrlsPath,

        [switch] $AppendInvalidUrls,
        [int] $MaxPackages,
        [int] $MaxRetries,
        [int] $RetryDelayMilliseconds,
        [int] $RequestTimeoutSeconds
    )

    $ForbiddenUrls = ConvertTo-ForbiddenUrlSet -Value $ForbiddenUrls

    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds($RequestTimeoutSeconds)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('UniGetUI-IconValidator/2.0')

    $newInvalidUrls = [System.Collections.Generic.List[string]]::new()
    $inspectedPackages = 0
    $testedUrls = 0

    try {
        foreach ($packageId in $Database['icons_and_screenshots'].Keys) {
            if ($MaxPackages -gt 0 -and $inspectedPackages -ge $MaxPackages) {
                break
            }

            $entry = $Database['icons_and_screenshots'][$packageId]
            Normalize-Entry -Entry $entry

            $urls = @()
            if (-not [string]::IsNullOrWhiteSpace($entry['icon'])) {
                $urls += [string] $entry['icon']
            }
            $urls += @($entry['images'])

            foreach ($url in $urls) {
                if ([string]::IsNullOrWhiteSpace($url) -or $ForbiddenUrls.Contains($url)) {
                    continue
                }

                $testedUrls++
                try {
                    $response = Invoke-UrlRequest -Uri ([uri] $url) -Client $client -MaxRetries $MaxRetries -RetryDelayMilliseconds $RetryDelayMilliseconds
                    try {
                        $statusCode = [int] $response.StatusCode
                        if ($statusCode -ne 200 -and $statusCode -ne 403) {
                            [void] $newInvalidUrls.Add($url)
                            Write-Warning "[$packageId] $url returned HTTP $statusCode"
                        }
                    }
                    finally {
                        $response.Dispose()
                    }
                }
                catch {
                    [void] $newInvalidUrls.Add($url)
                    Write-Warning "[$packageId] Failed to fetch $url. $($_.Exception.Message)"
                }
            }

            $inspectedPackages++
        }
    }
    finally {
        $client.Dispose()
    }

    $distinctInvalidUrls = $newInvalidUrls | Select-Object -Unique
    if ($AppendInvalidUrls -and $distinctInvalidUrls.Count -gt 0) {
        $directory = Split-Path -Parent $InvalidUrlsPath
        if (-not [string]::IsNullOrWhiteSpace($directory)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }

        $existing = Get-ForbiddenUrlSet -Path $InvalidUrlsPath
        $linesToAppend = @()
        foreach ($url in $distinctInvalidUrls) {
            if ($existing.Add($url)) {
                $linesToAppend += $url
            }
        }

        if ($linesToAppend.Count -gt 0) {
            [System.IO.File]::AppendAllLines(
                $InvalidUrlsPath,
                $linesToAppend,
                [System.Text.UTF8Encoding]::new($false)
            )
        }
    }

    Write-Host "Packages inspected: $inspectedPackages"
    Write-Host "URLs tested: $testedUrls"
    Write-Host "New invalid URLs found: $($distinctInvalidUrls.Count)"
}

$resolvedJsonPath = Resolve-RepoPath -Path $JsonPath
$resolvedInvalidUrlsPath = Resolve-RepoPath -Path $InvalidUrlsPath

$database = Read-IconDatabase -Path $resolvedJsonPath
$forbiddenUrls = Get-ForbiddenUrlSet -Path $resolvedInvalidUrlsPath

Update-PackageCount -Database $database -ForbiddenUrls (,$forbiddenUrls)

if ($Validate) {
    Test-IconUrls -Database $database -ForbiddenUrls (,$forbiddenUrls) -InvalidUrlsPath $resolvedInvalidUrlsPath -AppendInvalidUrls:$AppendInvalidUrls -MaxPackages $MaxPackages -MaxRetries $MaxRetries -RetryDelayMilliseconds $RetryDelayMilliseconds -RequestTimeoutSeconds $RequestTimeoutSeconds
    return
}

Write-IconDatabase -Database $database -Path $resolvedJsonPath
Write-Host "Updated icon database counts in $resolvedJsonPath"
