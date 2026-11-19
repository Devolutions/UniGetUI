function Find-Approval
{
    param([string] $Name, [hashtable] $Approvals, [string] $AppName)

    foreach ($pattern in $Approvals.Keys)
    {
        $approval = Select-AppEntry -Value $Approvals[$pattern] -AppName $AppName | Select-Object -First 1
        if ($Name -like $pattern -and $approval) { return $approval }
    }

    return $null
}

# A config entry can be a list, and an entry that lists apps applies only to those apps' notices
function Select-AppEntry
{
    param($Value, [string] $AppName)

    return @($Value | Where-Object { -not @($_.apps | Where-Object { $_ }) -or $AppName -in $_.apps })
}

# Legal approves a text, not its line endings or the whitespace around it: neither changes the hash
function Get-LicenseTextHash
{
    param([string] $Text)

    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($Text -replace "`r", '').Trim()))).ToLowerInvariant()
}
