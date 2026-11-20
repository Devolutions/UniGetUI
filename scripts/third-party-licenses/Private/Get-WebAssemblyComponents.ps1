function Get-WebAssemblyComponents
{
    param(
        [Parameter(Mandatory)] [string] $ManifestPath,
        [Parameter(Mandatory)] [string] $BinariesPath,
        [Parameter(Mandatory)] [string] $PackagesRoot,
        [Parameter(Mandatory)] [hashtable] $UrlMappings,
        [switch] $IncludeProjects
    )

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.ManifestType -ne 'Publish')
    {
        throw "'$ManifestPath' is not a published static-web-assets manifest."
    }
    if (-not (Test-Path -LiteralPath $BinariesPath -PathType Container))
    {
        throw "Published browser directory '$BinariesPath' does not exist."
    }

    $directory = Split-Path -Parent $ManifestPath
    while ($directory -and -not (Test-Path -LiteralPath (Join-Path $directory 'project.assets.json')))
    {
        $directory = Split-Path -Parent $directory
    }
    if (-not $directory) { throw "No restored package provenance found for '$ManifestPath'." }
    $assets = Get-Content -LiteralPath (Join-Path $directory 'project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    $targetName = @($assets.targets.Keys | Where-Object { $_ -like '*/browser-wasm' })
    if ($targetName.Count -ne 1) { throw "'$ManifestPath' must have exactly one browser-wasm restore target." }

    $shipped = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $runtimePacks = @{}
    foreach ($asset in $manifest.Assets)
    {
        if ($asset.AssetRole -ne 'Primary' -or $asset.CopyToPublishDirectory -eq 'Never') { continue }
        $relative = $asset.RelativePath.Replace('#[.{fingerprint}]!', ".$($asset.Fingerprint)")
        if ([IO.Path]::IsPathRooted($relative) -or '..' -in ($relative -split '[/\\]'))
        {
            throw "Published browser asset '$relative' is outside its output directory."
        }
        if (-not (Test-Path -LiteralPath (Join-Path $BinariesPath $relative) -PathType Leaf)) { continue }

        if ($asset.AssetTraitName -eq 'WasmResource' -and $asset.AssetTraitValue -eq 'runtime')
        {
            [void]$shipped.Add([IO.Path]::ChangeExtension((Split-Path $asset.Identity -Leaf), '.dll'))
        }
        elseif ($asset.OriginalItemSpec)
        {
            [void]$shipped.Add((Split-Path $asset.OriginalItemSpec -Leaf))
        }

        $pack = [regex]::Match([string]$asset.OriginalItemSpec, '(?i)^(?<root>.*[\\/]packs[\\/](?<id>Microsoft\.NETCore\.App\.Runtime\.Mono\.browser-wasm)[\\/](?<version>[^\\/]+))[\\/]')
        if ($pack.Success)
        {
            $runtimePacks[$pack.Groups['id'].Value] = @{ Root = $pack.Groups['root'].Value; Version = $pack.Groups['version'].Value }
        }
    }

    if ($shipped.Count -eq 0) { throw "No published browser payload matched '$ManifestPath'." }

    $deps = [pscustomobject]@{ Libraries = $assets.libraries; Target = $assets.targets[$targetName[0]]; Rid = 'browser-wasm' }
    foreach ($component in Get-DotnetDepsComponents -Deps $deps -IncludeProjects:$IncludeProjects)
    {
        $componentAssets = @($component.Assets)
        if ($component.IsFirstParty)
        {
            $project = $assets.libraries["$($component.Name)/$($component.Version)"].path
            $componentAssets += [IO.Path]::ChangeExtension((Split-Path $project -Leaf), '.dll')
        }
        if (-not @($componentAssets | Where-Object { $shipped.Contains($_) }).Count) { continue }
        $metadata = if ($component.IsFirstParty) { [pscustomobject]@{} }
        else { Resolve-NuGetMetadata -Component $component -PackagesRoot $PackagesRoot -UrlMappings $UrlMappings }
        $version = $component.Version
        if ($component.IsFirstParty)
        {
            $assembly = $componentAssets | ForEach-Object { Join-Path (Split-Path -Parent $ManifestPath) "linked\$_" } |
                Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
            if ($assembly) { $version = ((Get-FileVersionProperties -Path $assembly).ProductVersion -split '\+')[0] }
        }
        [pscustomobject]@{
            Ecosystem = 'nuget'; Name = $component.Name; Version = $version
            License = $metadata.License; Source = $metadata.Source; Website = $metadata.Website
            Copyright = $metadata.Copyright; LicenseText = $metadata.LicenseText
            LicenseTextOrigin = $metadata.LicenseTextOrigin; RepositoryCommit = $metadata.RepositoryCommit
            PackageNotice = $metadata.PackageNotice; IsRuntime = $false; IsFirstParty = $component.IsFirstParty
        }
    }

    if ($IncludeProjects -and $manifest.Source -and $shipped.Contains("$($manifest.Source).dll"))
    {
        $assembly = Join-Path (Split-Path -Parent $ManifestPath) "linked\$($manifest.Source).dll"
        if (-not (Test-Path -LiteralPath $assembly)) { throw "Published browser assembly provenance is missing: '$assembly'." }
        [pscustomobject]@{
            Ecosystem = 'project'; Name = $manifest.Source
            Version = ((Get-FileVersionProperties -Path $assembly).ProductVersion -split '\+')[0]
            License = ''; Source = ''; Website = ''; Copyright = ''; LicenseText = ''; LicenseTextOrigin = 'none'
            RepositoryCommit = ''; IsRuntime = $false; IsFirstParty = $true
        }
    }

    foreach ($entry in $runtimePacks.GetEnumerator())
    {
        $licenseFile = Get-ChildItem -LiteralPath $entry.Value.Root -File |
            Where-Object Name -Match '^licen[cs]e(\.txt)?$' | Select-Object -First 1
        $noticeFile = Get-ChildItem -LiteralPath $entry.Value.Root -File |
            Where-Object Name -Match '^thirdpartynotices(\.txt)?$' | Select-Object -First 1
        [pscustomobject]@{
            Ecosystem = 'nuget'; Name = $entry.Key; Version = $entry.Value.Version
            License = 'MIT'; Source = 'https://github.com/dotnet/runtime'; Website = 'https://dot.net'
            Copyright = ''; LicenseText = if ($licenseFile) { Get-Content -LiteralPath $licenseFile.FullName -Raw } else { '' }
            LicenseTextOrigin = if ($licenseFile) { 'package' } else { 'none' }
            RepositoryCommit = "v$($entry.Value.Version)"; IsRuntime = $true; IsFirstParty = $false
            Notice = if ($noticeFile)
            {
                [pscustomobject]@{ Repository = ''; File = $noticeFile.Name; Url = ''; Text = Get-Content -LiteralPath $noticeFile.FullName -Raw }
            }
        }
    }
}
