function Get-ComponentInventory
{
    param([AllowEmptyCollection()] [object[]] $Components)

    foreach ($component in $Components | Where-Object { $_ } | Sort-Object Ecosystem, Name, Version -Unique)
    {
        $entry = [ordered]@{
            name         = [string]$component.Name
            version      = [string]$component.Version
            ecosystem    = [string]$component.Ecosystem
            license      = [string]$component.License
            source       = [string](Get-ComponentPage -Component $component)
            isRuntime    = [bool]$component.IsRuntime
            isFirstParty = [bool]$component.IsFirstParty
        }
        # Only when false, so an inventory with no unshipped entry (the notices JSON a merging run reads) keeps its exact shape
        if ($component.IsShipped -eq $false) { $entry.isShipped = $false }
        [pscustomobject]$entry
    }
}

# What CycloneDX for .NET wrote for a package and the inventory does not hold: the SHA-512 of its .nupkg, as the NuGet
# cache records it, and its .nuspec's author, description, copyright and repository
function Get-SbomPackageDetails
{
    param(
        [Parameter(Mandatory)] [string] $PackagesRoot,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Version
    )

    $id = $Name.ToLowerInvariant()
    $folderVersion = $Version.ToLowerInvariant()
    $folder = Join-Path $PackagesRoot $id $folderVersion
    $details = [ordered]@{}

    # A missing, empty or damaged file (anything but a 64-byte SHA-512) leaves the hash out instead of stopping the run
    $hashFile = Join-Path $folder "$id.$folderVersion.nupkg.sha512"
    $bytes = try { [Convert]::FromBase64String(([string](Get-Content -LiteralPath $hashFile -Raw -ErrorAction Stop)).Trim()) } catch { $null }
    if ($bytes.Length -eq 64) { $details.hashes = @([ordered]@{ alg = 'SHA-512'; content = [Convert]::ToHexString($bytes) }) }

    $nuspec = Get-ChildItem -LiteralPath $folder -Filter '*.nuspec' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($nuspec)
    {
        [xml]$document = Get-Content -LiteralPath $nuspec.FullName -Raw
        $node = $document.SelectSingleNode("//*[local-name()='metadata']")
        foreach ($field in @{ author = 'authors'; description = 'description'; copyright = 'copyright' }.GetEnumerator())
        {
            $value = ([string]$node.SelectSingleNode("*[local-name()='$($field.Value)']").InnerText).Trim()
            if ($value) { $details[$field.Key] = $value }
        }

        $repository = $node.SelectSingleNode("*[local-name()='repository']")
        if ($repository -and $repository.GetAttribute('url') -match '^https?://') { $details.vcs = $repository.GetAttribute('url') }
    }

    $details
}

# deps.json's graph between the components the SBOM lists: a package by name and version, one of our projects by name,
# since the inventory carries its file version instead. The app (the root) depends on the project each deps.json is for.
function Get-SbomDependencies
{
    param(
        [AllowEmptyCollection()] [object[]] $Entries,
        [AllowEmptyCollection()] [object[]] $Deps,
        [Parameter(Mandatory)] [string] $RootRef
    )

    $refs = [hashtable]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Entries)
    {
        if ($entry.purl -like 'pkg:nuget/*') { $refs["package|$($entry.name)|$($entry.version)"] = $entry.'bom-ref' }
        elseif ($entry.isProject) { $refs["project|$($entry.name)"] = $entry.'bom-ref' }
    }

    # A library the SBOM does not list (a meta-package, or one -ShippedOnly left out) links through to what it brings in
    $find = {
        param($depsFile, $key, $seen)
        $name, $version = $key -split '/', 2
        $ref = if ($depsFile.Libraries[$key].type -eq 'project') { $refs["project|$name"] } else { $refs["package|$name|$version"] }
        if ($ref -or -not $seen) { return $ref }

        $children = $depsFile.Target[$key].dependencies
        if ($children -and $seen.Add($key))
        {
            foreach ($child in $children.GetEnumerator()) { & $find $depsFile "$($child.Key)/$($child.Value)" $seen }
        }
    }

    $roots = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    $edges = [hashtable]::new([StringComparer]::Ordinal)
    foreach ($depsFile in $Deps)
    {
        $app = $refs["project|$((Split-Path $depsFile.Path -Leaf) -replace '\.deps\.json$', '')"]
        if ($app) { [void]$roots.Add($app) }

        foreach ($library in $depsFile.Target.GetEnumerator())
        {
            $from = & $find $depsFile $library.Key
            if (-not $from -or -not $library.Value.dependencies) { continue }

            if (-not $edges.ContainsKey($from)) { $edges[$from] = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal) }
            foreach ($dependency in $library.Value.dependencies.GetEnumerator())
            {
                foreach ($to in @(& $find $depsFile "$($dependency.Key)/$($dependency.Value)" ([Collections.Generic.HashSet[string]]::new())))
                {
                    if ($to -and $to -ne $from) { [void]$edges[$from].Add($to) }
                }
            }
        }
    }

    $root = [ordered]@{ ref = $RootRef }
    if ($roots.Count) { $root.dependsOn = @($roots) }
    $root

    foreach ($entry in $Entries)
    {
        $item = [ordered]@{ ref = $entry.'bom-ref' }
        if ($edges.ContainsKey($entry.'bom-ref') -and $edges[$entry.'bom-ref'].Count) { $item.dependsOn = @($edges[$entry.'bom-ref']) }
        $item
    }
}

function ConvertTo-CycloneDxJson
{
    param(
        [Parameter(Mandatory)] [string] $AppName,
        [string] $AppVersion,
        [AllowEmptyCollection()] [object[]] $Components,
        # -SbomDetails: the fields CycloneDX for .NET wrote, read from the NuGet cache and deps.json
        [switch] $Details,
        [string] $PackagesRoot,
        [AllowEmptyCollection()] [object[]] $Deps = @()
    )

    $unhashed = [Collections.Generic.List[string]]::new()
    $document = [ordered]@{
        '$schema'   = 'http://cyclonedx.org/schema/bom-1.5.schema.json'
        bomFormat   = 'CycloneDX'
        specVersion = '1.5'
        version     = 1
        metadata    = @{
            component = @{ type = 'application'; name = $AppName; version = $AppVersion }
        }
        components = @(foreach ($component in Get-ComponentInventory -Components $Components)
        {
            $kind = if (-not $component.isFirstParty -and $component.ecosystem -in 'nuget', 'npm') { $component.ecosystem } else { 'generic' }
            $name = (($component.name -split '/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
            $purl = "pkg:$kind/$name$(if ($component.version) { '@' + [Uri]::EscapeDataString($component.version) })"
            $entry = [ordered]@{
                type      = 'library'
                'bom-ref' = $purl
                name      = $component.name
                version   = $component.version
                purl      = $purl
            }
            if ($component.license)
            {
                $entry.licenses = @(@{ license = @{ name = $component.license } })
            }
            if ($component.source -match '^https?://')
            {
                $entry.externalReferences = @(@{ type = 'website'; url = $component.source })
            }
            if ($component.isShipped -eq $false)
            {
                $entry.scope = 'required'
                $entry.properties = @(@{ name = 'devolutions:shipped'; value = 'false' })
            }
            # A .NET runtime's package folder is not named after it, and CycloneDX for .NET never listed one
            if ($Details -and $kind -eq 'nuget' -and -not $component.isRuntime)
            {
                $package = Get-SbomPackageDetails -PackagesRoot $PackagesRoot -Name $component.name -Version $component.version
                if (-not $package.Contains('hashes')) { $unhashed.Add($component.name) }
                foreach ($field in 'author', 'description', 'copyright', 'hashes')
                {
                    if ($package.Contains($field)) { $entry[$field] = $package[$field] }
                }
                if ($package.vcs) { $entry.externalReferences = @($entry.externalReferences | Where-Object { $_ }) + @(@{ type = 'vcs'; url = $package.vcs }) }
            }
            # Read by the dependency graph only, never written
            if ($Details -and $component.isFirstParty -and $component.ecosystem -eq 'nuget') { $entry.isProject = $true }
            $entry
        })
    }

    if ($Details)
    {
        # Not a purl, so it never matches a component's
        $rootRef = "$AppName@$AppVersion"
        $document.Insert(3, 'serialNumber', "urn:uuid:$([guid]::NewGuid())")
        $document.metadata = [ordered]@{
            timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
            tools     = @{ components = @(@{ type = 'application'; publisher = 'Devolutions'; name = 'ThirdPartyLicenses (DevTools/ThirdPartyLicenses)' }) }
            component = [ordered]@{ type = 'application'; 'bom-ref' = $rootRef; name = $AppName; version = $AppVersion }
        }
        $document.dependencies = @(Get-SbomDependencies -Entries $document.components -Deps $Deps -RootRef $rootRef)
        foreach ($entry in $document.components) { $entry.Remove('isProject') }

        if ($unhashed.Count)
        {
            $named = ($unhashed | Select-Object -First 5) -join ', '
            Write-Warning "-SbomDetails: $($unhashed.Count) NuGet package(s) have no .nupkg.sha512 in $PackagesRoot, so the SBOM gives them no hash: $named$(if ($unhashed.Count -gt 5) { ', ...' })"
        }
    }

    ConvertTo-Json -InputObject $document -Depth 8
}
