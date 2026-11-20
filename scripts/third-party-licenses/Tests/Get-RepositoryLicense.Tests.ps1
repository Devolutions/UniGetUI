Describe 'Repository license finder' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Get-RepositoryNotice.ps1')

        Mock Invoke-GitHubApi { "text of $($Path -replace '^.*/contents/' -replace '\?.*$')" }
    }

    BeforeEach {
        $script:RepositoryFileCache = @{}
    }

    It 'reads an OFL.txt when the repository has no LICENSE file' {
        Mock Get-RepositoryRootFiles { [pscustomobject]@{ Ref = ''; Names = @('FONTLOG.txt', 'OFL.txt', 'README.md') } }

        $license = Get-RepositoryLicense -RepositoryUrl 'https://github.com/googlefonts/opensans'

        $license.File | Should -Be 'OFL.txt'
        $license.Text | Should -Be 'text of OFL.txt'
    }

    It 'keeps the LICENSE file when the repository has both, whatever their order' {
        Mock Get-RepositoryRootFiles { [pscustomobject]@{ Ref = ''; Names = @('OFL.txt', 'LICENSE') } }

        $license = Get-RepositoryLicense -RepositoryUrl 'https://github.com/example/font'

        $license.File | Should -Be 'LICENSE'
    }

    It 'finds nothing when no root file is a license' {
        Mock Get-RepositoryRootFiles { [pscustomobject]@{ Ref = ''; Names = @('FONTLOG.txt', 'README.md', 'OFL-FAQ.txt') } }

        Get-RepositoryLicense -RepositoryUrl 'https://github.com/example/font' | Should -BeNullOrEmpty
    }
}
