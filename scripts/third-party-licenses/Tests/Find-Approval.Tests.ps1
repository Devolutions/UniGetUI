Describe 'Approved packages' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Find-Approval.ps1')

        $script:approvals = @{
            'SMBLibrary' = @{ license = 'LGPL-3.0-or-later'; apps = @('Remote Desktop Manager', 'Devolutions Launcher') }
            'Acme.*'     = @{ license = 'MPL-2.0' }
            'Split.Lib'  = @(@{ license = 'MIT'; apps = @('Remote Desktop Manager') }, @{ license = 'Apache-2.0'; apps = @('Devolutions Console') })
        }
    }

    It 'takes the approval of a list that covers <AppName>' -ForEach @(
        @{ AppName = 'Remote Desktop Manager'; License = 'MIT' }
        @{ AppName = 'Devolutions Console'; License = 'Apache-2.0' }
    ) {
        (Find-Approval -Name 'Split.Lib' -Approvals $approvals -AppName $AppName).license | Should -Be $License
    }

    It 'covers no app that no approval of a list lists' {
        Find-Approval -Name 'Split.Lib' -Approvals $approvals -AppName 'Devolutions Server' | Should -BeNullOrEmpty
    }

    It 'covers an app the approval lists' {
        (Find-Approval -Name 'SMBLibrary' -Approvals $approvals -AppName 'Devolutions Launcher').license | Should -Be 'LGPL-3.0-or-later'
    }

    It 'does not cover an app the approval does not list' {
        Find-Approval -Name 'SMBLibrary' -Approvals $approvals -AppName 'Devolutions Server' | Should -BeNullOrEmpty
    }

    It 'covers every app when the approval lists none' {
        (Find-Approval -Name 'Acme.Widgets' -Approvals $approvals -AppName 'Devolutions Server').license | Should -Be 'MPL-2.0'
    }
}

Describe 'Hash of an approved licence text' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Find-Approval.ps1')
    }

    It 'does not change with the line endings or the whitespace around the text' {
        Get-LicenseTextHash -Text "`r`n   MIT License`r`n`r`nCopyright (c) Acme`r`n" | Should -Be (Get-LicenseTextHash -Text "MIT License`n`nCopyright (c) Acme")
    }

    It 'changes when the text inside changes' {
        Get-LicenseTextHash -Text "MIT License`nCopyright (c) Acme" | Should -Not -Be (Get-LicenseTextHash -Text "MIT License`n`nCopyright (c) Acme")
    }

    It 'keeps the empty text hash that approvals of a notice without a licence text hold' {
        Get-LicenseTextHash -Text '' | Should -Be 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'
    }
}
