# A statement legal requires for a component in named apps, in legal's own words (package-overrides.json "statement");
# {{app_name}} in it names the app whose notices print it (legal approved the Launcher's name, 2026-10-16)
function Get-ComponentStatement
{
    param(
        [hashtable] $Override,
        [string] $AppName,
        [switch] $Signed
    )

    $statement = if ($Override) { $Override.statement }
    if (-not $statement -or $AppName -notin @($statement.apps) -or ($statement.signedOnly -and -not $Signed)) { return '' }
    return ([string]$statement.text).Replace('{{app_name}}', $AppName)
}

# Upstream texts of third-party code compiled into a package's binary (PDFium's libraries), one verbatim file each under
# its project's name (package-overrides.json "notices")
function Get-OverrideNotice
{
    param(
        [object[]] $Notices,
        [Parameter(Mandatory)] [scriptblock] $ResolvePath
    )

    if (-not $Notices) { return $null }

    $texts = foreach ($notice in $Notices)
    {
        $path = & $ResolvePath $notice.path
        $text = if ($path) { "$(Get-Content -LiteralPath $path -Raw)".Trim() }
        if (-not $text)
        {
            # Content never fails a build: the notices are written without it, like a missing manual file
            Write-Warning "package-overrides.json: notice file '$($notice.path)' of '$($notice.name)' not found or empty."
            continue
        }

        "$($notice.name):`n$text"
    }

    if (-not $texts) { return $null }
    return [pscustomobject]@{ Repository = ''; File = ''; Url = ''; Text = @($texts) -join "`n`n" }
}

# A licence given only as an address carries no holder either: legal named both for Mono.Posix.NETStandard (2026-10-15)
function Set-OverrideFields
{
    param(
        [Parameter(Mandatory)] [pscustomobject] $Component,
        [Parameter(Mandatory)] [hashtable] $Override
    )

    $givenAsAddress = $Override.license -and $Component.License -match '^https?://'
    $replacesLicense = $givenAsAddress -or ($Override.license -and $Component.License -eq 'Custom')
    if ((-not $Component.License -or $replacesLicense) -and $Override.license) { $Component.License = $Override.license }
    if (-not $Component.Source -and $Override.source) { $Component.Source = $Override.source }
    if (-not $Component.Website -and $Override.website) { $Component.Website = $Override.website }
    if ((-not $Component.Copyright -or $givenAsAddress) -and $Override.copyright) { $Component.Copyright = $Override.copyright }

    # Legal's notice is the whole attribution of some commercial components: the licence file they ship is not printed
    if ($Override.omitLicenseText)
    {
        # Kept for the approval, so a new licence from the package is approved again
        $Component | Add-Member -NotePropertyName OmittedLicenseText -NotePropertyValue ([string]$Component.LicenseText) -Force
        $Component.LicenseText = ''
        $Component.LicenseTextOrigin = 'omitted'
    }
}
