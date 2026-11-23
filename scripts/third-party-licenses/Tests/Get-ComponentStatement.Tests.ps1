Describe 'Notice files an override attaches' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Get-ComponentStatement.ps1')
        $script:resolve = { param($path) $full = Join-Path $TestDrive $path; if (Test-Path -LiteralPath $full) { $full } }
        $null = New-Item -ItemType File -Path (Join-Path $TestDrive 'empty.txt')
    }

    It 'prints each file under its project name, in the listed order' {
        Set-Content -LiteralPath (Join-Path $TestDrive 'zlib.txt') -Value 'Copyright (C) 1995 Jean-loup Gailly'
        Set-Content -LiteralPath (Join-Path $TestDrive 'freetype.txt') -Value 'The FreeType Project LICENSE'
        $notices = @(@{ name = 'zlib'; path = 'zlib.txt' }, @{ name = 'FreeType'; path = 'freetype.txt' })

        (Get-OverrideNotice -Notices $notices -ResolvePath $resolve).Text |
            Should -Be "zlib:`nCopyright (C) 1995 Jean-loup Gailly`n`nFreeType:`nThe FreeType Project LICENSE"
    }

    It 'skips <File> with a warning, so the notices are still written' -ForEach @(
        @{ File = 'missing.txt' }
        @{ File = 'empty.txt' }
    ) {
        $notices = @(@{ name = 'ICU'; path = $File }, @{ name = 'zlib'; path = 'zlib.txt' })

        (Get-OverrideNotice -Notices $notices -ResolvePath $resolve -WarningVariable warnings -WarningAction SilentlyContinue).Text |
            Should -Be "zlib:`nCopyright (C) 1995 Jean-loup Gailly"
        $warnings | Should -BeLike "*$File*"
    }

    It 'gives nothing when the override lists no notice' {
        Get-OverrideNotice -Notices $null -ResolvePath $resolve | Should -BeNullOrEmpty
    }
}

Describe 'Statement legal requires for a component' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Get-ComponentStatement.ps1')
        . (Join-Path $privateDirectory 'Compare-LicenseLock.ps1')

        $script:override = @{ statement = @{ apps = @('Remote Desktop Manager', 'Devolutions Launcher'); signedOnly = $true; text = 'Ships unmodified.' } }
    }

    It 'is printed for a listed app when the package is signed' {
        Get-ComponentStatement -Override $override -AppName 'Devolutions Launcher' -Signed | Should -Be 'Ships unmodified.'
    }

    It 'is left out when the package is not signed' {
        Get-ComponentStatement -Override $override -AppName 'Remote Desktop Manager' | Should -BeNullOrEmpty
    }

    It 'is left out for an app it does not list' {
        Get-ComponentStatement -Override $override -AppName 'Devolutions Server' -Signed | Should -BeNullOrEmpty
    }

    It 'is printed unsigned when it does not ask for a signature' {
        $unsigned = @{ statement = @{ apps = @('Remote Desktop Manager'); text = 'Always.' } }

        Get-ComponentStatement -Override $unsigned -AppName 'Remote Desktop Manager' | Should -Be 'Always.'
    }

    It 'names the app whose notices print it' {
        $named = @{ statement = @{ apps = @('Remote Desktop Manager', 'Devolutions Launcher'); text = '{{app_name}} loads a replacement.' } }

        Get-ComponentStatement -Override $named -AppName 'Devolutions Launcher' | Should -Be 'Devolutions Launcher loads a replacement.'
        Get-ComponentStatement -Override $named -AppName 'Remote Desktop Manager' | Should -Be 'Remote Desktop Manager loads a replacement.'
    }

    It 'gives nothing for a component with no statement' {
        Get-ComponentStatement -Override @{ license = 'MIT' } -AppName 'Remote Desktop Manager' -Signed | Should -BeNullOrEmpty
    }

    It 'is reported by the lock when it changes on a component with no licence text' {
        $component = { param($text) [pscustomobject]@{ Name = 'ActiproSoftware.Controls.Avalonia'; Ecosystem = 'nuget'; Version = '25.1.1'; License = 'LicenseRef-Actipro-EULA'; Copyright = 'Actipro Software LLC'; LicenseText = ''; Notice = [pscustomobject]@{ Text = $text } } }

        $changes = Compare-LicenseLock -Current @(ConvertTo-LicenseLock -Components @(& $component 'Reworded.')) -Previous @(ConvertTo-LicenseLock -Components @(& $component 'Ships unmodified.'))

        @($changes.Kind) | Should -Be @('TextChanged')
    }
}

Describe 'Fields an override gives a component' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Get-ComponentStatement.ps1')

        $script:override = @{ license = 'MIT'; copyright = 'Copyright (C) 2004 Jonathan Pryor'; source = 'https://github.com/mono/mono' }
    }

    It 'replaces a licence given only as an address, and the holder with it' {
        $component = [pscustomobject]@{ License = 'https://go.microsoft.com/fwlink/?linkid=869050'; Source = ''; Website = ''; Copyright = 'Microsoft Corporation' }

        Set-OverrideFields -Component $component -Override $override

        $component.License | Should -Be 'MIT'
        $component.Copyright | Should -Be 'Copyright (C) 2004 Jonathan Pryor'
        $component.Source | Should -Be 'https://github.com/mono/mono'
    }

    It 'replaces a licence shipped as a file, keeping the holder when the override gives none' {
        $component = [pscustomobject]@{ License = 'Custom'; Source = ''; Website = ''; Copyright = 'Actipro Software LLC' }

        Set-OverrideFields -Component $component -Override @{ license = 'LicenseRef-Actipro-EULA' }

        $component.License | Should -Be 'LicenseRef-Actipro-EULA'
        $component.Copyright | Should -Be 'Actipro Software LLC'
    }

    It 'keeps the holder of a licence shipped as a file, even when the override gives one' {
        $component = [pscustomobject]@{ License = 'Custom'; Source = ''; Website = ''; Copyright = 'Actipro Software LLC' }

        Set-OverrideFields -Component $component -Override @{ license = 'LicenseRef-Actipro-EULA'; copyright = 'Copyright (c) Someone Else' }

        $component.License | Should -Be 'LicenseRef-Actipro-EULA'
        $component.Copyright | Should -Be 'Actipro Software LLC'
    }

    It 'only fills an empty licence, so the holder the binary gives stays' {
        $component = [pscustomobject]@{ License = ''; Source = ''; Website = ''; Copyright = 'Copyright (c) gsudo' }

        Set-OverrideFields -Component $component -Override $override

        $component.License | Should -Be 'MIT'
        $component.Copyright | Should -Be 'Copyright (c) gsudo'
    }

    It 'keeps the licence and holder a package declares' {
        $component = [pscustomobject]@{ License = 'Apache-2.0'; Source = 'https://github.com/owner/repo'; Website = ''; Copyright = 'Copyright Owner' }

        Set-OverrideFields -Component $component -Override $override

        $component.License | Should -Be 'Apache-2.0'
        $component.Copyright | Should -Be 'Copyright Owner'
        $component.Source | Should -Be 'https://github.com/owner/repo'
    }

    It 'leaves out the licence file a package ships when asked, and keeps the rest' {
        $component = [pscustomobject]@{ License = 'Custom'; Source = ''; Website = ''; Copyright = 'Copyright (c) Actipro Software LLC'; LicenseText = 'END-USER LICENSE AGREEMENT'; LicenseTextOrigin = 'package' }

        Set-OverrideFields -Component $component -Override @{ license = 'LicenseRef-Actipro-EULA'; omitLicenseText = $true }

        $component.LicenseText | Should -BeNullOrEmpty
        $component.LicenseTextOrigin | Should -Be 'omitted'
        $component.License | Should -Be 'LicenseRef-Actipro-EULA'
        $component.Copyright | Should -Be 'Copyright (c) Actipro Software LLC'
    }

    It 'keeps the licence file without the field' {
        $component = [pscustomobject]@{ License = 'MIT'; Source = ''; Website = ''; Copyright = ''; LicenseText = 'MIT License'; LicenseTextOrigin = 'package' }

        Set-OverrideFields -Component $component -Override @{ license = 'MIT' }

        $component.LicenseText | Should -Be 'MIT License'
        $component.LicenseTextOrigin | Should -Be 'package'
    }
}
