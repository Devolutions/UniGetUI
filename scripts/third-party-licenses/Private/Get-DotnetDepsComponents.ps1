$script:ShippedAssetKinds = 'runtime', 'native', 'resources', 'runtimeTargets'
$script:ShippedFileVersions = @{}

function Read-DepsFile
{
    param([Parameter(Mandatory)] [string] $DepsFile)

    $deps = Get-Content -LiteralPath $DepsFile -Raw | ConvertFrom-Json -AsHashtable
    $runtimeTarget = $deps.runtimeTarget.name
    [pscustomobject]@{
        Path      = $DepsFile
        Libraries = $deps.libraries
        Target    = $deps.targets[$runtimeTarget]
        Rid       = if ($runtimeTarget -match '/(?<rid>.+)$') { $Matches.rid } else { '' }
    }
}

function Get-DotnetDepsComponents
{
    param(
        [Parameter(Mandatory)] [pscustomobject] $Deps,
        [switch] $IncludeProjects
    )

    foreach ($library in $Deps.Libraries.GetEnumerator())
    {
        if ($library.Value.type -notin 'package', 'runtimepack' -and -not ($IncludeProjects -and $library.Value.type -eq 'project'))
        {
            continue
        }

        $assets = $Deps.Target[$library.Key]
        if (-not $assets -or -not ($assets.Keys | Where-Object { $_ -in $script:ShippedAssetKinds }))
        {
            continue
        }

        $id, $version = $library.Key -split '/', 2
        $isRuntime = $library.Value.type -eq 'runtimepack'
        $id = $id -replace '^runtimepack\.', ''
        $packagePath = if ($library.Value.path) { $library.Value.path } else { "$($id.ToLowerInvariant())/$($version.ToLowerInvariant())" }

        # The version deps.json records for each file tells, when two apps published into one folder list the package at
        # different versions, which of them is on disk. 0.0.0.0 (often a native file) tells nothing.
        $assetFileVersions = @{}
        foreach ($kind in $script:ShippedAssetKinds)
        {
            if (-not $assets[$kind]) { continue }

            foreach ($asset in $assets[$kind].GetEnumerator())
            {
                $fileVersion = $null
                if ($asset.Key -and [version]::TryParse([string]$asset.Value.fileVersion, [ref]$fileVersion) -and $fileVersion -ne [version]'0.0.0.0')
                {
                    $assetFileVersions[(Split-Path $asset.Key -Leaf)] = $fileVersion
                }
            }
        }

        [pscustomobject]@{
            Ecosystem         = 'nuget'
            Name              = if ($isRuntime) { $id -replace '\.Runtime\.[^.]+$', '' } else { $id }
            Version           = $version
            PackagePath       = $packagePath
            IsRuntime         = $isRuntime
            IsFirstParty      = $library.Value.type -eq 'project'
            Assets            = @(foreach ($kind in $script:ShippedAssetKinds) { @($assets[$kind].Keys | Where-Object { $_ } | ForEach-Object { Split-Path $_ -Leaf }) })
            RuntimeTargets    = @($assets['runtimeTargets'].Keys | Where-Object { $_ })
            NamedAssets       = @(foreach ($kind in $script:ShippedAssetKinds -ne 'runtimeTargets') { @($assets[$kind].Keys | Where-Object { $_ } | ForEach-Object { Split-Path $_ -Leaf }) })
            AssetFileVersions = $assetFileVersions
        }
    }
}

# A runtimeTargets file keeps its runtimes/<rid>/ path in the output: one whose folder the build removed is not shipped,
# even when another runtime keeps a file of the same name. The other assets can be anywhere, so they match by name.
function Test-ComponentShipped
{
    param(
        [Parameter(Mandatory)] [pscustomobject] $Component,
        [Parameter(Mandatory)] [Collections.Generic.Dictionary[string, Collections.Generic.List[string]]] $Shipped,
        [Parameter(Mandatory)] [string] $Directory
    )

    foreach ($path in @($Component.RuntimeTargets))
    {
        if (Test-Path -LiteralPath (Join-Path $Directory $path) -PathType Leaf) { return $true }
    }

    return @($Component.NamedAssets | Where-Object { $Shipped.ContainsKey($_) }).Count -gt 0
}

# Hosts and modules can ship different versions of the same file name in separate directories.
function Get-ShippedFiles
{
    param([Parameter(Mandatory)] [string] $Directory)

    $files = [Collections.Generic.Dictionary[string, Collections.Generic.List[string]]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File)
    {
        if (-not $files.ContainsKey($file.Name)) { $files[$file.Name] = [Collections.Generic.List[string]]::new() }
        $files[$file.Name].Add($file.FullName)
    }
    return , $files
}

# $true when one of the component's files on disk has the version deps.json records for it, $false when every file
# that could be compared has another one, $null when none could be (no version recorded, or none readable on disk).
function Test-ShippedVersion
{
    param(
        [Parameter(Mandatory)] [pscustomobject] $Component,
        [Parameter(Mandatory)] [Collections.Generic.Dictionary[string, Collections.Generic.List[string]]] $Shipped
    )

    $compared = $false
    foreach ($asset in $Component.AssetFileVersions.GetEnumerator())
    {
        $paths = $null
        if (-not $Shipped.TryGetValue($asset.Key, [ref]$paths))
        {
            continue
        }

        foreach ($path in $paths)
        {
            if (-not $script:ShippedFileVersions.ContainsKey($path))
            {
                $info = Get-FileVersionProperties -Path $path
                $script:ShippedFileVersions[$path] = [version]::new($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart)
            }

            $onDisk = $script:ShippedFileVersions[$path]
            if ($onDisk -eq [version]'0.0.0.0')
            {
                continue
            }

            if ($onDisk -eq $asset.Value)
            {
                return $true
            }

            $compared = $true
        }
    }

    return $(if ($compared) { $false } else { $null })
}

function Test-DepsShipsRuntime
{
    param([Parameter(Mandatory)] [pscustomobject] $Deps)

    return [bool]($Deps.Libraries.Values | Where-Object { $_.type -eq 'runtimepack' })
}

function Get-SharedRuntimeComponents
{
    param(
        [Parameter(Mandatory)] [pscustomobject] $Deps,
        [Parameter(Mandatory)] [string] $PackagesRoot,
        [string] $RuntimeConfigPath
    )

    $runtimeConfig = if ($RuntimeConfigPath) { $RuntimeConfigPath } else { $Deps.Path -replace '\.deps\.json$', '.runtimeconfig.json' }
    if (-not (Test-Path -LiteralPath $runtimeConfig))
    {
        throw "-IncludeRuntime needs '$runtimeConfig' to know which .NET frameworks the app runs on."
    }

    $options = (Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json -AsHashtable).runtimeOptions
    $frameworks = @($options.frameworks) + @($options.framework) | Where-Object { $_ }
    $rid = $Deps.Rid

    foreach ($framework in $frameworks)
    {
        $version = [string]$framework.version
        $packs = @(Get-ChildItem -LiteralPath $PackagesRoot -Directory -Filter "$($framework.name).runtime.*" -ErrorAction SilentlyContinue)
        $preferred = @($packs | Where-Object { -not $rid -or $_.Name -ieq "$($framework.name).runtime.$rid" })
        if ($preferred) { $packs = $preferred }

        $majorMinor = ($version -split '\.')[0..1] -join '.'
        $packVersion = $packs | Get-ChildItem -Directory | Where-Object { $_.Name -like "$majorMinor.*" } |
            Sort-Object { [version]($_.Name -split '-')[0] } | Select-Object -Last 1

        [pscustomobject]@{
            Ecosystem   = 'nuget'
            Name        = $framework.name
            Version     = $version
            PackagePath = if ($packVersion) { "$($packVersion.Parent.Name)/$($packVersion.Name)" } else { $null }
            IsRuntime   = $true
        }
    }
}

function Get-AppVersion
{
    param(
        [Parameter(Mandatory)] [string] $DepsFile,
        [string] $BinariesPath
    )

    $appName = (Split-Path $DepsFile -Leaf) -replace '\.deps\.json$', ''
    $folders = @((Split-Path -Parent $DepsFile), $BinariesPath) | Where-Object { $_ }
    $candidates = foreach ($folder in $folders) { foreach ($extension in '.dll', '.exe', '') { Join-Path $folder "$appName$extension" } }
    $assembly = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $assembly)
    {
        return ''
    }

    $info = Get-FileVersionProperties -Path $assembly
    $version = if ($info.ProductVersion) { $info.ProductVersion } else { $info.FileVersion }
    return ([string]$version -split '\+')[0]
}

function Get-UnlistedBinaries
{
    param(
        [Parameter(Mandatory)] [pscustomobject[]] $Deps,
        [Parameter(Mandatory)] [string] $Directory,
        [string[]] $Excludes = @(),
        [string[]] $ExcludeExceptions = @(),
        [switch] $IncludeOwn
    )

    # Several apps can be published into one folder: a binary listed by any of their deps.json is not unlisted.
    $known = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    # The .NET reader never reads a reference entry, so with -IncludeOwn a file only a reference entry lists is scanned too
    $knownOutsideReferences = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $appNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($depsFile in $Deps)
    {
        [void]$appNames.Add(((Split-Path $depsFile.Path -Leaf) -replace '\.deps\.json$', ''))
        foreach ($library in $depsFile.Target.GetEnumerator())
        {
            $isReference = $depsFile.Libraries -and $depsFile.Libraries[$library.Key].type -eq 'reference'
            foreach ($kind in $script:ShippedAssetKinds)
            {
                foreach ($asset in @($library.Value[$kind].Keys | Where-Object { $_ }))
                {
                    [void]$known.Add((Split-Path $asset -Leaf))
                    if (-not $isReference) { [void]$knownOutsideReferences.Add((Split-Path $asset -Leaf)) }
                }
            }
        }
    }

    $scanRoot = Get-Item -LiteralPath $Directory
    $binaries = Get-ChildItem -LiteralPath $scanRoot.FullName -Recurse -File |
        Where-Object { $_.Extension -in '.dll', '.so', '.dylib', '.exe' -or ($_.Extension -eq '' -and $appNames.Contains($_.Name)) }

    foreach ($binary in $binaries)
    {
        $relative = [IO.Path]::GetRelativePath($scanRoot.FullName, $binary.FullName) -replace '\\', '/'
        $listed = $known.Contains($binary.Name)
        if ($relative -like 'ref/*' -or $appNames.Contains($binary.BaseName) -or $knownOutsideReferences.Contains($binary.Name) -or ($listed -and -not $IncludeOwn))
        {
            continue
        }

        if ($binary.Name -match '^(?<assembly>.+)\.XmlSerializers\.dll$' -and $known.Contains("$($Matches.assembly).dll"))
        {
            continue
        }

        $info = Get-FileVersionProperties -Path $binary.FullName
        $version = [string]$(if ($info.ProductVersion) { $info.ProductVersion } else { $info.FileVersion })
        $isOurs = $info.CompanyName -match 'Devolutions'
        if ($isOurs -and $IncludeOwn)
        {
            [pscustomobject]@{ Ecosystem = 'binary'; Name = $binary.Name; Version = ($version -split '\+')[0]; Path = $relative; IsFirstParty = $true }
            continue
        }

        # A file only a reference entry lists reaches here with -IncludeOwn alone: a third-party one stays skipped
        if ($listed -or $isOurs -or $info.ProductName -like 'Microsoft® .NET*')
        {
            continue
        }

        if ($ExcludeExceptions -notcontains $binary.BaseName -and ($Excludes | Where-Object { $binary.BaseName -like $_ }))
        {
            continue
        }

        [pscustomobject]@{
            Ecosystem = 'binary'
            Name      = $binary.Name
            Version   = $version
            Copyright = [string]$info.LegalCopyright
            Company   = [string]$info.CompanyName
            Path      = $relative
        }
    }
}
