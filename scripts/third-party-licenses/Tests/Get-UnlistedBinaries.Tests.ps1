Describe 'Binaries no deps.json lists' {
    BeforeAll {
        . (Join-Path (Split-Path -Parent $PSScriptRoot) 'Private' 'Get-DotnetDepsComponents.ps1')
        . (Join-Path (Split-Path -Parent $PSScriptRoot) 'Private' 'Get-FileVersionProperties.ps1')

        $script:directory = Join-Path $TestDrive 'bin'
        $null = New-Item -ItemType Directory -Path $script:directory
        foreach ($name in 'Devolutions.Kept.dll', 'Devolutions.Dropped.dll') {
            [IO.File]::WriteAllBytes((Join-Path $script:directory $name), [byte[]]@(0))
        }

        $script:deps = [pscustomobject]@{ Path = (Join-Path $script:directory 'TestApp.deps.json'); Target = @{} }
    }

    It 'keeps an excluded binary that an exception names' {
        $binaries = @(Get-UnlistedBinaries -Deps @($script:deps) -Directory $script:directory -Excludes @('Devolutions.*') -ExcludeExceptions @('Devolutions.Kept'))

        $binaries.Name | Should -Be 'Devolutions.Kept.dll'
    }
}
