Describe 'LGPL notice' {
    BeforeAll {
        $toolDirectory = Split-Path -Parent $PSScriptRoot
        . (Join-Path $toolDirectory 'Private' 'Get-LgplNotice.ps1')
        . (Join-Path $toolDirectory 'Private' 'Resolve-LicenseText.ps1')
        . (Join-Path $toolDirectory 'Private' 'Get-FileVersionProperties.ps1')
        . (Join-Path $toolDirectory 'Private' 'Format-Notices.ps1')
        . (Join-Path $toolDirectory 'Private' 'ConvertTo-NoticesJson.ps1')
        . (Join-Path $toolDirectory 'Private' 'Compare-LicenseLock.ps1')
        $script:templatePath = Join-Path $toolDirectory 'templates' 'default.txt'

        function New-LgplComponent {
            param([string]$Name = 'SMBLibrary', [string]$Version = '1.5.7')

            [pscustomobject]@{
                Ecosystem   = 'nuget'
                Name        = $Name
                Version     = $Version
                License     = 'LGPL-3.0-or-later'
                Source      = "https://github.com/example/$Name"
                Copyright   = 'Copyright © Tal Aloni 2014-2026'
                LicenseText = 'GNU LESSER GENERAL PUBLIC LICENSE'
                Notice      = $null
                SharedTexts = @([pscustomobject]@{ Id = 'GPL-3.0'; Text = 'GNU GENERAL PUBLIC LICENSE' })
            }
        }
    }

    It 'words the license as <Expected> for <License>' -TestCases @(
        @{ License = 'LGPL-3.0-or-later'; Expected = 'version 3 or later' }
        @{ License = 'LGPL-3.0+'; Expected = 'version 3 or later' }
        @{ License = 'LGPL-3.0-only'; Expected = 'version 3' }
        @{ License = '(MIT AND LGPL-3.0-only)'; Expected = 'version 3' }
        @{ License = '(MIT OR Apache-2.0) AND LGPL-3.0-only'; Expected = 'version 3' }
        @{ License = '(MIT AND LGPL-3.0-only) OR (Apache-2.0 AND LGPL-3.0-or-later)'; Expected = 'version 3' }
        @{ License = '(MIT AND LGPL-3.0-or-later) OR (Apache-2.0 AND LGPL-3.0-only)'; Expected = 'version 3 or later' }
    ) {
        Get-LgplVersion -License $License | Should -Be $Expected
    }

    It 'gives no notice for <License>' -TestCases @(
        @{ License = 'LGPL-2.1-or-later' }
        @{ License = 'GPL-3.0-only' }
        @{ License = 'MIT' }
        @{ License = '(MIT OR LGPL-3.0-only)' }
        @{ License = 'MIT OR (Apache-2.0 AND LGPL-3.0-only)' }
    ) {
        Get-LgplVersion -License $License | Should -BeNullOrEmpty
    }

    It 'names the app, the GPL text and the source of the shipped version' {
        $notice = Get-LgplNotice -Component (New-LgplComponent) -AppName 'Remote Desktop Manager' -LicenseVersion 'version 3 or later' `
            -SourceUrl 'https://github.com/TalAloni/SMBLibrary/tree/v1.5.7' -GplLabel 'GPL-3.0'

        $notice -split "`n" | Should -Be @(
            'SMBLibrary 1.5.7 — Copyright © Tal Aloni 2014-2026'
            'Remote Desktop Manager uses SMBLibrary, which is licensed under the GNU Lesser General Public License, version 3 or later; SMBLibrary and its use in Remote Desktop Manager are covered by that license.'
            'The full texts of the GNU LGPL v3 and GNU GPL v3 are included in this file.'
            'GNU GPL v3: see [GPL-3.0]'
            'The source code of SMBLibrary 1.5.7 is available at https://github.com/TalAloni/SMBLibrary/tree/v1.5.7.'
        )
    }

    It 'neither claims nor points to the GPL text when it could not be read' {
        $notice = Get-LgplNotice -Component (New-LgplComponent) -AppName 'Devolutions Launcher' -LicenseVersion 'version 3 or later' -SourceUrl 'https://github.com/example/SMBLibrary'

        $notice | Should -Not -Match 'see \['
        $notice | Should -Not -Match 'GNU GPL v3 are included'
    }

    It 'prints the GPL text once, after the blocks, for every LGPL component' {
        $components = @((New-LgplComponent -Name 'Alpha'), (New-LgplComponent -Name 'Beta' -Version '2.0'))

        $notices = Format-Notices -TemplatePath $templatePath -AppName 'Remote Desktop Manager' -AppVersion '2027.1' -Components $components

        ([regex]::Matches($notices, 'GNU GENERAL PUBLIC LICENSE')).Count | Should -Be 1
        $notices | Should -Match "\[GPL-3\.0\]\nApplies to: Alpha 1\.5\.7,\n\s+Beta 2\.0\n\nGNU GENERAL PUBLIC LICENSE"
    }

    It 'keeps the GPL label when two NOTICEs hold the same text' {
        $notice = [pscustomobject]@{ Text = 'GNU GENERAL PUBLIC LICENSE' }
        $components = @(
            (New-LgplComponent -Name 'Alpha')
            [pscustomobject]@{ Ecosystem = 'nuget'; Name = 'Gamma'; Version = '1.0'; License = 'MIT'; Notice = $notice }
            [pscustomobject]@{ Ecosystem = 'nuget'; Name = 'Delta'; Version = '1.0'; License = 'MIT'; Notice = $notice }
        )

        $notices = Format-Notices -TemplatePath $templatePath -AppName 'Remote Desktop Manager' -AppVersion '2027.1' -Components $components

        ([regex]::Matches($notices, '(?m)^\[(GPL-3\.0|NOTICE-[0-9a-f]+)\]$')).Value | Should -Be '[GPL-3.0]'
        ([regex]::Matches($notices, 'Notice: see \[GPL-3\.0\]')).Count | Should -Be 2
    }

    It 'lists a single NOTICE that holds the GPL text under its label' {
        $components = @(
            (New-LgplComponent -Name 'Alpha')
            [pscustomobject]@{ Ecosystem = 'nuget'; Name = 'Gamma'; Version = '1.0'; License = 'MIT'; Notice = [pscustomobject]@{ Text = 'GNU GENERAL PUBLIC LICENSE' } }
        )

        $notices = Format-Notices -TemplatePath $templatePath -AppName 'Remote Desktop Manager' -AppVersion '2027.1' -Components $components

        $notices | Should -Match "\[GPL-3\.0\]\nApplies to: Gamma 1\.0,\n\s+Alpha 1\.5\.7\n"
    }

    It 'keeps the GPL text in the notices JSON, so a merged run prints it too' {
        $path = Join-Path $TestDrive 'THIRD-PARTY-NOTICES.json'
        Set-Content -LiteralPath $path -Value (ConvertTo-NoticesJson -AppName 'Remote Desktop Manager' -Components @(New-LgplComponent))

        $read = (Read-NoticesJson -Path $path).Components[0]

        $read.SharedTexts.Id | Should -Be 'GPL-3.0'
        $read.SharedTexts.Text | Should -Be 'GNU GENERAL PUBLIC LICENSE'
    }

    It 'reports a text change when only the GPL text changes' {
        $before = New-LgplComponent
        $after = New-LgplComponent
        $after.SharedTexts = @([pscustomobject]@{ Id = 'GPL-3.0'; Text = 'GNU GENERAL PUBLIC LICENSE, reworded' })

        $changes = Compare-LicenseLock -Current @(ConvertTo-LicenseLock -Components @($after)) -Previous @(ConvertTo-LicenseLock -Components @($before))

        @($changes.Kind) | Should -Be @('TextChanged')
    }
}
