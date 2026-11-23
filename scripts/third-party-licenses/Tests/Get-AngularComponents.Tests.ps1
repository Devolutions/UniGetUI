Describe 'Angular license list reader' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Get-AngularComponents.ps1')

        function New-App {
            param([Parameter(Mandatory)][string]$Name, [string[]]$Blocks)

            $app = Join-Path $TestDrive $Name
            $package = Join-Path $app 'node_modules' 'jquery'
            New-Item -ItemType Directory -Force -Path $package | Out-Null
            Set-Content -LiteralPath (Join-Path $package 'package.json') -Value '{ "version": "3.7.1", "repository": { "url": "git+https://github.com/jquery/jquery.git" } }'
            $list = Join-Path $app 'dist' '3rdpartylicenses.txt'
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $list) | Out-Null
            Set-Content -LiteralPath $list -Value ($Blocks -join ('-' * 80 + "`n")) -NoNewline
            return $list
        }
    }

    It 'takes the version and source from node_modules for a plain Angular list' {
        $list = New-App -Name 'angular' -Blocks @("Package: jquery`nLicense: MIT`n`nMIT License`n")

        $component = Get-AngularComponents -LicensesFile $list

        $component.Version | Should -Be '3.7.1'
        $component.Source | Should -Be 'https://github.com/jquery/jquery'
        $component.LicenseText | Should -Be 'MIT License'
    }

    It 'takes the version and source the list carries, over node_modules' {
        $list = New-App -Name 'bundle' -Blocks @("Package: jquery`nVersion: 3.6.0`nSource: https://github.com/jquery/jquery-dist`nLicense: MIT`n`nMIT License`n")

        $component = Get-AngularComponents -LicensesFile $list

        $component.Version | Should -Be '3.6.0'
        $component.Source | Should -Be 'https://github.com/jquery/jquery-dist'
        $component.License | Should -Be 'MIT'
    }

    It 'reads a list given as text without looking for a node_modules folder' {
        $list = New-App -Name 'unrelated' -Blocks @("Package: jquery`nLicense: MIT`n`nMIT License`n")

        $component = Get-AngularComponents -Text (Get-Content -LiteralPath $list -Raw)

        $component.Version | Should -Be ''
        $component.Source | Should -Be ''
    }

    It 'expands a listed npm shorthand source' {
        $list = New-App -Name 'shorthand' -Blocks @("Package: expose-loader`nVersion: 5.0.1`nSource: webpack-contrib/expose-loader`nLicense: MIT`n`nMIT License`n")

        (Get-AngularComponents -LicensesFile $list).Source | Should -Be 'https://github.com/webpack-contrib/expose-loader'
    }
}
