Describe 'WebAssembly reader' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        foreach ($file in 'Get-DotnetDepsComponents.ps1', 'Resolve-LicenseText.ps1', 'Get-RepositoryNotice.ps1', 'Get-WebAssemblyComponents.ps1', 'Get-FileVersionProperties.ps1') {
            . (Join-Path $privateDirectory $file)
        }
    }

    It 'keeps the NOTICE a package ships, as the deps.json reader does' {
        $obj = Join-Path $TestDrive 'obj'
        $manifestDirectory = Join-Path $obj 'Release' 'net10.0-browser'
        $published = Join-Path $TestDrive 'publish'
        $package = Join-Path $TestDrive 'packages' 'example' '1.0.0'
        New-Item -ItemType Directory -Force -Path $manifestDirectory, (Join-Path $published '_framework'), $package | Out-Null
        @{
            libraries = @{ 'Example/1.0.0' = @{ type = 'package'; path = 'example/1.0.0' } }
            targets   = @{ 'net10.0-browser/browser-wasm' = @{ 'Example/1.0.0' = @{ runtime = @{ 'lib/net10.0/Example.dll' = @{} } } } }
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $obj 'project.assets.json')
        '<package><metadata><id>Example</id><version>1.0.0</version><license type="expression">MIT</license></metadata></package>' |
            Set-Content -LiteralPath (Join-Path $package 'example.nuspec')
        'Credits for the code Example carries.' | Set-Content -LiteralPath (Join-Path $package 'NOTICE')
        Set-Content -LiteralPath (Join-Path $published '_framework' 'Example.hash.wasm') -Value ''
        @{
            ManifestType = 'Publish'
            Assets       = @(@{ Identity = 'Example.wasm'; RelativePath = '_framework/Example#[.{fingerprint}]!.wasm'; Fingerprint = 'hash'; AssetRole = 'Primary'; AssetTraitName = 'WasmResource'; AssetTraitValue = 'runtime'; OriginalItemSpec = 'Example.wasm' })
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $manifestDirectory 'staticwebassets.publish.json')

        $component = Get-WebAssemblyComponents -ManifestPath (Join-Path $manifestDirectory 'staticwebassets.publish.json') `
            -BinariesPath $published -PackagesRoot (Join-Path $TestDrive 'packages') -UrlMappings @{ exact = @{}; contains = @{} }

        $component.PackageNotice.Text | Should -Be 'Credits for the code Example carries.'
    }
}
