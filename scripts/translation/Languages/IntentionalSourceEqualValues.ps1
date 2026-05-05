function Test-IntentionalSourceEqualValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LanguageCode,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$SourceValue,

        [Parameter(Mandatory = $false)]
        [AllowEmptyString()]
        [string]$TargetValue = ''
    )

    if ($SourceValue -in @(
        'MSI',
        'MSIX',
        'OK',
        'UniGetUI',
        'UniGetUI - {0} {1}',
        'WinGet COM API',
        'your@email.com',
        '{0}: {1}',
        '{0}: {1}, {2}'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'it' -and $SourceValue -ceq 'No' -and $TargetValue -ceq 'No') {
        return $true
    }

    if ($LanguageCode -ceq 'it' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'DEBUG BUILD'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'ca' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        '1 - Errors',
        'Error',
        'Global',
        'Local',
        'Manifest',
        'Manifests',
        'No',
        'Notes:',
        'Text'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'pt_PT' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Global',
        'Local'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'hr' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Manifest',
        'Status',
        'URL'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'es' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Error',
        'Global',
        'Local',
        'No',
        'URL'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'es-MX' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Error',
        'Global',
        'Local',
        'No',
        'URL',
        'Url'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'fr' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Ascendant',
        'Descendant',
        'Global',
        'Local',
        'Machine | Global',
        'Navigation',
        'Portable',
        'Source',
        'Sources',
        'Verbose',
        'Version',
        'installation',
        'option',
        'version {0}',
        '{0} minutes'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'nl' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        '1 week',
        'Filters',
        'Manifest',
        'Status',
        'Updates',
        'URL',
        'update',
        'website',
        '{0} status'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'sk' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Manifest',
        'Text'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'de' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Global',
        'Manifest',
        'Name',
        'Repository',
        'Start',
        'Status',
        'Text',
        'Updates',
        'URL',
        'Verbose',
        'Version',
        'Version:',
        'optional',
        '{package} Installation',
        '{package} Update'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'sv' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Global',
        'Manifest',
        'Start',
        'Status',
        'Text',
        'Version',
        'Version:',
        'installation',
        'version {0}',
        '{0} installation',
        '{0} status',
        '{pm} version:'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'id' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Global',
        'Grid',
        'Manifest',
        'Status',
        'URL',
        'Verbose',
        '{0} status'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'fil' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Android Subsystem',
        'Default',
        'Error',
        'Global',
        'Grid',
        'Machine | Global',
        'Manifest',
        'OK',
        'Ok',
        'Package',
        'Password',
        'Portable',
        'Portable mode',
        'PreRelease',
        'Repository',
        'Source',
        'Source:',
        'Telemetry',
        'Text',
        'URL',
        'UniGetUI',
        'UniGetUI - {0} {1}',
        'User',
        'Username',
        'Verbose',
        'website',
        'library'
    )) {
        return $true
    }

    if ($LanguageCode -ceq 'tl' -and $SourceValue -ceq $TargetValue -and $SourceValue -in @(
        'Machine | Global'
    )) {
        return $true
    }

    return $false
}
