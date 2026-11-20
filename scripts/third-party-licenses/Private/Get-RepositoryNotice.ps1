$script:NoticeFilePattern = '^notice(\.(txt|md|html?))?$'
$script:LicenseFilePattern = '^(licen[cs]e|copying)(\.(txt|md|html?|rst))?$'
$script:FontLicenseFilePattern = '^ofl(\.(txt|md|html?|rst))?$'
$script:RepositoryListingCache = @{}
$script:RepositoryFileCache = @{}
$script:SpdxTextCache = @{}

function Import-RemoteCache
{
    param([string] $Path)

    if (-not $Path -or -not (Test-Path -LiteralPath $Path))
    {
        return
    }

    try
    {
        $cache = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
        if ($cache.Listings) { $script:RepositoryListingCache = $cache.Listings }
        if ($cache.Files) { $script:RepositoryFileCache = $cache.Files }
        if ($cache.SpdxTexts) { $script:SpdxTextCache = $cache.SpdxTexts }
    }
    catch
    {
        Write-Warning "Ignoring unreadable cache '$Path': $($_.Exception.Message)"
    }
}

function Export-RemoteCache
{
    param([string] $Path)

    if (-not $Path)
    {
        return
    }

    $cache = @{ Listings = $script:RepositoryListingCache; Files = $script:RepositoryFileCache; SpdxTexts = $script:SpdxTextCache }
    Set-Content -LiteralPath $Path -Value (ConvertTo-Json -InputObject $cache -Depth 5 -Compress) -Encoding utf8NoBOM
}

function Invoke-WithRetry
{
    param([Parameter(Mandatory)] [scriptblock] $Request)

    for ($attempt = 1; ; $attempt++)
    {
        try
        {
            return & $Request
        }
        catch
        {
            $status = [int]$_.Exception.Response.StatusCode
            if ($attempt -ge 3 -or ($status -ge 400 -and $status -lt 500)) { throw }
            Start-Sleep -Seconds 2
        }
    }
}

function Get-GitHubToken
{
    foreach ($token in $env:GITHUB_TOKEN, $env:GH_TOKEN)
    {
        if ($token) { return $token }
    }

    if (Get-Command gh -ErrorAction SilentlyContinue)
    {
        $token = & gh auth token 2>$null
        if ($LASTEXITCODE -eq 0 -and $token) { return ([string]$token).Trim() }
    }

    return ''
}

function Invoke-GitHubApi
{
    param(
        [Parameter(Mandatory)] [string] $Path,
        [switch] $Raw
    )

    $headers = @{ 'User-Agent' = 'Devolutions-ThirdPartyLicenses'; 'X-GitHub-Api-Version' = '2022-11-28' }
    $headers.Accept = if ($Raw) { 'application/vnd.github.raw' } else { 'application/vnd.github+json' }
    if ($null -eq $script:GitHubToken) { $script:GitHubToken = Get-GitHubToken }
    if ($script:GitHubToken) { $headers.Authorization = "Bearer $script:GitHubToken" }

    $uri = "https://api.github.com/$Path"
    if ($Raw)
    {
        $content = (Invoke-WithRetry { Invoke-WebRequest -Uri $uri -Headers $headers }).Content
        $text = if ($content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($content) } else { [string]$content }
        return $text.TrimStart([char]0xFEFF)
    }

    return Invoke-WithRetry { Invoke-RestMethod -Uri $uri -Headers $headers }
}

function Get-GitHubRepository
{
    param([string] $Url)

    if ($Url -match '^https?://(www\.)?github\.com/(?<owner>[^/]+)/(?<name>[^/#?]+)')
    {
        return "$($Matches.owner)/$($Matches.name -replace '\.git$', '')"
    }

    return ''
}

function Convert-HtmlToText
{
    param([Parameter(Mandatory)] [string] $Html)

    $text = $Html -replace '(?is)<(script|style|head)\b.*?</\1\s*>', ''
    $text = $text -replace '(?i)<br\s*/?>', "`n"
    $text = $text -replace '(?i)<li\b[^>]*>', "`n- "
    $text = $text -replace '(?i)</(p|div|h[1-6]|pre|table|tr|ul|ol|blockquote)\s*>', "`n`n"
    $text = $text -replace '(?i)<(p|div|h[1-6]|pre|table|tr|ul|ol|blockquote)\b[^>]*>', "`n`n"
    $text = $text -replace '(?s)<[^>]+>', ''
    $text = [Net.WebUtility]::HtmlDecode($text) -replace "`r", '' -replace [char]0xA0, ' '
    $text = ($text -split "`n" | ForEach-Object { $_.TrimEnd() }) -join "`n"
    return ($text -replace "`n{3,}", "`n`n").Trim()
}

function Get-RepositoryRootFiles
{
    param(
        [Parameter(Mandatory)] [string] $Repository,
        [string] $Commit
    )

    $key = "$Repository@$Commit".ToLowerInvariant()
    if (-not $script:RepositoryListingCache.ContainsKey($key))
    {
        $ref = $Commit
        try
        {
            try
            {
                $files = Invoke-GitHubApi -Path "repos/$Repository/contents$(if ($ref) { "?ref=$ref" })"
            }
            catch
            {
                if (-not $ref) { throw }
                $ref = ''
                $files = Invoke-GitHubApi -Path "repos/$Repository/contents"
            }

            $script:RepositoryListingCache[$key] = [pscustomobject]@{ Ref = $ref; Names = @($files | Where-Object type -eq 'file' | ForEach-Object name) }
        }
        catch
        {
            $script:RepositoryListingCache[$key] = [pscustomobject]@{ Error = $_.Exception.Message }
        }
    }

    $listing = $script:RepositoryListingCache[$key]
    if ($listing.Error) { throw $listing.Error }
    return $listing
}

function Get-RepositoryFile
{
    param(
        [string] $RepositoryUrl,
        [string] $Commit,
        [Parameter(Mandatory)] [string] $NamePattern
    )

    $repository = Get-GitHubRepository -Url $RepositoryUrl
    if (-not $repository)
    {
        return $null
    }

    $key = "$repository@$Commit/$NamePattern".ToLowerInvariant()
    if ($script:RepositoryFileCache.ContainsKey($key))
    {
        $cached = $script:RepositoryFileCache[$key]
        if ($cached -and $cached.Error) { throw $cached.Error }
        return $cached
    }

    $listing = Get-RepositoryRootFiles -Repository $repository -Commit $Commit
    $name = @($listing.Names | Where-Object { $_ -match $NamePattern }) | Select-Object -First 1
    $result = $null
    if ($name)
    {
        $escapedName = [uri]::EscapeDataString($name)
        try
        {
            $text = Invoke-GitHubApi -Raw -Path "repos/$repository/contents/$escapedName$(if ($listing.Ref) { "?ref=$($listing.Ref)" })"
        }
        catch
        {
            $script:RepositoryFileCache[$key] = [pscustomobject]@{ Error = $_.Exception.Message }
            throw
        }

        if ($name -match '\.html?$') { $text = Convert-HtmlToText -Html $text }

        $result = [pscustomobject]@{
            Repository = $repository
            File       = $name
            Url        = "https://github.com/$repository/blob/$(if ($listing.Ref) { $listing.Ref } else { 'HEAD' })/$escapedName"
            Text       = $text
        }
    }

    $script:RepositoryFileCache[$key] = $result
    return $result
}

function Get-RepositoryNotice
{
    param(
        [string] $RepositoryUrl,
        [string] $Commit
    )

    return Get-RepositoryFile -RepositoryUrl $RepositoryUrl -Commit $Commit -NamePattern $script:NoticeFilePattern
}

# A package's own NOTICE comes first, then an override's notices, then its repository's when that one differs
function Join-ComponentNotice
{
    param(
        [pscustomobject] $PackageNotice,
        [pscustomobject] $OverrideNotice,
        [pscustomobject] $RepositoryNotice
    )

    # The package text is trimmed and keeps its CRLF; the repository's comes as GitHub stores it
    if ($PackageNotice -and $RepositoryNotice -and ($RepositoryNotice.Text -replace "`r").Trim() -eq ($PackageNotice.Text -replace "`r").Trim())
    {
        $RepositoryNotice = $null
    }

    $notices = @($PackageNotice, $OverrideNotice, $RepositoryNotice | Where-Object { $_ })
    if ($notices.Count -lt 2) { return $notices | Select-Object -First 1 }

    return [pscustomobject]@{
        Repository = $notices[-1].Repository
        File       = $notices[-1].File
        Url        = $notices[-1].Url
        Text       = @($notices.Text) -join "`n`n"
    }
}

function Get-RepositoryLicense
{
    param(
        [string] $RepositoryUrl,
        [string] $Commit
    )

    # A font project may keep its licence only as OFL.txt (Open Sans)
    $license = Get-RepositoryFile -RepositoryUrl $RepositoryUrl -Commit $Commit -NamePattern $script:LicenseFilePattern
    if (-not $license) { $license = Get-RepositoryFile -RepositoryUrl $RepositoryUrl -Commit $Commit -NamePattern $script:FontLicenseFilePattern }
    return $license
}

# The source of a component's shipped version: its release tag (v1.2.3 or 1.2.3) when the repository has one
function Get-RepositoryTagUrl
{
    param(
        [Parameter(Mandatory)] [string] $RepositoryUrl,
        [string] $Version
    )

    $repository = Get-GitHubRepository -Url $RepositoryUrl
    if (-not $repository -or -not $Version -or $Version -match ',') { return $RepositoryUrl }

    $key = "tag:$repository@$Version".ToLowerInvariant()
    if (-not $script:RepositoryFileCache.ContainsKey($key))
    {
        foreach ($tag in "v$Version", $Version)
        {
            try
            {
                $null = Invoke-GitHubApi -Path "repos/$repository/git/ref/tags/$([uri]::EscapeDataString($tag))"
                $script:RepositoryFileCache[$key] = "https://github.com/$repository/tree/$tag"
                break
            }
            catch
            {
                # Only a 404 says the tag does not exist; any other failure is not cached, so a later run asks again
                if ([int]$_.Exception.Response.StatusCode -ne 404) { throw }
            }
        }

        if (-not $script:RepositoryFileCache.ContainsKey($key)) { $script:RepositoryFileCache[$key] = '' }
    }

    $url = $script:RepositoryFileCache[$key]
    return $(if ($url) { $url } else { "https://github.com/$repository" })
}

function Get-SpdxLicenseText
{
    param([Parameter(Mandatory)] [string] $Id)

    if (-not $script:SpdxTextCache.ContainsKey($Id))
    {
        $uri = "https://raw.githubusercontent.com/spdx/license-list-data/main/text/$([uri]::EscapeDataString($Id)).txt"
        $text = try { [string](Invoke-WithRetry { Invoke-WebRequest -Uri $uri }).Content } catch { '' }
        $script:SpdxTextCache[$Id] = $text
    }

    return $script:SpdxTextCache[$Id]
}
