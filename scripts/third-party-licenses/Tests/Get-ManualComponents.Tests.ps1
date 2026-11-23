Describe 'Manual component website' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Get-ManualComponents.ps1')
        . (Join-Path $privateDirectory 'Get-FileVersionProperties.ps1')
        . (Join-Path $privateDirectory 'ConvertTo-NoticesJson.ps1')
        . (Join-Path $privateDirectory 'Format-Notices.ps1')
    }

    It 'is the download link, while the source stays the repository' {
        $path = Join-Path $TestDrive 'manual-third-party-components.json'
        Set-Content -LiteralPath $path -Value '{ "components": [ { "name": "Public Suffix List", "version": "2026-01-01", "license": "MPL-2.0", "source": "https://github.com/publicsuffix/list", "website": "https://publicsuffix.org/" } ] }'

        $component = Get-ManualComponents -Path $path

        $component.Source | Should -Be 'https://github.com/publicsuffix/list'
        Get-ComponentDownload -Component $component | Should -Be 'https://publicsuffix.org/'
    }

    It 'falls back to the source when there is none' {
        $component = [pscustomobject]@{ Ecosystem = 'manual'; Name = 'jsbn'; Version = '1.1'; Source = 'http://www-cs-students.stanford.edu/~tjw/jsbn/'; Website = '' }

        Get-ComponentDownload -Component $component | Should -Be 'http://www-cs-students.stanford.edu/~tjw/jsbn/'
    }
}
