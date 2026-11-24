#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PingetRepository,

    [string] $Version,

    [string] $Toolchain = 'nightly',

    [string[]] $Targets = @('x86_64-pc-windows-msvc', 'aarch64-pc-windows-msvc'),

    [string] $OutputPath = (Join-Path $PSScriptRoot 'config/pinget-crates.json')
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

function Invoke-Native
{
    param([Parameter(Mandatory)] [string] $FilePath, [string[]] $Arguments)

    $output = & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FilePath $($Arguments -join ' ') exited with $LASTEXITCODE." }
    return $output
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$packageReference = 'Devolutions.Pinget.Cli.Rust'

if (-not $Version)
{
    $project = Get-Content (Join-Path $repoRoot 'src/UniGetUI.Avalonia/UniGetUI.Avalonia.csproj') -Raw
    if ($project -notmatch "Include=`"$([regex]::Escape($packageReference))`"\s+Version=`"([^`"]+)`"") { throw "No $packageReference reference found." }
    $Version = $Matches[1]
}

$tag = "v$Version"
$tagTime = [DateTimeOffset]::Parse((Invoke-Native git @('-C', $PingetRepository, 'log', '-1', '--format=%cI', $tag))).UtcDateTime
$publishTime = $tagTime.ToString('yyyy-MM-ddTHH:mm:ssZ')

$work = Join-Path ([IO.Path]::GetTempPath()) "pinget-crates-$Version-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Path $work | Out-Null
try
{
    $archive = Join-Path $work 'source.zip'
    Invoke-Native git @('-C', $PingetRepository, 'archive', '--format=zip', '--output', $archive, $tag, 'rust') | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $work
    $workspace = Join-Path $work 'rust'

    Push-Location $workspace
    try
    {
        Write-Host "Resolving pinget $Version crates as published before $publishTime"
        Invoke-Native cargo @("+$Toolchain", 'generate-lockfile', '-Zunstable-options', '--publish-time', $publishTime, '--quiet') | Out-Null

        $shipped = [Collections.Generic.HashSet[string]]::new()
        foreach ($target in $Targets)
        {
            $lines = Invoke-Native cargo @("+$Toolchain", 'tree', '--locked', '-p', 'pinget-cli', '-e', 'normal,no-proc-macro', '--target', $target, '--prefix', 'none', '-f', '{p}')
            foreach ($line in $lines)
            {
                if ($line -match '^(\S+) v(\S+)') { [void]$shipped.Add("$($Matches[1]) $($Matches[2])") }
            }
        }

        $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }
        $binaries = @(Get-ChildItem (Join-Path $nugetRoot "$($packageReference.ToLowerInvariant())/$Version/runtimes") -Recurse -File -Filter 'pinget.exe' -ErrorAction SilentlyContinue)
        if (-not $binaries) { throw "No pinget.exe found for $packageReference $Version in $nugetRoot. Restore UniGetUI first." }

        $cratePath = [regex]'index\.crates\.io-[0-9a-f]+[\\/]([A-Za-z0-9_\-]+?)-(\d+\.\d+\.\d+(?:-[0-9A-Za-z.\-]+)?(?:\+[0-9A-Za-z.\-]+)?)[\\/]'
        $inBinaries = [Collections.Generic.HashSet[string]]::new()
        foreach ($binary in $binaries)
        {
            $text = [Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes($binary.FullName))
            foreach ($match in $cratePath.Matches($text)) { [void]$inBinaries.Add("$($match.Groups[1].Value) $($match.Groups[2].Value)") }
        }

        $unexplained = @($inBinaries | Where-Object { -not $shipped.Contains($_) } | Sort-Object)
        if ($unexplained)
        {
            throw "The resolved crates do not match the shipped pinget binaries, which contain: $($unexplained -join ', ')"
        }
        Write-Host "$($shipped.Count) crates resolved; all $($inBinaries.Count) crate versions found in the shipped binaries match."

        foreach ($target in $Targets) { Invoke-Native cargo @("+$Toolchain", 'fetch', '--locked', '--target', $target, '--quiet') | Out-Null }
        $metadata = (Invoke-Native cargo @("+$Toolchain", 'metadata', '--locked', '--format-version', '1')) -join "`n" | ConvertFrom-Json
    }
    finally
    {
        Pop-Location
    }

    $licenseFile = [regex]'^(?i)(un)?licen[cs]e|^(?i)copying'
    $copyrightFile = [regex]'^(?i)copyright(\.(txt|md))?$'
    $noticeFile = [regex]'^(?i)notice(\.(txt|md))?$'
    $textsDirectory = Join-Path $work 'texts'
    New-Item -ItemType Directory -Path $textsDirectory | Out-Null
    $crateOverrides = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'config/pinget-crate-overrides.json') -Raw | ConvertFrom-Json -AsHashtable

    function Test-NamesHolder
    {
        param([IO.FileInfo] $File)

        foreach ($line in (Get-Content -LiteralPath $File.FullName))
        {
            if ($line -cnotmatch '^\s*(Copyright\s+(\([cC]\)|©|\d{4})|\([cC]\)\s*\d{4}|©)') { continue }
            $holder = $line -replace '(?i)copyright|\(c\)|©|all rights reserved|<[^>]*>|\[[^\]]*\]|\{[^}]*\}|\d{4}', '' -replace '[^\p{L}]', ''
            if ($holder.Length -ge 3) { return $true }
        }

        return $false
    }

    function ConvertTo-LicenseKey
    {
        param([string] $Text)

        return ($Text -replace '[^A-Za-z0-9]', '').ToLowerInvariant()
    }

    function Get-LicenseFileFor
    {
        param([string] $Id, [IO.FileInfo[]] $Files)

        $keys = @(
            ConvertTo-LicenseKey $Id
            ConvertTo-LicenseKey ($Id -replace '-(only|or-later)$', '' -replace '-\d+(\.\d+)+$', '')
            if ($Id -match '^BSD-\d-Clause$') { 'bsd' }
        )

        foreach ($file in $Files)
        {
            $suffix = if ($file.BaseName -match '^(?i)licen[cs]e[-_.](.+)$') { ConvertTo-LicenseKey $Matches[1] }
                elseif ($file.BaseName -match '^(?i)unlicense$') { 'unlicense' }
            if ($suffix -and $keys -contains $suffix) { return $file }
        }

        return $null
    }

    $components = foreach ($package in @($metadata.packages | Where-Object { $shipped.Contains("$($_.name) $($_.version)") -and $_.name -notlike 'pinget*' } | Sort-Object name, version))
    {
        $directory = Split-Path -Parent $package.manifest_path
        $files = @(Get-ChildItem -LiteralPath $directory -File | Where-Object { $licenseFile.IsMatch($_.Name) })
        $expression = ([string]$package.license) -replace '\s*/\s*', ' OR '

        $chosen = @()
        if ($files.Count -eq 1)
        {
            $chosen = $files
        }
        elseif ($files.Count -gt 1 -and $expression)
        {
            $terms = @($expression -replace '[()]', '' -split '\s+AND\s+')
            foreach ($term in $terms)
            {
                $available = @($(foreach ($option in ($term -split '\s+OR\s+')) { Get-LicenseFileFor -Id ($option -split '\s+WITH\s+')[0].Trim() -Files $files }) | Where-Object { $_ })
                $match = @($available | Where-Object { Test-NamesHolder $_ }) + $available | Select-Object -First 1
                if (-not $match) { $chosen = @(); break }
                if ($chosen.FullName -notcontains $match.FullName) { $chosen += $match }
            }
        }

        $entry = [ordered]@{
            name    = $package.name
            version = $package.version
            license = $expression
            source  = if ($package.repository) { ([string]$package.repository).TrimEnd('/') -replace '\.git$', '' } else { "https://crates.io/crates/$($package.name)" }
            website = "https://crates.io/crates/$($package.name)/$($package.version)"
        }

        $authors = @($package.authors | ForEach-Object { ($_ -replace '\s*<[^>]*>', '').Trim() } | Where-Object { $_ })
        if ($authors) { $entry.copyright = "Copyright (c) $($authors -join ', ')" }

        $vcsInfo = Join-Path $directory '.cargo_vcs_info.json'
        if (Test-Path -LiteralPath $vcsInfo)
        {
            $sha = (Get-Content -LiteralPath $vcsInfo -Raw | ConvertFrom-Json).git.sha1
            if ($sha) { $entry.repositoryCommit = $sha }
        }

        $override = $crateOverrides[$package.name]
        if ($override) { foreach ($field in $override.Keys) { $entry[$field] = $override[$field] } }

        if ($chosen)
        {
            $name = "$($package.name)-$($package.version).txt"
            $chosen = @(Get-ChildItem -LiteralPath $directory -File | Where-Object { $copyrightFile.IsMatch($_.Name) }) + $chosen
            $texts = $chosen | ForEach-Object { (Get-Content -LiteralPath $_.FullName -Raw).Trim() }
            Set-Content -LiteralPath (Join-Path $textsDirectory $name) -Value ($texts -join "`n`n") -NoNewline -Encoding utf8NoBOM
            $entry.licenseTextPath = "texts/$name"
        }

        $notice = Get-ChildItem -LiteralPath $directory -File | Where-Object { $noticeFile.IsMatch($_.Name) } | Select-Object -First 1
        if ($notice)
        {
            Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $textsDirectory "$($package.name)-$($package.version).NOTICE")
            $entry.noticePath = "texts/$($package.name)-$($package.version).NOTICE"
        }

        $entry
    }

    $manualPath = Join-Path $work 'crates.json'
    @{ components = @($components) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manualPath -Encoding utf8NoBOM
    $seedPath = Join-Path $work 'seed.json'
    @{ format = 'devolutions-third-party-notices/1'; appName = 'pinget'; appVersion = $Version; components = @() } | ConvertTo-Json | Set-Content -LiteralPath $seedPath -Encoding utf8NoBOM

    & (Join-Path $PSScriptRoot 'Invoke-ThirdPartyLicenses.ps1') `
        -AppName 'pinget' `
        -AppVersion $Version `
        -ArtifactPath $seedPath `
        -ManualComponentsPath $manualPath `
        -OutputPath (Join-Path $work 'THIRD-PARTY-NOTICES.txt') `
        -Check `
        -CheckOutputDirectory $work `
        -Json `
        -RepoRoot $repoRoot
    $exitCode = $LASTEXITCODE

    $generated = Join-Path $work 'THIRD-PARTY-NOTICES.json'
    if (-not (Test-Path -LiteralPath $generated)) { throw 'The crate notices were not generated.' }
    Copy-Item -LiteralPath $generated -Destination $OutputPath -Force
    Write-Host "Wrote $OutputPath ($(@($components).Count) crates)"
}
finally
{
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
exit $exitCode
