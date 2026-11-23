#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]] $ArtifactPath,

    [Parameter(Mandatory)]
    [string] $OutputPath,

    [string] $AppName,

    [string] $AppVersion,

    [string] $TemplatePath,

    [switch] $IncludeRuntime,

    [string] $BinariesPath,

    [string] $WebAssemblyPath,

    [switch] $ShippedOnly,

    [string[]] $ManualComponentsPath,

    [switch] $Json,

    [switch] $Sbom,

    [string] $SbomPath,

    [switch] $SbomRuntime,

    [switch] $SbomOwnBinaries,

    [switch] $SbomDetails,

    [switch] $SeparateVersions,

    [string] $EmbeddedAssetsPath,

    [switch] $IncludeCopiedSource,

    [switch] $Signed,

    [switch] $Check,

    [string] $CheckOutputDirectory = $(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }),

    [string] $CachePath = $(if ($env:RUNNER_TEMP) { Join-Path $env:RUNNER_TEMP 'third-party-licenses-cache.json' }),

    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
)

$ErrorActionPreference = 'Stop'

if ($SbomPath -and -not $Sbom) { throw '-SbomPath needs -Sbom.' }
# Without -SbomPath the SBOM list also goes into the notices JSON, which ships: what these two add would reach it
if ($SbomRuntime -and -not $SbomPath) { throw '-SbomRuntime needs -SbomPath.' }
if ($SbomOwnBinaries -and -not $SbomPath) { throw '-SbomOwnBinaries needs -SbomPath.' }
if ($SbomDetails -and -not $Sbom) { throw '-SbomDetails needs -Sbom.' }
# One says the runtime ships, the other that it does not
if ($SbomRuntime -and $IncludeRuntime) { throw '-SbomRuntime and -IncludeRuntime cannot be used together.' }

Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Private') -Filter '*.ps1' | ForEach-Object { . $_.FullName }
Import-RemoteCache -Path $CachePath

function Resolve-RepoPath
{
    param([string] $Path)

    if ([IO.Path]::IsPathRooted($Path)) { return $Path }
    return Join-Path $RepoRoot $Path
}

function Resolve-ToolPath
{
    param([string] $Path)

    @((Join-Path $PSScriptRoot $Path), (Resolve-RepoPath $Path)) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

function Resolve-ArtifactFiles
{
    param([Parameter(Mandatory)] [string] $Pattern)

    $normalized = $Pattern -replace '\\', '/'
    if ($normalized -notmatch '[*?]')
    {
        $path = Resolve-RepoPath $normalized
        if (-not (Test-Path -LiteralPath $path)) { return @() }
        if (Test-Path -LiteralPath $path -PathType Leaf) { return @(Get-Item -LiteralPath $path) }

        return @(
            Get-ChildItem -LiteralPath $path -Filter '*.deps.json' -File
            Get-ChildItem -LiteralPath $path -Filter '3rdpartylicenses.txt' -File -Recurse
        )
    }

    $segments = $normalized -split '/'
    $staticSegments = @($segments | ForEach-Object -Begin { $stop = $false } -Process {
        if ($stop -or $_ -match '[*?]') { $stop = $true } else { $_ }
    })
    $base = Resolve-RepoPath ($staticSegments -join '/')
    if (-not (Test-Path -LiteralPath $base)) { return @() }

    $regex = '^' + (([regex]::Escape($normalized)) -replace '\\\*\\\*/', '(?:.*/)?' -replace '\\\*', '[^/]*' -replace '\\\?', '[^/]') + '$'
    $rootPrefix = if ([IO.Path]::IsPathRooted($normalized)) { '' } else { ($RepoRoot -replace '\\', '/').TrimEnd('/') + '/' }

    $matched = Get-ChildItem -LiteralPath $base -File -Recurse | Where-Object {
        $relative = ($_.FullName -replace '\\', '/')
        if ($rootPrefix) { $relative = $relative.Substring($rootPrefix.Length) }
        $relative -match $regex
    }

    if (@($matched).Count -gt 1)
    {
        $latest = $matched | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        Write-Warning "'$Pattern' matched $(@($matched).Count) files; using the most recent: $($latest.FullName)"
        return @($latest)
    }

    return @($matched)
}

function Test-Excluded
{
    param([string] $Name, [string[]] $Patterns, [string[]] $Exceptions)

    # A binary no package declares is named by its file: its exception names it without the extension, as its pattern does
    if ($Exceptions -contains $Name -or $Exceptions -contains ($Name -replace '\.(dll|exe|so|dylib)$', '')) { return $false }

    foreach ($pattern in $Patterns)
    {
        if ($Name -like $pattern) { return $true }
    }

    return $false
}

function Test-LicenseAllowed
{
    param([string] $License, [string[]] $Allowed)

    $test = {
        param([string] $Expression)

        foreach ($alternative in ($Expression -split '\s+OR\s+'))
        {
            $ids = @($alternative -split '\s+AND\s+' | ForEach-Object { ($_ -split '\s+WITH\s+')[0].Trim() } | Where-Object { $_ })
            if ($ids -and -not ($ids | Where-Object { $_ -ne '<allowed>' -and $_ -notin $Allowed })) { return $true }
        }

        return $false
    }

    $expression = $License
    while ($expression -match '\(([^()]*)\)')
    {
        $expression = $expression.Replace($Matches[0], $(if (& $test $Matches[1]) { ' <allowed> ' } else { ' <denied> ' }))
    }

    return & $test $expression
}

$configDirectory = Join-Path $PSScriptRoot 'config'
$urlMappings = Get-Content -LiteralPath (Join-Path $configDirectory 'license-url-mappings.json') -Raw | ConvertFrom-Json -AsHashtable
if (-not $AppName) { $AppName = 'Application' }
$excludedPackages = (Get-Content -LiteralPath (Join-Path $configDirectory 'excluded-packages.json') -Raw | ConvertFrom-Json -AsHashtable).packages
$globalExcludes = @($excludedPackages.Keys | Where-Object { Select-AppEntry -Value $excludedPackages[$_] -AppName $AppName })
$excludeExceptions = @($globalExcludes | ForEach-Object { Select-AppEntry -Value $excludedPackages[$_] -AppName $AppName } | ForEach-Object { $_.except } | Where-Object { $_ })
$allowedLicenses = @((Get-Content -LiteralPath (Join-Path $configDirectory 'allowed-licenses.json') -Raw | ConvertFrom-Json -AsHashtable).allowed)
$approvedPackages = (Get-Content -LiteralPath (Join-Path $configDirectory 'approved-packages.json') -Raw | ConvertFrom-Json -AsHashtable).packages
$errorKinds = 'MissingVersion', 'MissingLicense', 'MissingLicenseText', 'MissingSource', 'MissingCopyright', 'LicenseNotAllowed', 'ApprovedLicenseChanged', 'ShippedVersionMismatch'
$warningKinds = 'NoticeNotChecked', 'NoticeUnavailable', 'InvalidSidecar', 'InvalidSourceNotice', 'UnreadableBundle', 'UnreadableAvaloniaResources', 'OtherAppNotices',
    'UnusedLicenseText'

if (-not $TemplatePath) { $TemplatePath = 'templates/default.txt' }
$TemplatePath = Resolve-ToolPath $TemplatePath
if (-not $TemplatePath) { throw 'Template not found.' }

$overrides = @{}
$overridesConfig = Get-Content -LiteralPath (Join-Path $configDirectory 'package-overrides.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($entry in @($overridesConfig.packages.GetEnumerator()))
{
    $parts = @(Select-AppEntry -Value $entry.Value -AppName $AppName)
    if (-not $parts) { continue }
    $merged = @{}
    foreach ($part in $parts) { foreach ($field in $part.Keys) { $merged[$field] = $part[$field] } }
    $overrides[$entry.Key.ToLowerInvariant()] = $merged
}

$artifactFiles = @(foreach ($pattern in $ArtifactPath)
{
    $files = Resolve-ArtifactFiles -Pattern $pattern
    if (-not $files) { throw "No build output found for '$pattern'. Run the build/publish first." }
    $files
})

$issues = [Collections.Generic.List[object]]::new()
$packagesRoot = Get-NuGetPackagesRoot
$depsByBinariesDirectory = [ordered]@{}
$shippedByDirectory = @{}
$mergedInventory = [Collections.Generic.List[object]]::new()
$readDeps = [Collections.Generic.List[object]]::new()

$collected = foreach ($file in $artifactFiles)
{
    Write-Host "Reading $($file.FullName)"
    if ($file.Name -like '*.deps.json')
    {
        $deps = Read-DepsFile -DepsFile $file.FullName
        $readDeps.Add($deps)
        $binariesDirectory = if ($BinariesPath) { Resolve-RepoPath $BinariesPath } elseif (($file.FullName -replace '\\', '/') -notmatch '/obj/') { $file.DirectoryName }
        $dotnetComponents = @(Get-DotnetDepsComponents -Deps $deps -IncludeProjects:$Sbom)

        # deps.json lists the whole restore graph: a package trimmed from the output, or left out by an installer that
        # packages a subset of it, is not redistributed and has nothing to attribute.
        if ($ShippedOnly)
        {
            if (-not $binariesDirectory) { throw "-ShippedOnly needs -BinariesPath for '$($file.Name)'." }
            $shippedKey = (Get-Item -LiteralPath $binariesDirectory).FullName
            if (-not $shippedByDirectory.ContainsKey($shippedKey)) { $shippedByDirectory[$shippedKey] = Get-ShippedFiles -Directory $shippedKey }
            $shipped = $shippedByDirectory[$shippedKey]
            $dotnetComponents = @($dotnetComponents | Where-Object { Test-ComponentShipped -Component $_ -Shipped $shipped -Directory $shippedKey })

            # Matched by name, so two apps published into one folder can both claim a file: its version says whose it is
            foreach ($component in $dotnetComponents)
            {
                $component | Add-Member -NotePropertyName ShippedVersionMatch -NotePropertyValue (Test-ShippedVersion -Component $component -Shipped $shipped)
            }
        }

        if ($IncludeRuntime -and -not (Test-DepsShipsRuntime -Deps $deps))
        {
            $dotnetComponents += @(Get-SharedRuntimeComponents -Deps $deps -PackagesRoot $packagesRoot)
        }

        # The frameworks the app runs on but does not ship: SBOM only, so never in the notices. The copy in -BinariesPath
        # is the one the installer packs.
        if ($SbomRuntime -and -not (Test-DepsShipsRuntime -Deps $deps))
        {
            $runtimeConfig = Join-Path ($binariesDirectory ?? $file.DirectoryName) ($file.Name -replace '\.deps\.json$', '.runtimeconfig.json')
            if (Test-Path -LiteralPath $runtimeConfig)
            {
                foreach ($framework in @(Get-SharedRuntimeComponents -Deps $deps -PackagesRoot $packagesRoot -RuntimeConfigPath $runtimeConfig))
                {
                    $metadata = Resolve-DotnetInstallMetadata -Framework $framework.Name
                    $mergedInventory.Add([pscustomobject]@{
                        Ecosystem = 'nuget'; Name = $framework.Name; Version = $framework.Version; License = $metadata.License
                        Source = $metadata.Source; IsRuntime = $true; IsFirstParty = $false; IsShipped = $false
                    })
                }
            }
            else
            {
                Write-Warning "-SbomRuntime: '$runtimeConfig' not found, so the SBOM lists no .NET framework for '$($file.Name)'."
            }
        }

        $sourceComponents = @(if ($IncludeCopiedSource -and $binariesDirectory) { Get-SourceComponents -Directory $binariesDirectory -DepsComponents $dotnetComponents -PackagesRoot $packagesRoot -Excludes $globalExcludes -ExcludeExceptions $excludeExceptions -Issues $issues })
        $dotnetComponents += @($sourceComponents | Where-Object Ecosystem -eq 'nuget')

        foreach ($component in $dotnetComponents)
        {
            $metadata = if ($component.IsFirstParty)
            {
                if ($binariesDirectory)
                {
                    foreach ($asset in $component.Assets)
                    {
                        $assemblyName = [IO.Path]::GetFileNameWithoutExtension($asset)
                        $version = Get-AppVersion -DepsFile (Join-Path $binariesDirectory "$assemblyName.deps.json")
                        if ($version)
                        {
                            $component.Version = $version
                            break
                        }
                    }
                }
                [pscustomobject]@{ Found = $true }
            }
            elseif ($component.PackagePath)
            {
                Resolve-NuGetMetadata -Component $component -PackagesRoot $packagesRoot -UrlMappings $urlMappings
            }
            else
            {
                Resolve-DotnetInstallMetadata -Framework $component.Name
            }

            if (-not $metadata.Found)
            {
                $issues.Add([pscustomobject]@{ Kind = 'MissingPackageMetadata'; Id = $component.Name; Ecosystem = 'nuget'; Detail = "Not in NuGet cache $packagesRoot" })
            }

            [pscustomobject]@{
                Ecosystem           = 'nuget'
                Name                = $component.Name
                Version             = $component.Version
                License             = $metadata.License
                Source              = $metadata.Source
                Website             = $metadata.Website
                Copyright           = $metadata.Copyright
                LicenseText         = $metadata.LicenseText
                LicenseTextOrigin   = $metadata.LicenseTextOrigin
                RepositoryCommit    = $metadata.RepositoryCommit
                PackageNotice       = $metadata.PackageNotice
                IsRuntime           = $component.IsRuntime
                IsFirstParty        = $component.IsFirstParty
                ShippedVersionMatch = $component.ShippedVersionMatch
            }
        }

        if (-not $AppVersion)
        {
            $AppVersion = Get-AppVersion -DepsFile $file.FullName -BinariesPath $(if ($BinariesPath) { Resolve-RepoPath $BinariesPath })
        }

        if ($binariesDirectory)
        {
            $binariesDirectory = (Get-Item -LiteralPath $binariesDirectory).FullName
            if (-not $depsByBinariesDirectory.Contains($binariesDirectory)) { $depsByBinariesDirectory[$binariesDirectory] = [Collections.Generic.List[object]]::new() }
            $depsByBinariesDirectory[$binariesDirectory].Add($deps)
        }
        else
        {
            Write-Warning "'$($file.Name)' comes from obj/ (single-file or AOT publish): pass -BinariesPath <publish dir> to also detect binaries that no package provides."
        }

        $sourceComponents | Where-Object Ecosystem -eq 'source'
    }
    elseif ($file.Name -eq '3rdpartylicenses.txt')
    {
        foreach ($component in Get-AngularComponents -LicensesFile $file.FullName)
        {
            $component | Add-Member -NotePropertyName Copyright -NotePropertyValue ''
            $component | Add-Member -NotePropertyName LicenseTextOrigin -NotePropertyValue ($(if ($component.LicenseText) { 'package' } else { 'none' }))
            $component
        }
    }
    elseif ($file.Name -eq 'staticwebassets.publish.json')
    {
        if (-not $WebAssemblyPath) { throw "-WebAssemblyPath is required for '$($file.FullName)'." }
        Get-WebAssemblyComponents -ManifestPath $file.FullName -BinariesPath (Resolve-RepoPath $WebAssemblyPath) `
            -PackagesRoot $packagesRoot -UrlMappings $urlMappings -IncludeProjects:$Sbom
    }
    elseif ($file.Extension -eq '.psd1')
    {
        Get-PowerShellModuleComponents -Path $file.FullName -UrlMappings $urlMappings
    }
    elseif ($file.Name -eq 'webpack-components.json')
    {
        Get-WebpackComponents -Path $file.FullName
    }
    elseif ($file.Extension -eq '.json')
    {
        $noticesJson = Read-NoticesJson -Path $file.FullName
        if (-not $AppVersion) { $AppVersion = $noticesJson.AppVersion }

        # Its LGPL notices and statements name the app they were written for; a JSON with none merges as is (PSU)
        $namesItsApp = $noticesJson.AppName -and @($noticesJson.Components | Where-Object { $_.Notice.Text -match [regex]::Escape($noticesJson.AppName) })
        if ($namesItsApp -and $noticesJson.AppName -ne $AppName)
        {
            $issues.Add([pscustomobject]@{ Kind = 'OtherAppNotices'; Id = $file.Name; Ecosystem = 'json'; Detail = "$($file.FullName) was written for '$($noticesJson.AppName)': its notices and approvals are that app's" })
        }
        foreach ($entry in @($noticesJson.Inventory | Where-Object { $_ })) { $mergedInventory.Add($entry) }
        $noticesJson.Components
    }
    else
    {
        throw "Unsupported build output '$($file.FullName)'. Expected *.deps.json, 3rdpartylicenses.txt, webpack-components.json or a notices JSON written by -Json."
    }
}

# Scanned once per folder against every deps.json published into it, so one app's packages are not reported as
# unlisted binaries of another app published next to it.
$unlisted = foreach ($entry in $depsByBinariesDirectory.GetEnumerator())
{
    foreach ($binary in @(Get-UnlistedBinaries -Deps $entry.Value.ToArray() -Directory $entry.Key -Excludes $globalExcludes -ExcludeExceptions $excludeExceptions -IncludeOwn:$SbomOwnBinaries))
    {
        # Ours: the SBOM only, as -SbomRuntime's frameworks
        if ($binary.IsFirstParty)
        {
            $mergedInventory.Add([pscustomobject]@{
                Ecosystem = 'binary'; Name = $binary.Name; Version = $binary.Version; License = ''; Source = ''; IsRuntime = $false; IsFirstParty = $true
            })
            continue
        }

        [pscustomobject]@{
            Ecosystem         = 'binary'
            Name              = $binary.Name
            Version           = $binary.Version
            License           = ''
            Source            = ''
            Website           = ''
            Copyright         = $binary.Copyright
            LicenseText       = ''
            LicenseTextOrigin = 'none'
            Path              = $binary.Path
            Company           = $binary.Company
        }
    }
}

$manual = foreach ($path in @($ManualComponentsPath | Where-Object { $_ }))
{
    $manualFile = Resolve-RepoPath $path
    if (-not (Test-Path -LiteralPath $manualFile -PathType Leaf)) { throw "Manual components file '$path' not found." }
    Get-ManualComponents -Path $manualFile -BinariesPath $(if ($BinariesPath) { Resolve-RepoPath $BinariesPath })
}

$embedded = if ($EmbeddedAssetsPath)
{
    $assetList = Get-Content -LiteralPath (Resolve-RepoPath $EmbeddedAssetsPath) -Raw | ConvertFrom-Json -AsHashtable
    foreach ($directory in $depsByBinariesDirectory.Keys)
    {
        Get-EmbeddedComponents -Directory $directory -AssetList $assetList -Issues $issues
    }
}

$collected = @($collected) + @($unlisted) + @($embedded) + @($manual)
$collected = @(foreach ($group in @($collected | Where-Object { $_ } |
    Group-Object { "$($_.Ecosystem):$($_.Name)".ToLowerInvariant() }))
{
    $candidates = @($group.Group)
    if (@($candidates | Where-Object ShippedVersionMatch -eq $true).Count)
    {
        $candidates = @($candidates | Where-Object ShippedVersionMatch -ne $false)
    }
    elseif (@($candidates | Where-Object ShippedVersionMatch -eq $false).Count)
    {
        $issues.Add([pscustomobject]@{
            Kind = 'ShippedVersionMismatch'; Id = $candidates[0].Name; Ecosystem = $candidates[0].Ecosystem
            Detail = "Published file versions do not match the dependency manifests. Verify package provenance for versions: $($candidates.Version -join ', ')."
        })
    }
    $candidates
})
$inventory = @(Get-ComponentInventory -Components (@($collected) + $mergedInventory.ToArray()))

$attributed = @($collected | Where-Object { -not $_.IsFirstParty -and -not (Test-Excluded -Name $_.Name -Patterns $globalExcludes -Exceptions $excludeExceptions) })

# A sidecar describing a package another reader found joins that package's block, so one upstream project gets one
# block: every package of that name in one ecosystem has its repository and licence, so the block that prints is its
# project's. Copied code, manual entries and components read back from a notices JSON (taken as they are) never join,
# and nothing joins a group that holds one.
$isResolved = { $_.PSObject.Properties['IsResolved'] -and $_.IsResolved }
$packagesByName = ($attributed | Where-Object Ecosystem -in 'npm', 'nuget' | Group-Object { $_.Name.ToLowerInvariant() } -AsHashTable -AsString) ?? @{}
foreach ($sidecar in @($attributed | Where-Object Ecosystem -eq 'embedded' | Where-Object { -not (& $isResolved) }))
{
    $page = ([string](Get-ComponentPage -Component $sidecar)).TrimEnd('/')
    $targets = @($packagesByName[$sidecar.Name.ToLowerInvariant()] | Group-Object Ecosystem |
        Where-Object { $page -and -not @($_.Group | Where-Object { (& $isResolved) -or $_.License -ne $sidecar.License -or ([string](Get-ComponentPage -Component $_)).TrimEnd('/') -ne $page }) } |
        Select-Object -ExpandProperty Name)
    if ($targets.Count -eq 1) { $sidecar | Add-Member -NotePropertyName JoinsEcosystem -NotePropertyValue $targets[0] -Force }
}

$componentGroups = foreach ($group in @($attributed |
    Group-Object { "$(if ($_.PSObject.Properties['JoinsEcosystem']) { $_.JoinsEcosystem } else { $_.Ecosystem }):$($_.Name)".ToLowerInvariant() }))
{
    if ($SeparateVersions) { $group.Group | Group-Object Version } else { $group }
}

$components = foreach ($group in @($componentGroups))
{
    # A component read back from a notices JSON can already list several versions ("1.0, 2.0"): ranked by its highest
    $versionKey = {
        $highest = [version]'0.0'
        foreach ($version in ($_.Version -split ',\s*'))
        {
            $parsed = $null
            if ([version]::TryParse(($version -split '-')[0], [ref]$parsed) -and $parsed -gt $highest) { $highest = $parsed }
        }

        $highest
    }

    $candidates = @($group.Group)
    # A sidecar that joined a package's block never stands in for the package, whatever version it gives
    $packages = @($candidates | Where-Object Ecosystem -ne 'embedded')
    $joined = @(if ($packages) { $candidates | Where-Object Ecosystem -eq 'embedded' })
    $component = @(if ($packages) { $packages } else { $candidates }) | Sort-Object $versionKey | Select-Object -Last 1
    $component.Version = ($candidates.Version | ForEach-Object { $_ -split ',\s*' } | Where-Object { $_ } | Sort-Object -Unique) -join ', '

    # Resolved and validated by the run that wrote the notices JSON: taken as it is
    if ($component.PSObject.Properties['IsResolved'] -and $component.IsResolved)
    {
        $component
        continue
    }

    $override = $overrides[$component.Name.ToLowerInvariant()]
    if ($override)
    {
        # A replaced license makes the package's own text the wrong one (a split license that grants Apache-2.0)
        $licenseReplaced = (-not $component.License -or $component.License -eq 'Custom') -and $override.license
        Set-OverrideFields -Component $component -Override $override
        if ((-not $component.LicenseText -or $licenseReplaced) -and $override.licenseTextPath)
        {
            $component.LicenseText = Get-Content -LiteralPath (Resolve-ToolPath $override.licenseTextPath) -Raw
            $component.LicenseTextOrigin = 'override'
        }
    }

    $component.Copyright = Format-Copyright -Copyright $component.Copyright
    $repositoryUrl = Get-ComponentPage -Component $component
    $isGitHub = $component.Ecosystem -ne 'binary' -and (Get-GitHubRepository -Url $repositoryUrl)

    # A text written by hand beside an asset or in copied source only stands in for a repository that gives none
    $handWritten = $component.LicenseTextOrigin -in 'sidecar', 'source'
    if ((-not $component.LicenseText -or $handWritten) -and $isGitHub -and $component.LicenseTextOrigin -ne 'omitted')
    {
        try
        {
            $repositoryLicense = Get-RepositoryLicense -RepositoryUrl $repositoryUrl -Commit $component.RepositoryCommit
            if ($repositoryLicense -and $repositoryLicense.Text)
            {
                if ($handWritten -and $component.LicenseText)
                {
                    $issues.Add([pscustomobject]@{ Kind = 'UnusedLicenseText'; Id = $component.Name; Ecosystem = $component.Ecosystem; Detail = "$($component.Path) gives a licence text, but $repositoryUrl has its own: that one is printed" })
                }

                $component.LicenseText = $repositoryLicense.Text
                $component.LicenseTextOrigin = 'repository'
            }
        }
        catch
        {
            $issues.Add([pscustomobject]@{ Kind = 'LicenseUnavailable'; Id = $component.Name; Ecosystem = $component.Ecosystem; Detail = "Could not read the LICENSE file of $repositoryUrl : $($_.Exception.Message)" })
        }
    }

    # A template line ("Copyright (c) 2024 [fullname]") names no holder and goes later, so it does not count here
    if ($joined -and -not $component.Copyright -and -not @(Get-CopyrightLines -LicenseText (Update-TemplateCopyrightLine -LicenseText $component.LicenseText)))
    {
        $component.Copyright = Format-Copyright -Copyright ([string]($joined.Copyright | Where-Object { $_ } | Select-Object -First 1))
    }

    # A text left out on purpose (Legal's notice is the attribution) is neither looked up nor reported missing
    if (-not $component.LicenseText -and $component.LicenseTextOrigin -ne 'omitted')
    {
        $component.LicenseText = Get-StandardLicenseText -License $component.License
        if ($component.LicenseText)
        {
            $component.LicenseTextOrigin = 'spdx'
            $component.LicenseText = Update-TemplateCopyrightLine -LicenseText $component.LicenseText -Copyright $component.Copyright
        }
        elseif ($component.Ecosystem -eq 'binary')
        {
            $detail = "Binary not provided by any package ($($component.Path), company '$($component.Company)'): add its license to config/package-overrides.json under '$($component.Name)'"
            $issues.Add([pscustomobject]@{ Kind = 'MissingLicenseText'; Id = $component.Name; Ecosystem = 'binary'; Detail = $detail })
        }
        else
        {
            $issues.Add([pscustomobject]@{ Kind = 'MissingLicenseText'; Id = $component.Name; Ecosystem = $component.Ecosystem; Detail = "License '$($component.License)'" })
        }
    }
    elseif ($component.LicenseTextOrigin -in 'package', 'repository')
    {
        # The text a package or its repository gives may leave its template line unfilled (Bitbound.SimpleMessenger)
        $component.LicenseText = Update-TemplateCopyrightLine -LicenseText $component.LicenseText -Copyright $component.Copyright
    }

    $copyrightLines = @(Get-CopyrightLines -LicenseText $component.LicenseText)
    if ($copyrightLines)
    {
        $knownWords = @($copyrightLines | ForEach-Object { Get-CopyrightHolderWords -Line $_ })
        $knownKeys = @($copyrightLines | ForEach-Object { Get-CopyrightHolderKey -Line $_ })
        $isKnown = $component.Copyright -and (
            (Get-CopyrightHolderKey -Line $component.Copyright) -in $knownKeys -or
            (Get-CopyrightHolderWords -Line $component.Copyright | Where-Object { $_ -in $knownWords }))
        if ($component.Copyright -and -not $isKnown)
        {
            $copyrightLines = @($component.Copyright) + $copyrightLines
        }

        $component.Copyright = $copyrightLines -join "`n"
    }
    elseif (-not $component.Copyright)
    {
        $issues.Add([pscustomobject]@{ Kind = 'MissingCopyright'; Id = $component.Name; Ecosystem = $component.Ecosystem; Detail = "No copyright holder in the license text, the package metadata or its assemblies: add 'copyright' to config/package-overrides.json under '$($component.Name)'" })
    }

    # A notice declared with a manual component (the file its package ships) wins over the repository's.
    $notice = if ($component.PSObject.Properties['Notice']) { $component.Notice }
    if (-not $notice -and $isGitHub)
    {
        try
        {
            $notice = Get-RepositoryNotice -RepositoryUrl $repositoryUrl -Commit $component.RepositoryCommit

            # A joined sidecar names the same repository: a commit one pins can still hold the NOTICE
            foreach ($pinned in @($joined.RepositoryCommit | Where-Object { $_ -and $_ -ne $component.RepositoryCommit } | Select-Object -Unique))
            {
                if ($notice) { break }
                $notice = Get-RepositoryNotice -RepositoryUrl $repositoryUrl -Commit $pinned
            }
        }
        catch
        {
            $issues.Add([pscustomobject]@{ Kind = 'NoticeUnavailable'; Id = $component.Name; Ecosystem = $component.Ecosystem; Detail = "Could not read the NOTICE file of $repositoryUrl : $($_.Exception.Message)" })
        }
    }
    elseif (-not $notice -and $component.Ecosystem -notin @('binary', 'manual') -and -not $component.IsRuntime)
    {
        $issues.Add([pscustomobject]@{ Kind = 'NoticeNotChecked'; Id = $component.Name; Ecosystem = $component.Ecosystem; Detail = "No GitHub repository found (source '$repositoryUrl'): check it for a NOTICE file by hand" })
    }

    $overrideNotice = if ($override) { Get-OverrideNotice -Notices $override.notices -ResolvePath { param($path) Resolve-ToolPath $path } }
    $notice = Join-ComponentNotice -PackageNotice $component.PackageNotice -OverrideNotice $overrideNotice -RepositoryNotice $notice

    # A template line a NOTICE copies has no holder to attach, so it goes (legal, 2026-10-14)
    if ($notice -and $notice.Text)
    {
        $notice = [pscustomobject]@{ Repository = $notice.Repository; File = $notice.File; Url = $notice.Url; Text = Update-TemplateCopyrightLine -LicenseText $notice.Text }
    }

    $legalNotices = @()
    # The notice names the version of the text printed: the SPDX choice when the text came from SPDX, the LGPL choice
    # when the text a package or repository gives is the LGPL itself
    $lgplLicense = if ($component.LicenseTextOrigin -eq 'spdx') { Get-PrintedLicenseAlternative -License $component.License }
        elseif ($component.LicenseText -match 'GNU LESSER GENERAL PUBLIC LICENSE') { @(Get-LicenseAlternatives -License $component.License) -match 'LGPL-3\.0' | Select-Object -First 1 }
        else { $component.License }
    $lgplVersion = Get-LgplVersion -License $lgplLicense
    if ($lgplVersion)
    {
        $sharedTexts = @()
        $gplText = Get-SpdxLicenseText -Id 'GPL-3.0-only'
        if ($gplText)
        {
            $sharedTexts += [pscustomobject]@{ Id = 'GPL-3.0'; Text = $gplText }
        }
        else
        {
            $issues.Add([pscustomobject]@{ Kind = 'MissingLicenseText'; Id = $component.Name; Ecosystem = $component.Ecosystem; Detail = 'The GNU GPL v3 text its LGPL notice includes could not be read from SPDX' })
        }

        # A text that is not the LGPL (GNU layout: COPYING is the GPL) gets the LGPL v3 text once, under its own label
        $lgplText = if ($component.LicenseText -notmatch 'GNU LESSER GENERAL PUBLIC LICENSE') { Get-SpdxLicenseText -Id 'LGPL-3.0-only' }
        if ($lgplText)
        {
            $sharedTexts += [pscustomobject]@{ Id = 'LGPL-3.0'; Text = $lgplText }
        }
        elseif ($component.LicenseText -notmatch 'GNU LESSER GENERAL PUBLIC LICENSE')
        {
            $issues.Add([pscustomobject]@{ Kind = 'MissingLicenseText'; Id = $component.Name; Ecosystem = $component.Ecosystem; Detail = 'The GNU LGPL v3 text its LGPL notice includes is not its licence text and could not be read from SPDX' })
        }

        if ($sharedTexts) { $component | Add-Member -NotePropertyName SharedTexts -NotePropertyValue $sharedTexts -Force }

        $sourceUrl = $repositoryUrl
        if ($isGitHub)
        {
            try
            {
                $sourceUrl = Get-RepositoryTagUrl -RepositoryUrl $repositoryUrl -Version $component.Version
            }
            catch
            {
                $issues.Add([pscustomobject]@{ Kind = 'NoticeUnavailable'; Id = $component.Name; Ecosystem = $component.Ecosystem; Detail = "Could not look up the release tag in $repositoryUrl : $($_.Exception.Message); the LGPL notice links the repository" })
                $sourceUrl = "https://github.com/$(Get-GitHubRepository -Url $repositoryUrl)"
            }
        }
        $lgplInFile = $lgplText -or $component.LicenseText -match 'GNU LESSER GENERAL PUBLIC LICENSE'
        $legalNotices += Get-LgplNotice -Component $component -AppName $AppName -LicenseVersion $lgplVersion -SourceUrl $sourceUrl `
            -GplLabel $(if ($gplText) { 'GPL-3.0' }) -LgplLabel $(if ($lgplText) { 'LGPL-3.0' }) -LgplTextMissing:(-not $lgplInFile)
    }

    $statement = Get-ComponentStatement -Override $override -AppName $AppName -Signed:$Signed
    if ($statement) { $legalNotices += $statement }

    if ($legalNotices)
    {
        $notice = [pscustomobject]@{
            Repository = [string]$notice.Repository
            File       = [string]$notice.File
            Url        = [string]$notice.Url
            Text       = @(@($legalNotices) + @($notice.Text) | Where-Object { $_ }) -join "`n`n"
        }
    }

    $component | Add-Member -NotePropertyName Notice -NotePropertyValue $notice -Force

    if (-not $component.Version)
    {
        $issues.Add([pscustomobject]@{ Kind = 'MissingVersion'; Id = $component.Name; Ecosystem = $component.Ecosystem; Detail = 'Version not found' })
    }

    if (-not $component.License)
    {
        $issues.Add([pscustomobject]@{ Kind = 'MissingLicense'; Id = $component.Name; Ecosystem = $component.Ecosystem; Detail = "No license declared: add 'license' to config/package-overrides.json under '$($component.Name)'" })
    }
    elseif (-not (Test-LicenseAllowed -License $component.License -Allowed $allowedLicenses))
    {
        $approval = Find-Approval -Name $component.Name -Approvals $approvedPackages -AppName $AppName
        $licenseTextSha256 = Get-LicenseTextHash -Text $(if ($component.LicenseTextOrigin -eq 'omitted') { $component.OmittedLicenseText } else { $component.LicenseText })
        if (-not $approval)
        {
            $issues.Add([pscustomobject]@{ Kind = 'LicenseNotAllowed'; Id = $component.Name; Ecosystem = $component.Ecosystem; Detail = "License '$($component.License)' is not in config/allowed-licenses.json; add the package to config/approved-packages.json once legal has approved it (license '$($component.License)', licenseTextSha256 '$licenseTextSha256')" })
        }
        elseif ($approval.license -ne $component.License -or $approval.licenseTextSha256 -ne $licenseTextSha256)
        {
            $issues.Add([pscustomobject]@{ Kind = 'ApprovedLicenseChanged'; Id = $component.Name; Ecosystem = $component.Ecosystem; Detail = "License or license text changed since its approval in config/approved-packages.json (approved '$($approval.license)', now '$($component.License)'): once legal has approved it again, set license '$($component.License)' and licenseTextSha256 '$licenseTextSha256'" })
        }
    }

    if (-not $component.Source)
    {
        $issues.Add([pscustomobject]@{ Kind = 'MissingSource'; Id = $component.Name; Ecosystem = $component.Ecosystem; Detail = "No source link: add 'source' to config/package-overrides.json under '$($component.Name)'" })
    }

    $component
}

Export-RemoteCache -Path $CachePath

$components = @($components | Sort-Object { $_.Name.ToLowerInvariant() }, Ecosystem)
foreach ($entry in $inventory)
{
    $resolved = $components | Where-Object { $_.Name -eq $entry.name -and $_.Ecosystem -eq $entry.ecosystem -and $entry.version -in ($_.Version -split ',\s*') } | Select-Object -First 1
    if ($resolved)
    {
        $entry.license = [string]$resolved.License
        $entry.source = [string](Get-ComponentPage -Component $resolved)
    }
}
$notices = Format-Notices -TemplatePath $TemplatePath -AppName $AppName -AppVersion $AppVersion -Components $components
$lock = @(ConvertTo-LicenseLock -Components $components)

$resolvedOutput = Resolve-RepoPath $OutputPath
$lockPath = [IO.Path]::ChangeExtension($resolvedOutput, '.lock.json')
$previousLock = if (Test-Path -LiteralPath $lockPath) { @(Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable) } else { @() }
$changes = @(if ($previousLock.Count) { Compare-LicenseLock -Current $lock -Previous $previousLock -SeparateVersions:$SeparateVersions })

if ($Check)
{
    $noticesPath = Join-Path $CheckOutputDirectory (Split-Path -Leaf $resolvedOutput)
    New-Item -ItemType Directory -Force -Path $CheckOutputDirectory | Out-Null
}
else
{
    $noticesPath = $resolvedOutput
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedOutput) | Out-Null
    Set-Content -LiteralPath $lockPath -Value (ConvertTo-Json -InputObject $lock -Depth 4) -Encoding utf8NoBOM
}

Set-Content -LiteralPath $noticesPath -Value $notices -NoNewline -Encoding utf8NoBOM

$jsonPath = if ($Json) { [IO.Path]::ChangeExtension($noticesPath, '.json') }
if ($jsonPath)
{
    $jsonArguments = @{ AppName = $AppName; AppVersion = $AppVersion; Components = $components }
    # For a later run to merge; an SBOM written to its own file needs none, and the notices JSON may ship
    if ($Sbom -and -not $SbomPath) { $jsonArguments.Inventory = $inventory }
    Set-Content -LiteralPath $jsonPath -Value (ConvertTo-NoticesJson @jsonArguments) -Encoding utf8NoBOM
}

if ($Sbom)
{
    $sbomFile = if ($SbomPath) { Resolve-RepoPath $SbomPath } else { [IO.Path]::ChangeExtension($noticesPath, '.sbom.json') }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $sbomFile) | Out-Null
    $sbomArguments = @{ AppName = $AppName; AppVersion = $AppVersion; Components = $inventory }
    if ($SbomDetails) { $sbomArguments += @{ Details = $true; PackagesRoot = $packagesRoot; Deps = $readDeps.ToArray() } }
    Set-Content -LiteralPath $sbomFile -Value (ConvertTo-CycloneDxJson @sbomArguments) -Encoding utf8NoBOM
}

$issues = @(foreach ($issue in $issues)
{
    $severity = if ($issue.Kind -in $errorKinds) { 'Error' } elseif ($issue.Kind -in $warningKinds) { 'Warning' } else { 'Info' }
    $issue | Add-Member -NotePropertyName Severity -NotePropertyValue $severity -Force
    $issue
})

Write-LicenseReport -AppName $AppName -ComponentCount $components.Count -NoticesPath $noticesPath -Changes $changes -Issues $issues -HasLock ($previousLock.Count -gt 0)

if ($env:GITHUB_OUTPUT)
{
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "notices-path=$noticesPath"
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "license-changes=$(@($changes | Where-Object Kind -eq 'LicenseChanged').Count)"
}

exit $(if (@($issues | Where-Object Severity -eq 'Error').Count) { 1 } else { 0 })
