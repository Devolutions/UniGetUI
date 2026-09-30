param(
    [Parameter(Mandatory)][string]$InputDirectory,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cache = @{}
$rows = [Collections.Generic.List[string]]::new()
$files = @(Get-ChildItem $InputDirectory -Filter '*.inputs')
if ($files.Count -eq 0) { throw 'No compiler input records were produced.' }
foreach ($file in $files) {
    $rows.Add("project|$($file.BaseName)")
    foreach ($line in Get-Content $file.FullName) {
        $parts = $line -split '\|', 2
        if ($parts.Count -ne 2) {
            $rows.Add("$($file.BaseName)|flag|$line")
            continue
        }
        $kind, $path = $parts
        if ($kind -eq 'flags') {
            $rows.Add("$($file.BaseName)|flags|$path")
            continue
        }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Missing compiler input: $path"
        }
        $full = [IO.Path]::GetFullPath($path)
        $repositoryFile = $full.StartsWith("$root$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase)
        $identity = if ($kind -in @('reference', 'analyzer')) {
            [IO.Path]::GetFileName($full)
        }
        elseif ($repositoryFile) { [IO.Path]::GetRelativePath($root, $full) }
        elseif ($full.StartsWith($env:NUGET_PACKAGES, [StringComparison]::OrdinalIgnoreCase)) {
            "package\$([IO.Path]::GetRelativePath($env:NUGET_PACKAGES, $full))"
        }
        elseif ($full.StartsWith($env:BenchmarkGeneratedSources, [StringComparison]::OrdinalIgnoreCase)) {
            "generated\$([IO.Path]::GetRelativePath($env:BenchmarkGeneratedSources, $full))"
        }
        elseif ($full.StartsWith((Join-Path $env:RUNNER_TEMP 'windows-projection'), [StringComparison]::OrdinalIgnoreCase)) {
            "projection\$([IO.Path]::GetFileName($full))"
        }
        else { throw "Unclassified compiler input: $full" }
        $identity = $identity.Replace('\', '/')
        $projectReference = $kind -eq 'reference' -and $repositoryFile
        $hash = if ($projectReference) { 'project-reference' }
        elseif ($cache.ContainsKey($full)) { $cache[$full] }
        else {
            $bytes = if ([IO.Path]::GetExtension($full) -in @('.cs', '.axaml', '.resx')) {
                [Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText($full).Replace("`r`n", "`n"))
            }
            else { [IO.File]::ReadAllBytes($full) }
            $cache[$full] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
            $cache[$full]
        }
        $rows.Add("$($file.BaseName)|$kind|$identity|$hash")
    }
}
$canonical = @($rows | Sort-Object -Unique -CaseSensitive)
$canonical | Set-Content "$Output.inputs" -Encoding utf8NoBOM
$digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes($canonical -join "`n")))
[ordered]@{
    projects = $files.Count
    inputs = $canonical.Count
    sha256 = $digest
} | ConvertTo-Json | Set-Content $Output -Encoding utf8
