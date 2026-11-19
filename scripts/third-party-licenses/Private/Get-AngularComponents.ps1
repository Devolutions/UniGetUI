function Get-AngularComponents
{
    param(
        [Parameter(Mandatory, ParameterSetName = 'File')] [string] $LicensesFile,
        # A list read from one of our assemblies: no node_modules folder belongs to it
        [Parameter(Mandatory, ParameterSetName = 'Text')] [AllowEmptyString()] [string] $Text
    )

    $nodeModules = $null
    $directory = if ($LicensesFile) { Split-Path -Parent (Resolve-Path -LiteralPath $LicensesFile) }
    while ($directory)
    {
        $candidate = Join-Path $directory 'node_modules'
        if (Test-Path -LiteralPath $candidate)
        {
            $nodeModules = $candidate
            break
        }

        $directory = Split-Path -Parent $directory
    }

    $content = $(if ($LicensesFile) { Get-Content -LiteralPath $LicensesFile -Raw } else { $Text }) -replace "`r`n", "`n"
    $blocks = $content -split ('-' * 80 + "`n")

    foreach ($block in $blocks)
    {
        if ($block -notmatch '(?s)^\s*Package: (?<name>[^\n]+)\n(?:Version: (?<version>[^\n]*)\n)?(?:Source: (?<source>[^\n]*)\n)?License: (?<license>.*?)\n\n(?<text>.*)$')
        {
            continue
        }

        $name = $Matches.name.Trim()
        # A bundle's list read from one of our assemblies has no node_modules beside it, so it carries these two itself
        $listedVersion = ([string]$Matches.version).Trim()
        $listedSource = ([string]$Matches.source).Trim()
        $license = try { $Matches.license | ConvertFrom-Json -AsHashtable } catch { $Matches.license.Trim() }
        if ($license -is [hashtable]) { $license = $license.type }
        if ($license -is [array]) { $license = ($license | ForEach-Object { if ($_ -is [hashtable]) { $_.type } else { $_ } }) -join ' OR ' }

        $version = ''
        $source = ''
        $website = ''
        if ($nodeModules)
        {
            $packageJsonPath = Join-Path $nodeModules $name 'package.json'
            if (Test-Path -LiteralPath $packageJsonPath)
            {
                $packageJson = Get-Content -LiteralPath $packageJsonPath -Raw | ConvertFrom-Json -AsHashtable
                $version = [string]$packageJson.version
                $website = [string]$packageJson.homepage
                $repository = $packageJson.repository
                $source = if ($repository -is [hashtable]) { [string]$repository.url } else { [string]$repository }
            }
        }

        if ($listedVersion) { $version = $listedVersion }
        if ($listedSource) { $source = $listedSource }

        # npm shorthand: "owner/repo" and "github:owner/repo" name a GitHub repository, only in the repository
        # field: a homepage without a scheme is not one. [regex] keeps $Matches, which still holds this block.
        $shorthand = [regex]::Match($source, '^(?:(?<host>github|gitlab|bitbucket):)?(?<path>[\w.-]+/[\w.-]+)$')
        if ($shorthand.Success)
        {
            $hosts = @{ '' = 'github.com'; 'github' = 'github.com'; 'gitlab' = 'gitlab.com'; 'bitbucket' = 'bitbucket.org' }
            $source = "https://$($hosts[$shorthand.Groups['host'].Value])/$($shorthand.Groups['path'].Value)"
        }

        if (-not $source) { $source = $website }

        [pscustomobject]@{
            Ecosystem   = 'npm'
            Name        = $name
            Version     = $version
            License     = [string]$license
            Source      = $source -replace '^git\+', '' -replace '\.git$', ''
            Website     = $website
            LicenseText = $Matches.text.Trim()
        }
    }
}
