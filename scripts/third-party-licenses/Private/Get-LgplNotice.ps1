# Legal's condition for shipping an LGPL-3.0 component (PI-831): its notice names the app that uses it, says the GNU GPL
# v3 text the LGPL builds on is in the file, and links the source of the shipped version.
function Get-LgplVersion
{
    param([string] $License)

    # A choice that offers another licence is taken under that one, so legal's LGPL condition does not apply
    $alternatives = @(Get-LicenseAlternatives -License $License)
    if ($alternatives | Where-Object { $_ -notmatch 'LGPL-3\.0' }) { return '' }

    # The first alternative's version; for a text from SPDX the caller passes the printed alternative only
    $ids = @($alternatives[0] -split '\s+' | Where-Object { $_ })
    if ($ids | Where-Object { $_ -in 'LGPL-3.0-or-later', 'LGPL-3.0+' }) { return 'version 3 or later' }
    if ($ids | Where-Object { $_ -in 'LGPL-3.0-only', 'LGPL-3.0' }) { return 'version 3' }
    return ''
}

function Get-LgplNotice
{
    param(
        [Parameter(Mandatory)] [pscustomobject] $Component,
        [Parameter(Mandatory)] [string] $AppName,
        [Parameter(Mandatory)] [string] $LicenseVersion,
        [string] $SourceUrl,
        [string] $GplLabel,
        [string] $LgplLabel,
        [switch] $LgplTextMissing
    )

    $name = $Component.Name
    $title = "$name $($Component.Version)".Trim()
    $holders = @(([string]$Component.Copyright) -split "`n" | Where-Object { $_ }) -join '; '
    $lines = @(
        $(if ($holders) { "$title — $holders" } else { $title })
        "$AppName uses $name, which is licensed under the GNU Lesser General Public License, $LicenseVersion; $name and its use in $AppName are covered by that license."
        $(if ($GplLabel -and -not $LgplTextMissing) { 'The full texts of the GNU LGPL v3 and GNU GPL v3 are included in this file.' })
        $(if ($LgplLabel) { "GNU LGPL v3: see [$LgplLabel]" })
        $(if ($GplLabel) { "GNU GPL v3: see [$GplLabel]" })
        $(if ($SourceUrl) { "The source code of $title is available at $SourceUrl." })
    )

    return @($lines | Where-Object { $_ }) -join "`n"
}
