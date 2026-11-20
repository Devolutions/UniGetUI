$script:NoticesJsonFormat = 'devolutions-third-party-notices/1'

# The "Download" column of the in-product views: where the shipped version can be downloaded. JSON only: the notices
# file's format is fixed by PI-831.
function Get-ComponentDownload
{
    param([Parameter(Mandatory)] [pscustomobject] $Component)

    # Several deps.json can list different versions ("1.0, 2.0"): the package page then, which lists them all
    $version = if ($Component.Version -and $Component.Version -notmatch ',') { [string]$Component.Version }

    if ($Component.Ecosystem -eq 'nuget' -and $Component.IsRuntime)
    {
        return $(if ($version -match '^(\d+\.\d+)') { "https://dotnet.microsoft.com/download/dotnet/$($Matches[1])" } else { 'https://dotnet.microsoft.com/download/dotnet' })
    }

    if ($Component.Ecosystem -eq 'nuget')
    {
        return "https://www.nuget.org/packages/$($Component.Name)$(if ($version) { "/$version" })"
    }

    if ($Component.Ecosystem -eq 'npm')
    {
        return "https://www.npmjs.com/package/$($Component.Name)$(if ($version) { "/v/$version" })"
    }

    # No registry for a manual component: where its owner publishes it, else its source
    if ($Component.Ecosystem -eq 'manual')
    {
        return $(if ($Component.Website) { [string]$Component.Website } else { [string](Get-ComponentPage -Component $Component) })
    }

    return ''
}

function ConvertTo-NoticesJson
{
    param(
        [Parameter(Mandatory)] [string] $AppName,
        [string] $AppVersion,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Components,
        [object[]] $Inventory
    )

    $document = [ordered]@{
        format     = $script:NoticesJsonFormat
        appName    = $AppName
        appVersion = $AppVersion
        components = @(foreach ($component in $Components)
        {
            $notice = if ($component.Notice -and $component.Notice.Text)
            {
                [ordered]@{
                    repository = [string]$component.Notice.Repository
                    file       = [string]$component.Notice.File
                    url        = [string]$component.Notice.Url
                    text       = [string]$component.Notice.Text
                }
            }

            $entry = [ordered]@{
                name              = [string]$component.Name
                version           = [string]$component.Version
                ecosystem         = [string]$component.Ecosystem
                license           = [string]$component.License
                source            = [string](Get-ComponentPage -Component $component)
                download          = [string](Get-ComponentDownload -Component $component)
                repository        = [string]$component.Source
                website           = [string]$component.Website
                copyright         = [string]$component.Copyright
                licenseText       = [string]$component.LicenseText
                licenseTextOrigin = [string]$component.LicenseTextOrigin
                isRuntime         = [bool]$component.IsRuntime
                notice            = $notice
            }

            # The texts its notice refers to by label, so a run merged from this JSON prints them too
            if ($component.SharedTexts)
            {
                $entry.sharedTexts = @($component.SharedTexts | ForEach-Object { [ordered]@{ id = [string]$_.Id; text = [string]$_.Text } })
            }

            $entry
        })
    }

    if ($null -ne $Inventory)
    {
        $document.inventory = $Inventory
    }

    return ConvertTo-Json -InputObject $document -Depth 5
}

# Components read back from a notices JSON are already resolved: the job that wrote it had the NuGet cache or
# node_modules and did the lookups. They are merged as they are, without a second resolution.
function Read-NoticesJson
{
    param([Parameter(Mandatory)] [string] $Path)

    $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    if ($document.format -ne $script:NoticesJsonFormat)
    {
        throw "'$Path' is not a notices JSON written by -Json (expected format '$script:NoticesJsonFormat')."
    }

    $components = foreach ($entry in @($document.components))
    {
        [pscustomobject]@{
            Ecosystem         = [string]$entry.ecosystem
            Name              = [string]$entry.name
            Version           = [string]$entry.version
            License           = [string]$entry.license
            Source            = [string]$entry.repository
            Website           = [string]$entry.website
            Copyright         = [string]$entry.copyright
            LicenseText       = [string]$entry.licenseText
            LicenseTextOrigin = [string]$entry.licenseTextOrigin
            RepositoryCommit  = ''
            IsRuntime         = [bool]$entry.isRuntime
            Notice            = if ($entry.notice)
            {
                [pscustomobject]@{ Repository = [string]$entry.notice.repository; File = [string]$entry.notice.file; Url = [string]$entry.notice.url; Text = [string]$entry.notice.text }
            }
            SharedTexts       = @($entry.sharedTexts | Where-Object { $_ } | ForEach-Object { [pscustomobject]@{ Id = [string]$_.id; Text = [string]$_.text } })
            IsResolved        = $true
        }
    }

    [pscustomobject]@{
        AppName    = [string]$document.appName
        AppVersion = [string]$document.appVersion
        Components = @($components)
        Inventory  = @($document.inventory)
    }
}
