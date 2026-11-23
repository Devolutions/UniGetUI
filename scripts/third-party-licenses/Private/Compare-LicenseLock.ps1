function ConvertTo-LicenseLock
{
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Components
    )

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try
    {
        foreach ($component in $Components)
        {
            $shared = -join @($component.SharedTexts | Where-Object { $_ } | ForEach-Object { "`n$($_.Id)`n$($_.Text)" })
            $text = "$($component.Copyright)`n$($component.LicenseText)$(if ($component.Notice) { "`n$($component.Notice.Text)" })$shared"
            $hash = if ($text.Trim()) { [Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant() } else { '' }

            [ordered]@{
                id         = $component.Name
                ecosystem  = $component.Ecosystem
                version    = $component.Version
                license    = $component.License
                textSha256 = $hash
            }
        }
    }
    finally
    {
        $sha.Dispose()
    }
}

function Compare-LicenseLock
{
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Current,
        [AllowEmptyCollection()] [object[]] $Previous = @(),
        [switch] $SeparateVersions
    )

    $keyOf = { param($entry) "$($entry.ecosystem):$($entry.id)$(if ($SeparateVersions) { ":$($entry.version)" })".ToLowerInvariant() }
    $previousByKey = @{}
    foreach ($entry in $Previous) { $previousByKey[(& $keyOf $entry)] = $entry }
    $currentByKey = @{}
    foreach ($entry in $Current) { $currentByKey[(& $keyOf $entry)] = $entry }

    $changes = foreach ($key in $currentByKey.Keys)
    {
        $now = $currentByKey[$key]
        $before = $previousByKey[$key]
        if (-not $before)
        {
            [pscustomobject]@{ Kind = 'Added'; Id = $now.id; Ecosystem = $now.ecosystem; Before = ''; After = "$($now.version) ($($now.license))" }
            continue
        }

        if ($before.license -ne $now.license)
        {
            [pscustomobject]@{ Kind = 'LicenseChanged'; Id = $now.id; Ecosystem = $now.ecosystem; Before = $before.license; After = $now.license }
        }
        elseif ($before.textSha256 -ne $now.textSha256)
        {
            [pscustomobject]@{ Kind = 'TextChanged'; Id = $now.id; Ecosystem = $now.ecosystem; Before = $before.version; After = $now.version }
        }

        if ($before.version -ne $now.version)
        {
            [pscustomobject]@{ Kind = 'VersionChanged'; Id = $now.id; Ecosystem = $now.ecosystem; Before = $before.version; After = $now.version }
        }
    }

    $removed = foreach ($key in $previousByKey.Keys)
    {
        if (-not $currentByKey.ContainsKey($key))
        {
            $before = $previousByKey[$key]
            [pscustomobject]@{ Kind = 'Removed'; Id = $before.id; Ecosystem = $before.ecosystem; Before = "$($before.version) ($($before.license))"; After = '' }
        }
    }

    $order = @{ LicenseChanged = 0; TextChanged = 1; Added = 2; Removed = 3; VersionChanged = 4 }
    return @(@($changes) + @($removed) | Where-Object { $_ } | Sort-Object { $order[$_.Kind] }, Id)
}
