$script:SidecarSuffix = '.LICENSE.txt'
$script:BundleListSuffix = '.3rdpartylicenses.txt'
$script:AvaloniaResourcesName = '!AvaloniaResources'

function Get-OwnAssemblies
{
    param([Parameter(Mandatory)] [string] $Directory)

    # Third-party DLLs are described by their package; only our own assemblies and executables carry unlisted content
    $root = (Get-Item -LiteralPath $Directory).FullName
    Get-ChildItem -LiteralPath $root -File -Recurse |
        Where-Object { $_.Extension -in '.dll', '.exe' -and [IO.Path]::GetRelativePath($root, $_.FullName) -notmatch '(^|[\\/])ref[\\/]' -and (Get-FileVersionProperties -Path $_.FullName).CompanyName -match 'Devolutions' } |
        ForEach-Object FullName
}

function Get-AssemblyResources
{
    param([Parameter(Mandatory)] [string] $Path)

    $resources = [ordered]@{}
    $peReader = $null
    $stream = [IO.File]::OpenRead($Path)
    try
    {
        $peReader = [Reflection.PortableExecutable.PEReader]::new($stream)
        if (-not $peReader.HasMetadata) { return $resources }

        $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
        $directory = $peReader.PEHeaders.CorHeader.ResourcesDirectory
        foreach ($handle in $metadata.ManifestResources)
        {
            $resource = $metadata.GetManifestResource($handle)
            if (-not $resource.Implementation.IsNil) { continue }

            $block = $peReader.GetSectionData($directory.RelativeVirtualAddress + [int]$resource.Offset).GetReader()
            $length = $block.ReadInt32()
            $resources[$metadata.GetString($resource.Name)] = $block.ReadBytes($length)
        }
    }
    finally
    {
        if ($peReader) { $peReader.Dispose() }
        $stream.Dispose()
    }

    return $resources
}

function Expand-AvaloniaResources
{
    param(
        [Parameter(Mandatory)] [Collections.Specialized.OrderedDictionary] $Resources,
        [Parameter(Mandatory)] [string] $AssemblyName,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [Collections.Generic.List[object]] $Issues
    )

    # One manifest resource holds all AvaloniaResource files: an index (version, count, path/offset/size), then data
    $bytes = $Resources[$script:AvaloniaResourcesName]
    try
    {
        $reader = [IO.BinaryReader]::new([IO.MemoryStream]::new($bytes))
        $indexLength = $reader.ReadInt32()
        $version = $reader.ReadInt32()
        if ($version -ne 2) { throw "index version $version, expected 2" }

        $count = $reader.ReadInt32()
        $files = [ordered]@{}
        for ($i = 0; $i -lt $count; $i++)
        {
            $path = $reader.ReadString()
            $offset = $reader.ReadInt32()
            $size = $reader.ReadInt32()
            $files["$script:AvaloniaResourcesName$path"] = [ArraySegment[byte]]::new($bytes, 4 + $indexLength + $offset, $size).ToArray()
        }
    }
    catch
    {
        $Issues.Add([pscustomobject]@{ Kind = 'UnreadableAvaloniaResources'; Id = $AssemblyName; Ecosystem = 'embedded'; Detail = "Could not read the Avalonia resources of $AssemblyName ($($_.Exception.Message)): files inside were not checked" })
        return
    }

    $Resources.Remove($script:AvaloniaResourcesName)
    foreach ($file in $files.GetEnumerator()) { $Resources[$file.Key] = $file.Value }
}

function ConvertFrom-ResourceText
{
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [byte[]] $Bytes)

    $reader = [IO.StreamReader]::new([IO.MemoryStream]::new($Bytes), [Text.Encoding]::UTF8, $true)
    try { return $reader.ReadToEnd() -replace "`r`n", "`n" } finally { $reader.Dispose() }
}

function ConvertFrom-NoticeBlock
{
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Text,
        [Parameter(Mandatory)] [string] $Path,
        [string] $Ecosystem = 'embedded',
        [string] $Origin = 'sidecar'
    )

    # Without a text, the tool resolves it like a package's: the repository LICENSE at Commit, else the SPDX text.
    $header = '(?s)^\s*Component[ \t]*:[ \t]*(?<component>[^\n]+?)[ \t]*\nSource[ \t]*:[ \t]*(?<source>[^\n]*?)[ \t]*\nLicense[ \t]*:[ \t]*(?<license>[^\n]+?)[ \t]*' +
        '(?:\nCopyright[ \t]*:[ \t]*(?<copyright>[^\n]+?)[ \t]*)?(?:\nCommit[ \t]*:[ \t]*(?<commit>[^\n]+?)[ \t]*)?(?:\n\n(?<text>.*)|\n*)$'
    if ($Text -notmatch $header)
    {
        return $null
    }

    $block = $Matches
    $name = $block.component
    $version = ''
    if ($name -match '^(?<name>.+?)\s+(?<version>v?\d\S*|unversioned)$')
    {
        $name = $Matches.name
        $version = $Matches.version
    }

    $licenseText = ([string]$block.text).Trim()
    [pscustomobject]@{
        Ecosystem         = $Ecosystem
        Name              = $name
        Version           = $version
        License           = $block.license
        Source            = $block.source
        Website           = ''
        Copyright         = [string]$block.copyright
        LicenseText       = $licenseText
        LicenseTextOrigin = $(if ($licenseText) { $Origin } else { 'none' })
        RepositoryCommit  = [string]$block.commit
        Path              = $Path
    }
}

function Get-EmbeddedComponents
{
    param(
        [Parameter(Mandatory)] [string] $Directory,
        [Parameter(Mandatory)] [hashtable] $AssetList,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [Collections.Generic.List[object]] $Issues
    )

    $include = @($AssetList.include)
    $exclude = @($AssetList.exclude)
    # Zips are always bundles; other bundled files (a browserify script) are named in the asset list.
    $bundles = @('*.zip') + @($AssetList.bundles)

    foreach ($assemblyPath in @(Get-OwnAssemblies -Directory $Directory))
    {
        $assemblyName = Split-Path $assemblyPath -Leaf
        $resources = Get-AssemblyResources -Path $assemblyPath
        if ($resources.Contains($script:AvaloniaResourcesName))
        {
            Expand-AvaloniaResources -Resources $resources -AssemblyName $assemblyName -Issues $Issues
        }

        $names = @($resources.Keys)
        $sidecars = @($names | Where-Object { $_ -like "*$script:SidecarSuffix" })
        $bundleLists = @($names | Where-Object { $_ -like "*$script:BundleListSuffix" })

        # A sidecar describes the longest resource it extends: x.js.map.LICENSE.txt is x.js.map's, not x.js's
        $owners = @{}
        foreach ($sidecar in $sidecars)
        {
            $owner = $names | Where-Object { $_ -notin $sidecars -and $sidecar.StartsWith("$_.", [StringComparison]::OrdinalIgnoreCase) } |
                Sort-Object Length -Descending | Select-Object -First 1
            if ($owner)
            {
                $owners[$sidecar] = $owner
            }
            else
            {
                $Issues.Add([pscustomobject]@{ Kind = 'OrphanSidecar'; Id = $sidecar; Ecosystem = 'embedded'; Detail = "$assemblyName has no resource this sidecar describes" })
            }
        }

        $assets = @($names | Where-Object {
            $name = $_
            $name -notin $sidecars -and $name -notin $bundleLists -and -not ($exclude | Where-Object { $name -like $_ }) -and (
                ($include | Where-Object { $name -like $_ }) -or ($owners.Values -contains $name))
        })

        foreach ($asset in $assets)
        {
            $path = "$assemblyName!$asset"
            $attributed = $false

            foreach ($sidecar in @($sidecars | Where-Object { $owners[$_] -eq $asset }))
            {
                $component = ConvertFrom-NoticeBlock -Text (ConvertFrom-ResourceText -Bytes $resources[$sidecar]) -Path $path
                if ($component)
                {
                    $attributed = $true
                    $component
                }
                else
                {
                    $Issues.Add([pscustomobject]@{ Kind = 'InvalidSidecar'; Id = $sidecar; Ecosystem = 'embedded'; Detail = "$assemblyName!$sidecar does not start with the Component / Source / License header" })
                }
            }

            $isBundle = [bool]($bundles | Where-Object { $asset -like $_ })
            if ($isBundle)
            {
                foreach ($component in @(Get-BundleComponents -Asset $asset -Resources $resources -Path $path -Issues $Issues))
                {
                    $attributed = $true
                    $component
                }
            }

            # No block for an asset nobody described: an empty one would reach the notices and the About list
            if (-not $attributed)
            {
                $Issues.Add([pscustomobject]@{ Kind = 'UnattributedAsset'; Id = $asset; Ecosystem = 'embedded'; Detail = "$path has no $script:SidecarSuffix sidecar$(if ($isBundle) { " and no 3rdpartylicenses.txt, so it gets no block" })" })
            }
        }
    }
}

function Get-BundleComponents
{
    param(
        [Parameter(Mandatory)] [string] $Asset,
        [Parameter(Mandatory)] [Collections.Specialized.OrderedDictionary] $Resources,
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [Collections.Generic.List[object]] $Issues
    )

    $lists = [Collections.Generic.List[string]]::new()
    if ($Asset -like '*.zip')
    {
        try
        {
            $zip = [IO.Compression.ZipArchive]::new([IO.MemoryStream]::new($Resources[$Asset]), [IO.Compression.ZipArchiveMode]::Read)
        }
        catch
        {
            $zip = $null
            $Issues.Add([pscustomobject]@{ Kind = 'UnreadableBundle'; Id = $Asset; Ecosystem = 'embedded'; Detail = "Could not read $Path as a zip ($($_.Exception.Message)): its 3rdpartylicenses.txt was not checked" })
        }

        if ($zip)
        {
            try
            {
                foreach ($entry in @($zip.Entries | Where-Object Name -eq '3rdpartylicenses.txt'))
                {
                    $reader = [IO.StreamReader]::new($entry.Open())
                    try { $lists.Add($reader.ReadToEnd()) } finally { $reader.Dispose() }
                }
            }
            catch
            {
                $Issues.Add([pscustomobject]@{ Kind = 'UnreadableBundle'; Id = $Asset; Ecosystem = 'embedded'; Detail = "Could not read the entries of $Path ($($_.Exception.Message)): its 3rdpartylicenses.txt was not checked" })
            }
            finally
            {
                $zip.Dispose()
            }
        }
    }

    $stamped = $Resources["$Asset$script:BundleListSuffix"]
    if ($stamped)
    {
        $text = ConvertFrom-ResourceText -Bytes $stamped
        $firstLine, $rest = $text -split "`n", 2
        $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Resources[$Asset])).ToLowerInvariant()
        if ($firstLine.Trim() -ne "sha256:$actual")
        {
            $Issues.Add([pscustomobject]@{ Kind = 'BundleStampMismatch'; Id = $Asset; Ecosystem = 'embedded'; Detail = "$Path changed since its license list was made (stamp '$($firstLine.Trim())', file sha256:$actual): regenerate the list" })
        }

        $lists.Add([string]$rest)
    }

    foreach ($list in $lists)
    {
        foreach ($component in Get-AngularComponents -Text $list)
        {
            $component | Add-Member -NotePropertyName Copyright -NotePropertyValue ''
            $component | Add-Member -NotePropertyName LicenseTextOrigin -NotePropertyValue ($(if ($component.LicenseText) { 'package' } else { 'none' }))
            $component | Add-Member -NotePropertyName Path -NotePropertyValue $Path
            $component
        }
    }
}
