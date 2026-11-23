Describe 'Copyright lines of a license text' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Resolve-LicenseText.ps1')
        . (Join-Path $privateDirectory 'Get-FileVersionProperties.ps1')
    }

    It 'skips the copyright of the GNU licence document itself' {
        $text = @(
            '                   GNU LESSER GENERAL PUBLIC LICENSE'
            '                       Version 3, 29 June 2007'
            ''
            ' Copyright (C) 2007 Free Software Foundation, Inc. <https://fsf.org/>'
            ' Everyone is permitted to copy and distribute verbatim copies'
            ' of this license document, but changing it is not allowed.'
        ) -join "`n"

        Get-CopyrightLines -LicenseText $text | Should -BeNullOrEmpty
    }

    It 'skips it when an address line and a blank line come before the permission, as in the SPDX GPL 2.0 text' {
        $text = @(
            'Copyright (C) 1989, 1991 Free Software Foundation, Inc.'
            '51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA'
            ''
            'Everyone is permitted to copy and distribute verbatim copies of this license document, but changing it is not allowed.'
        ) -join "`n"

        Get-CopyrightLines -LicenseText $text | Should -BeNullOrEmpty
    }

    It 'keeps the copyright of the component' {
        $text = @(
            'MIT License'
            ''
            'Copyright (c) 2014 Tal Aloni'
            ''
            'Permission is hereby granted, free of charge, to any person obtaining a copy'
        ) -join "`n"

        Get-CopyrightLines -LicenseText $text | Should -Be 'Copyright (c) 2014 Tal Aloni'
    }

    It 'reads a line behind // comment marks, as ANGLE''s LICENSE writes it' {
        $text = "// Copyright 2018 The ANGLE Project Authors.`n// All rights reserved.`n//`n// Redistribution and use in source and binary forms"

        Get-CopyrightLines -LicenseText $text | Should -Be 'Copyright 2018 The ANGLE Project Authors.'
    }
}

Describe 'Alternatives of a licence expression' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Resolve-LicenseText.ps1')
        . (Join-Path $privateDirectory 'Get-FileVersionProperties.ps1')
        function Get-SpdxLicenseText { param($Id) @{ 'MIT' = 'MIT TEXT'; 'Apache-2.0' = 'APACHE TEXT'; 'LGPL-3.0-only' = 'LGPL TEXT' }[$Id] }
    }

    It 'reads <License> with its parentheses' -TestCases @(
        @{ License = 'MIT or Apache-2.0'; Expected = @('MIT', 'Apache-2.0') }
        @{ License = '(MIT OR Apache-2.0) AND LGPL-3.0-only'; Expected = @('MIT AND LGPL-3.0-only', 'Apache-2.0 AND LGPL-3.0-only') }
        @{ License = 'MIT OR (Apache-2.0 AND (BSD-2-Clause OR BSD-3-Clause))'; Expected = @('MIT', 'Apache-2.0 AND BSD-2-Clause', 'Apache-2.0 AND BSD-3-Clause') }
        @{ License = 'GPL-2.0-only WITH Classpath-exception-2.0'; Expected = @('GPL-2.0-only WITH Classpath-exception-2.0') }
        @{ License = '(MIT'; Expected = @('MIT') }
    ) {
        @(Get-LicenseAlternatives -License $License) | Should -Be $Expected
    }

    It 'prints every licence an alternative needs when the choice is inside an AND' {
        Get-StandardLicenseText -License '(MIT OR Apache-2.0) AND LGPL-3.0-only' | Should -Be "MIT TEXT`n`nLGPL TEXT"
    }

    It 'prints the other licence when the LGPL-3.0 is one of the choices, whatever their order' {
        Get-StandardLicenseText -License 'LGPL-3.0-only OR MIT' | Should -Be 'MIT TEXT'
    }

    It 'prints the first choice whose texts can all be read' {
        $license = '(LicenseRef-NoSpdx AND LGPL-3.0-or-later) OR (MIT AND LGPL-3.0-only)'

        Get-PrintedLicenseAlternative -License $license | Should -Be 'MIT AND LGPL-3.0-only'
        Get-StandardLicenseText -License $license | Should -Be "MIT TEXT`n`nLGPL TEXT"
    }

    It 'prints the LGPL-3.0 when the other licence of the choice has no standard text' {
        Get-PrintedLicenseAlternative -License 'LGPL-3.0-only OR LicenseRef-Commercial' | Should -Be 'LGPL-3.0-only'
        Get-StandardLicenseText -License 'LGPL-3.0-only OR LicenseRef-Commercial' | Should -Be 'LGPL TEXT'
    }
}

Describe 'Template copyright line of a standard text' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Resolve-LicenseText.ps1')
        . (Join-Path $privateDirectory 'Get-FileVersionProperties.ps1')
        $script:mit = "MIT License`n`nCopyright (c) <year> <copyright holders>`n`nPermission is hereby granted"
    }

    It 'puts the holder in place of the template line' {
        Update-TemplateCopyrightLine -LicenseText $mit -Copyright 'Copyright (c) phyyl' |
            Should -Be "MIT License`n`nCopyright (c) phyyl`n`nPermission is hereby granted"
    }

    It 'removes the template line, and the blank line it leaves, when no holder is known' {
        Update-TemplateCopyrightLine -LicenseText $mit -Copyright '' | Should -Be "MIT License`n`nPermission is hereby granted"
    }

    It 'removes it from a NOTICE that lists a component above it, keeping that component''s own lines' {
        $notice = "- Microsoft.Azure.KeyVault 3.0.5 - MIT`n`n- (c) Microsoft Corporation.`n`n$mit"

        Update-TemplateCopyrightLine -LicenseText $notice |
            Should -Be "- Microsoft.Azure.KeyVault 3.0.5 - MIT`n`n- (c) Microsoft Corporation.`n`nMIT License`n`nPermission is hereby granted"
    }

    It 'treats "All rights reserved" after the placeholders as no holder' {
        Update-TemplateCopyrightLine -LicenseText "Notice`n`nCopyright (c) <year> <owner> . All rights reserved.`n`nText" | Should -Be "Notice`n`nText"
    }

    It 'keeps lines that name a holder or belong to the licence itself' {
        $text = "Copyright 2006 Alp Toker <alp@atoker.com>`n`n   Copyright [yyyy] [name of copyright owner]"

        Update-TemplateCopyrightLine -LicenseText $text -Copyright 'Copyright (c) Someone' | Should -Be $text
    }

    It 'keeps a line whose only holder is an email address' {
        $text = "MIT License`n`nCopyright (c) 2026 <maintainer@example.com>`n`nPermission is hereby granted"

        Update-TemplateCopyrightLine -LicenseText $text | Should -Be $text
        Update-TemplateCopyrightLine -LicenseText $text -Copyright 'Copyright (c) Someone' | Should -Be $text
    }
}

Describe 'Template copyright line of the text a package or its repository gives' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Resolve-LicenseText.ps1')
        . (Join-Path $privateDirectory 'Get-FileVersionProperties.ps1')
    }

    It 'puts the holder in place of a square-bracket template line' {
        Update-TemplateCopyrightLine -LicenseText "MIT License`n`nCopyright (c) [year] [fullname]`n`nPermission is hereby granted" -Copyright '© 2026 Jared Goodwin' |
            Should -Be "MIT License`n`n© 2026 Jared Goodwin`n`nPermission is hereby granted"
    }

    It 'gives an edited CRLF text one kind of line ending' {
        Update-TemplateCopyrightLine -LicenseText "MIT License`r`n`r`nCopyright (c) <year> <copyright holders>`r`n`r`nPermission" -Copyright "Copyright (c) A`r`nCopyright (c) B" |
            Should -Be "MIT License`n`nCopyright (c) A`nCopyright (c) B`n`nPermission"
    }

    It 'keeps the Apache-2.0 appendix line in braces and the GPL How-to-Apply line' {
        $text = "   Copyright {yyyy} {name of copyright owner}`n`n    Copyright (C) <year>  <name of author>"

        Update-TemplateCopyrightLine -LicenseText $text -Copyright 'Copyright (c) Someone' | Should -Be $text
    }

    It 'returns a text with no template line as it was read' {
        Update-TemplateCopyrightLine -LicenseText "`nMIT License`n`nCopyright (c) 2026 Someone`n" -Copyright 'Copyright (c) Someone' |
            Should -Be "`nMIT License`n`nCopyright (c) 2026 Someone`n"
    }
}

Describe 'NOTICE file a package ships' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Resolve-LicenseText.ps1')
        . (Join-Path $privateDirectory 'Get-FileVersionProperties.ps1')
        . (Join-Path $privateDirectory 'Get-RepositoryNotice.ps1')
    }

    It 'is read from the package root' {
        $package = Join-Path $TestDrive 'avalonia.controls.markdown' '12.3.1'
        $null = New-Item -ItemType Directory -Path $package
        Set-Content -LiteralPath (Join-Path $package 'sample.nuspec') -Value '<package><metadata><copyright>Copyright AvaloniaUI</copyright></metadata></package>'
        Set-Content -LiteralPath (Join-Path $package 'NOTICE.md') -Value "# NOTICE`n`nColorCode.Core"
        $component = [pscustomobject]@{ PackagePath = 'avalonia.controls.markdown/12.3.1' }

        $notice = (Resolve-NuGetMetadata -Component $component -PackagesRoot $TestDrive -UrlMappings @{ exact = @{}; contains = @{} }).PackageNotice

        $notice.File | Should -Be 'NOTICE.md'
        $notice.Text | Should -Be "# NOTICE`n`nColorCode.Core"
    }

    It 'is left out when the file is empty' {
        $package = Join-Path $TestDrive 'empty.notice' '1.0.0'
        $null = New-Item -ItemType Directory -Path $package
        Set-Content -LiteralPath (Join-Path $package 'sample.nuspec') -Value '<package><metadata><copyright>Copyright Someone</copyright></metadata></package>'
        $null = New-Item -ItemType File -Path (Join-Path $package 'NOTICE')
        $component = [pscustomobject]@{ PackagePath = 'empty.notice/1.0.0' }

        (Resolve-NuGetMetadata -Component $component -PackagesRoot $TestDrive -UrlMappings @{ exact = @{}; contains = @{} }).PackageNotice | Should -BeNullOrEmpty
    }

    It 'comes before the repository NOTICE, which is kept when it differs' {
        $package = [pscustomobject]@{ Repository = ''; File = 'NOTICE.md'; Url = ''; Text = 'From the package.' }
        $repository = [pscustomobject]@{ Repository = 'owner/repo'; File = 'NOTICE'; Url = 'https://github.com/owner/repo/blob/HEAD/NOTICE'; Text = 'From the repository.' }

        (Join-ComponentNotice -PackageNotice $package -RepositoryNotice $repository).Text | Should -Be "From the package.`n`nFrom the repository."
        (Join-ComponentNotice -PackageNotice $package -RepositoryNotice $package).Text | Should -Be 'From the package.'
        Join-ComponentNotice -PackageNotice $null -RepositoryNotice $repository | Should -Be $repository
    }

    It 'prints the repository NOTICE once when it differs from the package''s only by line endings and surrounding blank lines' {
        $package = [pscustomobject]@{ Repository = ''; File = 'NOTICE'; Url = ''; Text = "Line one`r`nLine two" }
        $repository = [pscustomobject]@{ Repository = 'owner/repo'; File = 'NOTICE'; Url = 'https://github.com/owner/repo/blob/HEAD/NOTICE'; Text = "Line one`nLine two`n" }

        (Join-ComponentNotice -PackageNotice $package -RepositoryNotice $repository).Text | Should -Be "Line one`r`nLine two"
    }
}

Describe 'Licence file a package ships' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Resolve-LicenseText.ps1')
        . (Join-Path $privateDirectory 'Get-FileVersionProperties.ps1')
        . (Join-Path $privateDirectory 'Get-RepositoryNotice.ps1')
    }

    It 'reads the licence an older Microsoft package ships as dotnet_library_license.txt' {
        $package = Join-Path $TestDrive 'runtime.win-x64.runtime.native.system.data.sqlclient.sni' '4.4.0'
        $null = New-Item -ItemType Directory -Path $package
        Set-Content -LiteralPath (Join-Path $package 'sample.nuspec') -Value '<package><metadata><licenseUrl>http://go.microsoft.com/fwlink/?LinkId=329770</licenseUrl></metadata></package>'
        Set-Content -LiteralPath (Join-Path $package 'dotnet_library_license.txt') -Value 'MICROSOFT SOFTWARE LICENSE TERMS'
        $component = [pscustomobject]@{ PackagePath = 'runtime.win-x64.runtime.native.system.data.sqlclient.sni/4.4.0' }

        $metadata = Resolve-NuGetMetadata -Component $component -PackagesRoot $TestDrive -UrlMappings @{ exact = @{}; contains = @{} }

        # The file's final line break may be kept or trimmed by the reader; approvals ignore it either way
        $metadata.LicenseText.Trim() | Should -Be 'MICROSOFT SOFTWARE LICENSE TERMS'
        $metadata.LicenseTextOrigin | Should -Be 'package'
    }
}

Describe 'Copyright of a package without one in its nuspec' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Resolve-LicenseText.ps1')
        . (Join-Path $privateDirectory 'Get-FileVersionProperties.ps1')
    }

    It 'reads the main assembly, not a satellite that comes first' {
        $package = Join-Path $TestDrive 'package'
        $null = New-Item -ItemType Directory -Path (Join-Path $package 'lib' 'net40' 'de')
        # Any two assemblies with different copyrights: pwsh ships both on every platform
        Copy-Item (Join-Path $PSHOME 'Markdig.Signed.dll') (Join-Path $package 'lib' 'net40' 'de' 'Sample.resources.dll')
        Copy-Item (Join-Path $PSHOME 'System.Management.Automation.dll') (Join-Path $package 'lib' 'net40' 'Sample.dll')
        $expected = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $PSHOME 'System.Management.Automation.dll')).LegalCopyright.Trim()

        Get-AssemblyCopyright -PackageDirectory $package | Should -Be $expected
    }
}
