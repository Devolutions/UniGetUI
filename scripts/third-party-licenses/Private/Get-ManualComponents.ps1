# For what the build outputs do not describe: source code copied into the repo, a font or another vendor's assembly
# inside one of our packages, a package that angular.json copies from node_modules. Paths in the file are relative to
# the file itself, so it can live next to the app it belongs to.
function Get-ManualComponents
{
    param(
        [Parameter(Mandatory)] [string] $Path,
        [string] $BinariesPath
    )

    $directory = Split-Path -Parent (Resolve-Path -LiteralPath $Path)
    $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable

    $readText = {
        param($entry, [string] $field)

        $file = Join-Path $directory $entry[$field]
        if (Test-Path -LiteralPath $file -PathType Leaf)
        {
            return Get-Content -LiteralPath $file -Raw
        }

        Write-Warning "'$Path': $field '$($entry[$field])' of '$($entry.name)' not found."
        return ''
    }

    # Read from what ships when it can be, so the version follows upgrades: a package.json, or an assembly of the app
    $readVersion = {
        param($entry)

        if ($entry.versionPath)
        {
            $packageJson = & $readText $entry 'versionPath'
            return $(if ($packageJson) { [string]($packageJson | ConvertFrom-Json).version } else { '' })
        }

        if ($entry.versionAssembly)
        {
            $assembly = if ($BinariesPath) { Get-ChildItem -LiteralPath $BinariesPath -Recurse -File -Filter $entry.versionAssembly | Select-Object -First 1 }
            if (-not $assembly)
            {
                Write-Warning "'$Path': versionAssembly '$($entry.versionAssembly)' of '$($entry.name)' not found in -BinariesPath."
                return ''
            }

            $info = Get-FileVersionProperties -Path $assembly.FullName
            return ([string]$(if ($info.ProductVersion) { $info.ProductVersion } else { $info.FileVersion }) -split '[+ ]')[0]
        }

        return [string]$entry.version
    }

    foreach ($entry in @($document.components))
    {
        if (-not $entry.name)
        {
            throw "'$Path': every component needs a name."
        }

        $licenseText = if ($entry.licenseTextPath) { & $readText $entry 'licenseTextPath' } else { '' }
        $noticeText = if ($entry.noticePath) { & $readText $entry 'noticePath' } else { '' }

        [pscustomobject]@{
            Ecosystem         = 'manual'
            Name              = [string]$entry.name
            Version           = & $readVersion $entry
            License           = [string]$entry.license
            Source            = [string]$entry.source
            Website           = [string]$entry.website
            Copyright         = [string]$entry.copyright
            LicenseText       = $licenseText
            LicenseTextOrigin = if ($licenseText) { 'manual' } else { 'none' }
            RepositoryCommit  = [string]$entry.repositoryCommit
            Notice            = if ($noticeText) { [pscustomobject]@{ Repository = ''; File = (Split-Path $entry.noticePath -Leaf); Url = ''; Text = $noticeText } }
        }
    }
}
