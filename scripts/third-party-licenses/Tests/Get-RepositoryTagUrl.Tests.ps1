Describe 'Source of the shipped version' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Get-RepositoryNotice.ps1')

        function New-HttpError {
            param([Net.HttpStatusCode]$Status)

            [Microsoft.PowerShell.Commands.HttpResponseException]::new($Status.ToString(), [Net.Http.HttpResponseMessage]::new($Status))
        }
    }

    BeforeEach {
        $script:RepositoryFileCache = @{}
    }

    It 'links the v-prefixed release tag' {
        Mock Invoke-GitHubApi { if ($Path -like '*/tags/v1.5.7') { @{ ref = 'refs/tags/v1.5.7' } } else { throw (New-HttpError NotFound) } }

        Get-RepositoryTagUrl -RepositoryUrl 'https://github.com/TalAloni/SMBLibrary' -Version '1.5.7' | Should -Be 'https://github.com/TalAloni/SMBLibrary/tree/v1.5.7'
    }

    It 'links the bare version tag' {
        Mock Invoke-GitHubApi { if ($Path -like '*/tags/2.0.0') { @{ ref = 'refs/tags/2.0.0' } } else { throw (New-HttpError NotFound) } }

        Get-RepositoryTagUrl -RepositoryUrl 'https://github.com/example/lib' -Version '2.0.0' | Should -Be 'https://github.com/example/lib/tree/2.0.0'
    }

    It 'links the repository when no tag matches, and remembers it' {
        Mock Invoke-GitHubApi { throw (New-HttpError NotFound) }

        Get-RepositoryTagUrl -RepositoryUrl 'https://github.com/example/lib' -Version '3.0' | Should -Be 'https://github.com/example/lib'
        Get-RepositoryTagUrl -RepositoryUrl 'https://github.com/example/lib' -Version '3.0' | Should -Be 'https://github.com/example/lib'
        Should -Invoke Invoke-GitHubApi -Times 2 -Exactly
    }

    It 'lets a failed call through, and does not remember it' {
        Mock Invoke-GitHubApi { throw (New-HttpError Forbidden) }

        { Get-RepositoryTagUrl -RepositoryUrl 'https://github.com/example/lib' -Version '3.0' } | Should -Throw
        $script:RepositoryFileCache.Count | Should -Be 0
    }

    It 'keeps a source that is not on GitHub' {
        Get-RepositoryTagUrl -RepositoryUrl 'https://example.com/lib' -Version '1.0' | Should -Be 'https://example.com/lib'
    }
}
