#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Path,

    [string] $MaxVersion
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command readelf -ErrorAction SilentlyContinue)) {
    throw "readelf was not found on PATH. Install binutils to inspect glibc requirements."
}

$ResolvedPath = (Resolve-Path -LiteralPath $Path).Path

$Candidates = if (Test-Path -LiteralPath $ResolvedPath -PathType Container) {
    @(Get-ChildItem -LiteralPath $ResolvedPath -Recurse -File)
} else {
    @(Get-Item -LiteralPath $ResolvedPath)
}

function Test-ElfHeader {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath
    )

    $Header = [byte[]]::new(4)
    $Stream = [System.IO.File]::OpenRead($FilePath)
    try { $Read = $Stream.Read($Header, 0, 4) } finally { $Stream.Dispose() }

    return ($Read -eq 4 -and
            $Header[0] -eq 0x7F -and $Header[1] -eq 0x45 -and
            $Header[2] -eq 0x4C -and $Header[3] -eq 0x46)
}

$ElfFiles = @($Candidates | Where-Object { Test-ElfHeader -FilePath $_.FullName })
if ($ElfFiles.Count -eq 0) {
    throw "No ELF binaries were found under '$ResolvedPath'"
}

$Ceiling = if ($MaxVersion) { [version] $MaxVersion } else { $null }
$HighestVersion = $null
$Offenders = @()

foreach ($File in $ElfFiles) {
    $DynamicSymbols = (& readelf --wide --dyn-syms $File.FullName) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "readelf exited $LASTEXITCODE for '$($File.FullName)'" }

    $FileHighest = $null
    foreach ($Match in [regex]::Matches($DynamicSymbols, '(?<symbol>[A-Za-z_][A-Za-z0-9_]*)@+GLIBC_(?<version>\d+(?:\.\d+)+)')) {
        $Version = [version] $Match.Groups['version'].Value

        if ($null -eq $FileHighest -or $Version -gt $FileHighest) { $FileHighest = $Version }
        if ($null -eq $HighestVersion -or $Version -gt $HighestVersion) { $HighestVersion = $Version }

        if ($Ceiling -and $Version -gt $Ceiling) {
            $Offenders += [pscustomobject]@{
                File    = $File.Name
                Symbol  = $Match.Groups['symbol'].Value
                Version = $Version
            }
        }
    }

    $Reported = if ($FileHighest) { "GLIBC_$FileHighest" } else { 'no versioned glibc symbols' }
    Write-Host ("{0}: requires {1}" -f $File.Name, $Reported)
}

if ($null -eq $HighestVersion) {
    throw "No versioned glibc symbols were found under '$ResolvedPath'"
}

Write-Host ("Highest glibc requirement: GLIBC_$HighestVersion")

if ($Offenders.Count -gt 0) {
    Write-Host "References above the GLIBC_$Ceiling baseline:"
    $Offenders |
        Sort-Object -Property File, Symbol -Unique |
        ForEach-Object { Write-Host "  $($_.File): $($_.Symbol)@GLIBC_$($_.Version)" }

    throw "Binaries require GLIBC_$HighestVersion but the supported baseline is GLIBC_$Ceiling; they will not start on older distributions."
}

Write-Output $HighestVersion.ToString()
