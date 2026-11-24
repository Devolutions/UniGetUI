$script:NoticeMarkerStart = '// THIRD-PARTY-NOTICE'
$script:NoticeMarkerEnd = '// END THIRD-PARTY-NOTICE'
$script:NoticeMarkerSearchLines = 200

function Get-PdbDocuments
{
    param([Parameter(Mandatory)] [string] $PdbPath)

    $stream = [IO.File]::OpenRead($PdbPath)
    try
    {
        $provider = [Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($stream)
        try
        {
            $reader = $provider.GetMetadataReader()
            foreach ($handle in $reader.Documents)
            {
                $reader.GetString($reader.GetDocument($handle).Name)
            }
        }
        finally
        {
            $provider.Dispose()
        }
    }
    catch [BadImageFormatException]
    {
        # Windows (full) PDBs are not portable; they carry no document list we can read here.
    }
    finally
    {
        $stream.Dispose()
    }
}

function Get-SourceNotice
{
    param([Parameter(Mandatory)] [string] $SourcePath)

    # Runs for every source file the PDBs list: only the top of the file is read, and no script block runs per line
    $top = (Get-Content -LiteralPath $SourcePath -TotalCount $script:NoticeMarkerSearchLines -ReadCount 0) -join "`n"
    $markers = [regex]::Matches($top, "(?m)^[ \t]*$([regex]::Escape($script:NoticeMarkerStart))[ \t]*$")
    if (-not $markers.Count) { return }

    # One result per block; a block that another marker or the end of the file interrupts is not closed
    $lines = @(Get-Content -LiteralPath $SourcePath)
    foreach ($marker in $markers)
    {
        $markerLine = $top.Substring(0, $marker.Index).Split("`n").Count
        $body = [Collections.Generic.List[string]]::new()
        $closed = $false
        for ($index = $markerLine; $index -lt $lines.Count; $index++)
        {
            $trimmed = $lines[$index].Trim()
            if ($trimmed -eq $script:NoticeMarkerEnd) { $closed = $true; break }
            if ($trimmed -eq $script:NoticeMarkerStart) { break }
            $body.Add(($trimmed -replace '^//\s?', ''))
        }

        [pscustomobject]@{ Line = $markerLine; Text = $body -join "`n"; Closed = $closed }
    }
}

function Get-SourceComponents
{
    param(
        [Parameter(Mandatory)] [string] $Directory,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $DepsComponents,
        [Parameter(Mandatory)] [string] $PackagesRoot,
        [string[]] $Excludes = @(),
        [string[]] $ExcludeExceptions = @(),
        [Parameter(Mandatory)] [AllowEmptyCollection()] [Collections.Generic.List[object]] $Issues
    )

    $packagesPrefix = ($PackagesRoot -replace '\\', '/').TrimEnd('/') + '/'
    $listed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    # What the deps reader reports, not all deps.json lists: it skips an entry without shipped files
    foreach ($component in $DepsComponents) { [void]$listed.Add("$($component.Name)/$($component.Version)") }

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $missing = [Collections.Generic.List[string]]::new()
    $withoutPdb = [Collections.Generic.List[string]]::new()

    foreach ($assemblyPath in @(Get-OwnAssemblies -Directory $Directory))
    {
        $pdbPath = [IO.Path]::ChangeExtension($assemblyPath, '.pdb')
        if (-not (Test-Path -LiteralPath $pdbPath))
        {
            $withoutPdb.Add((Split-Path $assemblyPath -Leaf))
            continue
        }

        $assemblyName = Split-Path $assemblyPath -Leaf
        foreach ($document in @(Get-PdbDocuments -PdbPath $pdbPath))
        {
            if (-not $seen.Add($document)) { continue }
            $normalized = $document -replace '\\', '/'

            if ($normalized.StartsWith($packagesPrefix, [StringComparison]::OrdinalIgnoreCase))
            {
                # Source-only packages ship code under contentFiles/ or build/; analyzer code only runs at compile time
                if ($normalized.Substring($packagesPrefix.Length) -notmatch '^(?<id>[^/]+)/(?<version>[^/]+)/(?!analyzers/)') { continue }

                $id = $Matches.id
                $version = $Matches.version
                $excluded = $ExcludeExceptions -notcontains $id -and ($Excludes | Where-Object { $id -like $_ })
                if ($listed.Contains("$id/$version") -or $excluded -or -not $seen.Add("package:$id/$version")) { continue }

                # The cache folder is lower-case; the package id keeps its casing in the nuspec.
                $packagePath = "$id/$version".ToLowerInvariant()
                $nuspec = Get-ChildItem -LiteralPath (Join-Path $PackagesRoot $packagePath) -Filter '*.nuspec' -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($nuspec)
                {
                    $nuspecId = ([xml](Get-Content -LiteralPath $nuspec.FullName -Raw)).package.metadata.id
                    if ($nuspecId) { $id = [string]$nuspecId }
                }

                [pscustomobject]@{
                    Ecosystem   = 'nuget'
                    Name        = $id
                    Version     = $version
                    PackagePath = $packagePath
                    IsRuntime   = $false
                }
                continue
            }

            # Files under obj/ are generated (source generators, AssemblyInfo), often never written to disk.
            if ($normalized -notmatch '\.(cs|vb|fs)$' -or $normalized -match '/obj/') { continue }
            if (-not (Test-Path -LiteralPath $document))
            {
                $missing.Add($document)
                continue
            }

            foreach ($notice in @(Get-SourceNotice -SourcePath $document))
            {
                $component = if ($notice.Closed) { ConvertFrom-NoticeBlock -Text $notice.Text -Path "$assemblyName!$(Split-Path $document -Leaf)" -Ecosystem 'source' -Origin 'source' }
                if ($component)
                {
                    $component
                }
                elseif ($notice.Closed)
                {
                    $Issues.Add([pscustomobject]@{ Kind = 'InvalidSourceNotice'; Id = (Split-Path $document -Leaf); Ecosystem = 'source'; Detail = "$document has a third-party notice marker without the Component / Source / License header" })
                }
                else
                {
                    $Issues.Add([pscustomobject]@{ Kind = 'InvalidSourceNotice'; Id = (Split-Path $document -Leaf); Ecosystem = 'source'; Detail = "$document has a third-party notice marker on line $($notice.Line) that $script:NoticeMarkerEnd never closes" })
                }
            }
        }
    }

    if ($withoutPdb.Count)
    {
        $Issues.Add([pscustomobject]@{ Kind = 'PdbNotFound'; Id = "$($withoutPdb.Count) assemblies"; Ecosystem = 'source'; Detail = "No .pdb beside $($withoutPdb -join ', '): copied code in them was not checked (an assembly from a package usually ships without one; an assembly of ours that carries copied code needs its .pdb copied beside it)." })
    }

    if ($missing.Count)
    {
        $Issues.Add([pscustomobject]@{ Kind = 'SourceNotFound'; Id = "$($missing.Count) files"; Ecosystem = 'source'; Detail = "PDB source files are not on this machine (e.g. $($missing[0])): copied code in them was not checked. Run on the machine that built the binaries." })
    }
}
