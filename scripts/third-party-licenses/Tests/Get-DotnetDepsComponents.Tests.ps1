Describe 'Shipped .NET packages' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Get-DotnetDepsComponents.ps1')
    }

    # The PowerShell module removes runtimes/win-x86 but keeps runtimes/win-x64, which has an sni.dll too
    It 'leaves out a package whose runtime folder the build removed, even when another runtime keeps the file name' {
        $app = Join-Path $TestDrive 'app'
        New-Item -ItemType Directory -Path (Join-Path $app 'runtimes/win-x64/native'), (Join-Path $app 'runtimes/win/lib/net8.0') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $app 'runtimes/win-x64/native/sni.dll') -Value ''
        Set-Content -LiteralPath (Join-Path $app 'runtimes/win/lib/net8.0/Example.Data.dll') -Value ''
        Set-Content -LiteralPath (Join-Path $app 'Example.Data.dll') -Value ''
        $deps = [pscustomobject]@{
            Libraries = @{
                'sni.win-x64/4.4.0' = @{ type = 'package' }
                'sni.win-x86/4.4.0' = @{ type = 'package' }
                'Example.Data/1.0.0' = @{ type = 'package' }
                'Example.Unix/1.0.0' = @{ type = 'package' }
            }
            Target = @{
                'sni.win-x64/4.4.0' = @{ runtimeTargets = @{ 'runtimes/win-x64/native/sni.dll' = @{ rid = 'win-x64'; assetType = 'native' } } }
                'sni.win-x86/4.4.0' = @{ runtimeTargets = @{ 'runtimes/win-x86/native/sni.dll' = @{ rid = 'win-x86'; assetType = 'native' } } }
                # The same file name in lib/ and in runtimes/: shipped through lib/ even with its runtime folder gone
                'Example.Data/1.0.0' = @{
                    runtime = @{ 'lib/net8.0/Example.Data.dll' = @{} }
                    runtimeTargets = @{ 'runtimes/unix/lib/net8.0/Example.Data.dll' = @{ rid = 'unix'; assetType = 'runtime' } }
                }
                'Example.Unix/1.0.0' = @{ runtimeTargets = @{ 'runtimes/unix/lib/net8.0/Example.Unix.dll' = @{ rid = 'unix'; assetType = 'runtime' } } }
            }
        }
        $shipped = Get-ShippedFiles -Directory $app

        $components = @(Get-DotnetDepsComponents -Deps $deps | Where-Object { Test-ComponentShipped -Component $_ -Shipped $shipped -Directory $app })

        @($components.Name | Sort-Object) | Should -Be @('Example.Data', 'sni.win-x64')
    }
}
