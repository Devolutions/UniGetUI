function Get-WebpackComponents
{
    param([Parameter(Mandatory)] [string] $Path)

    $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    if ($document.format -ne 'devolutions-webpack-components/1')
    {
        throw "'$Path' is not a webpack component inventory."
    }

    foreach ($entry in $document.components)
    {
        if (-not $entry.name -or -not $entry.version)
        {
            throw "'$Path': every bundled package needs a name and version."
        }

        $source = [string]$entry.source
        $shorthand = [regex]::Match($source, '^(?:(?<host>github|gitlab|bitbucket):)?(?<path>[\w.-]+/[\w.-]+)$')
        if ($shorthand.Success)
        {
            $hosts = @{ '' = 'github.com'; github = 'github.com'; gitlab = 'gitlab.com'; bitbucket = 'bitbucket.org' }
            $source = "https://$($hosts[$shorthand.Groups['host'].Value])/$($shorthand.Groups['path'].Value)"
        }

        [pscustomobject]@{
            Ecosystem         = 'npm'
            Name              = [string]$entry.name
            Version           = [string]$entry.version
            License           = [string]$entry.license
            Source            = $source
            Website           = [string]$entry.website
            Copyright         = [string]$entry.copyright
            LicenseText       = [string]$entry.licenseText
            LicenseTextOrigin = if ($entry.licenseText) { 'package' } else { 'none' }
            RepositoryCommit  = ''
            IsRuntime         = $false
            Notice            = if ($entry.noticeText)
            {
                [pscustomobject]@{ Repository = $source; File = 'NOTICE'; Url = ''; Text = [string]$entry.noticeText }
            }
        }
    }
}
