BeforeAll {
    $toolRoot = $PSScriptRoot
    Get-ChildItem (Join-Path $toolRoot 'Private') -Filter '*.ps1' | ForEach-Object { . $_.FullName }
}

Describe 'Webpack attribution reader' {
    It 'WhenReadingWebpackInventoryThenPreservesPackageSpecificLicenseAndNotice' {
        $path = Join-Path $TestDrive 'webpack-components.json'
        $text = "Copyright (c) Example`r`nPermission text`r`n"
        @{
            format = 'devolutions-webpack-components/1'
            components = @(@{
                name = '@example/library'; version = '1.2.3'; license = 'MIT'
                source = 'github:example/library'; licenseText = $text; noticeText = "Notice`r`n"
            })
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path

        $component = Get-WebpackComponents -Path $path

        $component.LicenseText | Should -BeExactly $text
        $component.Notice.Text | Should -BeExactly "Notice`r`n"
        $component.Source | Should -Be 'https://github.com/example/library'
    }

    It 'WhenWebpackInventoryHasWrongFormatThenRejectsIt' {
        $path = Join-Path $TestDrive 'invalid.json'
        '{"format":"unknown","components":[]}' | Set-Content -LiteralPath $path

        { Get-WebpackComponents -Path $path } | Should -Throw '*not a webpack component inventory*'
    }
}

Describe 'Shipped dependency inventory' {
    BeforeEach {
        $deps = [pscustomobject]@{
            Libraries = @{
                'ThirdParty/1.0.0' = @{ type = 'package'; path = 'thirdparty/1.0.0' }
                'BuildTool/1.0.0' = @{ type = 'package' }
                'OurApplication/1.0.0' = @{ type = 'project' }
                'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.1' = @{ type = 'runtimepack' }
            }

            Target = @{
                'ThirdParty/1.0.0' = @{ runtime = @{ 'lib\net10.0\ThirdParty.dll' = @{} } }
                'BuildTool/1.0.0' = @{ compile = @{ 'BuildTool.dll' = @{} } }
                'OurApplication/1.0.0' = @{ runtime = @{ 'OurApplication.dll' = @{} } }
                'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.1' = @{ runtime = @{ 'System.Private.CoreLib.dll' = @{} } }
            }
        }
    }

    It 'WhenCollectingNoticesThenExcludesProjectsAndBuildOnlyPackages' {
        $components = @(Get-DotnetDepsComponents -Deps $deps)

        $components.Name | Should -Contain 'ThirdParty'
        $components.Name | Should -Not -Contain 'BuildTool'
        $components.Name | Should -Not -Contain 'OurApplication'
        ($components | Where-Object IsRuntime).Version | Should -Be '10.0.1'
    }

    It 'WhenCollectingSbomThenIncludesFirstPartyAssembliesAndRuntime' {
        $components = @(Get-DotnetDepsComponents -Deps $deps -IncludeProjects)
        $json = ConvertTo-CycloneDxJson -AppName 'Our Application' -AppVersion '1.0.0' -Components $components | ConvertFrom-Json

        $json.bomFormat | Should -Be 'CycloneDX'
        $json.components.name | Should -Contain 'OurApplication'
        $json.components.name | Should -Contain 'Microsoft.NETCore.App'
        $json.components.name | Should -Not -Contain 'BuildTool'
    }

    It 'WhenMergingJobsThenRetainsExcludedComponentsInSbomInventory' {
        $inventory = @(Get-ComponentInventory -Components (Get-DotnetDepsComponents -Deps $deps -IncludeProjects))
        $path = Join-Path $TestDrive 'THIRD-PARTY-NOTICES.json'
        ConvertTo-NoticesJson -AppName 'Application' -AppVersion '1.0.0' -Components @() -Inventory $inventory |
            Set-Content -LiteralPath $path

        $document = Read-NoticesJson -Path $path

        $document.Components | Should -BeNullOrEmpty
        $document.Inventory.name | Should -Contain 'OurApplication'
        ($document.Inventory | Where-Object name -eq 'OurApplication').isFirstParty | Should -BeTrue
    }
}

Describe 'PowerShell module attribution' {
    It 'WhenRootModuleUsesDynamicExpressionsThenReadsOnlyStaticMetadataWithoutExecutingIt' {
        $path = Join-Path $TestDrive 'Module.psd1'
        @'
# Licensed under the MIT License.
@{
    RootModule = if ($PSEdition -eq 'Core') { throw 'Must not execute' } else { 'Desktop.dll' }
    ModuleVersion = '4.2.0'
    CompanyName = 'Example'
    Copyright = 'Copyright (c) Example'
    PrivateData = @{ PSData = @{ ProjectUri = 'https://github.com/example/module' } }
}
'@ | Set-Content -LiteralPath $path

        $component = Get-PowerShellModuleComponents -Path $path

        $component.Version | Should -Be '4.2.0'
        $component.Source | Should -Be 'https://github.com/example/module'
        $component.License | Should -Be 'MIT'
    }

    It 'WhenDependenciesHaveTheirOwnIdentityThenUsesOnlyTheOwningModuleMetadata' {
        $path = Join-Path $TestDrive 'Owner.psd1'
        @'
@{
    ModuleVersion = '4.2.0'
    CompanyName = 'Example'
    Copyright = 'Copyright (c) Owner'
    ProjectUri = 'https://example.com/not-psdata'
    RequiredModules = @(@{ ModuleName = 'Dependency'; ModuleVersion = '1.0.0'; CompanyName = 'Devolutions'; Copyright = 'Wrong holder' })
    PrivateData = @{
        PSData = @{
            ProjectUri = 'https://example.com/owner'
            LicenseUri = 'https://example.com/owner-license'
            Nested = @{ ProjectUri = 'https://example.com/nested-wrong' }
        }
        Dependency = @{ ProjectUri = 'https://example.com/wrong'; LicenseUri = 'https://example.com/wrong-license' }
    }
}
'@ | Set-Content -LiteralPath $path

        $component = Get-PowerShellModuleComponents -Path $path -UrlMappings @{
            exact = @{ 'https://example.com/owner-license' = 'BSD-2-Clause' }; contains = @{}
        }

        $component.Version | Should -Be '4.2.0'
        $component.Copyright | Should -Be 'Copyright (c) Owner'
        $component.IsFirstParty | Should -BeFalse
        $component.Source | Should -Be 'https://example.com/owner'
        $component.License | Should -Be 'BSD-2-Clause'
    }

    It 'WhenOnlyDependenciesHaveAVersionThenDoesNotAttributeTheDependencyAsTheOwner' {
        $path = Join-Path $TestDrive 'NoOwner.psd1'
        "@{ RequiredModules = @(@{ ModuleName = 'Dependency'; ModuleVersion = '1.0.0' }) }" | Set-Content -LiteralPath $path

        $component = Get-PowerShellModuleComponents -Path $path

        $component | Should -BeNullOrEmpty
    }

    It 'WhenPsDataContainsDynamicUnrelatedFieldsThenDoesNotExecuteThem' {
        $path = Join-Path $TestDrive 'Dynamic.psd1'
        @'
@{
    ModuleVersion = '4.2.0'
    PrivateData = @{
        PSData = @{
            ProjectUri = 'https://example.com/owner'
            ReleaseNotes = $(throw 'Must not execute')
        }
    }
}
'@ | Set-Content -LiteralPath $path

        $component = Get-PowerShellModuleComponents -Path $path

        $component.Source | Should -Be 'https://example.com/owner'
        $component.Version | Should -Be '4.2.0'
    }
}

Describe 'Notice generation pipeline' {
    It 'WhenGeneratingThenPreservesSeparateVersionsAndTheirOwnLicenseTexts' {
        $inputPath = Join-Path $TestDrive 'webpack-components.json'
        @{
            format = 'devolutions-webpack-components/1'
            components = @(
                @{ name = 'example'; version = '1.0.0'; license = 'MIT'; source = 'https://example.com/one'; licenseText = "Copyright (c) First Holder`r`nFirst license`r`n" },
                @{ name = 'example'; version = '2.0.0'; license = 'ISC'; source = 'https://example.com/two'; licenseText = "Copyright (c) Second Holder`r`nSecond license`r`n" },
                @{ name = '@devolutions/internal'; version = '1.0.0'; license = ''; source = 'https://devolutions.net' }
            )
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $inputPath
        $outputPath = Join-Path $TestDrive 'THIRD-PARTY-NOTICES.txt'
        $pwsh = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })

        $output = & $pwsh -NoProfile -File (Join-Path $toolRoot 'Invoke-ThirdPartyLicenses.ps1') `
            -ArtifactPath $inputPath -OutputPath $outputPath -AppName 'Example' -AppVersion '3.0.0' -Json -Sbom -SeparateVersions 2>&1
        $exitCode = $LASTEXITCODE
        $document = Get-Content ([IO.Path]::ChangeExtension($outputPath, '.json')) -Raw | ConvertFrom-Json
        $bom = Get-Content ([IO.Path]::ChangeExtension($outputPath, '.sbom.json')) -Raw | ConvertFrom-Json

        $exitCode | Should -Be 0 -Because ($output -join "`n")
        $document.components.Count | Should -Be 2
        ($document.components | Where-Object version -eq '1.0.0').licenseText | Should -BeExactly "Copyright (c) First Holder`r`nFirst license`r`n"
        ($document.components | Where-Object version -eq '2.0.0').license | Should -Be 'ISC'
        $bom.components.name | Should -Contain '@devolutions/internal'
        $document.components.name | Should -Not -Contain '@devolutions/internal'
    }

    It 'WhenReadingNugetLicenseThenPreservesThePackageTextVerbatim' {
        $package = Join-Path $TestDrive 'example\1.0.0'
        New-Item -ItemType Directory -Path $package -Force | Out-Null
        '<package><metadata><id>Example</id><version>1.0.0</version><license type="file">LICENSE.txt</license></metadata></package>' |
            Set-Content -LiteralPath (Join-Path $package 'example.nuspec')
        $licenseText = "Copyright (c) Example`r`n`r`nExact license text  `r`n"
        Set-Content -LiteralPath (Join-Path $package 'LICENSE.txt') -Value $licenseText -NoNewline

        $metadata = Resolve-NuGetMetadata -Component ([pscustomobject]@{ PackagePath = 'example\1.0.0' }) -PackagesRoot $TestDrive -UrlMappings @{}

        $metadata.LicenseText | Should -BeExactly $licenseText
    }
}

Describe 'Post-publish WebAssembly inventory' {
    It 'WhenPublishingWebcilThenIncludesOnlyPresentAssembliesAndTheActualSdkRuntimePack' {
        $obj = Join-Path $TestDrive 'obj'
        $manifestDirectory = Join-Path $obj 'Release\net10.0-browser'
        $published = Join-Path $TestDrive 'publish'
        $package = Join-Path $TestDrive 'packages\example\1.0.0'
        $runtime = Join-Path $TestDrive 'dotnet\packs\Microsoft.NETCore.App.Runtime.Mono.browser-wasm\10.0.12'
        New-Item -ItemType Directory -Force -Path $manifestDirectory, (Join-Path $published '_framework'), $package, $runtime | Out-Null
        @{
            libraries = @{
                'Example/1.0.0' = @{ type = 'package'; path = 'example/1.0.0' }
                'Trimmed/1.0.0' = @{ type = 'package'; path = 'trimmed/1.0.0' }
                'BuildTool/1.0.0' = @{ type = 'package'; path = 'buildtool/1.0.0' }
            }
            targets = @{ 'net10.0-browser/browser-wasm' = @{
                'Example/1.0.0' = @{ runtime = @{ 'lib/net10.0/Example.dll' = @{} } }
                'Trimmed/1.0.0' = @{ runtime = @{ 'lib/net10.0/Trimmed.dll' = @{} } }
                'BuildTool/1.0.0' = @{ compile = @{ 'BuildTool.dll' = @{} } }
            } }
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $obj 'project.assets.json')
        '<package><metadata><id>Example</id><version>1.0.0</version><license type="expression">MIT</license></metadata></package>' |
            Set-Content -LiteralPath (Join-Path $package 'example.nuspec')
        'Copyright (c) Example' | Set-Content -LiteralPath (Join-Path $package 'LICENSE')
        'Copyright (c) Microsoft Corporation' | Set-Content -LiteralPath (Join-Path $runtime 'LICENSE.TXT')
        'Runtime third-party notices' | Set-Content -LiteralPath (Join-Path $runtime 'ThirdPartyNotices.txt')
        Set-Content -LiteralPath (Join-Path $published '_framework\Example.hash.wasm') -Value ''
        Set-Content -LiteralPath (Join-Path $published '_framework\dotnet.native.hash.wasm') -Value ''
        @{
            ManifestType = 'Publish'
            Assets = @(
                @{ Identity = 'Example.wasm'; RelativePath = '_framework/Example#[.{fingerprint}]!.wasm'; Fingerprint = 'hash'; AssetRole = 'Primary'; AssetTraitName = 'WasmResource'; AssetTraitValue = 'runtime'; OriginalItemSpec = 'Example.wasm' },
                @{ Identity = 'Trimmed.wasm'; RelativePath = '_framework/Trimmed#[.{fingerprint}]!.wasm'; Fingerprint = 'hash'; AssetRole = 'Primary'; AssetTraitName = 'WasmResource'; AssetTraitValue = 'runtime'; OriginalItemSpec = 'Trimmed.wasm' },
                @{ Identity = 'dotnet.native.wasm'; RelativePath = '_framework/dotnet.native#[.{fingerprint}]!.wasm'; Fingerprint = 'hash'; AssetRole = 'Primary'; AssetTraitName = 'WasmResource'; AssetTraitValue = 'native'; OriginalItemSpec = (Join-Path $runtime 'runtimes\browser-wasm\native\dotnet.native.wasm') }
            )
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $manifestDirectory 'staticwebassets.publish.json')

        $components = @(Get-WebAssemblyComponents -ManifestPath (Join-Path $manifestDirectory 'staticwebassets.publish.json') `
            -BinariesPath $published -PackagesRoot (Join-Path $TestDrive 'packages') -UrlMappings @{})

        $components.Name | Should -Contain 'Example'
        $components.Name | Should -Not -Contain 'Trimmed'
        $components.Name | Should -Not -Contain 'BuildTool'
        ($components | Where-Object IsRuntime).Version | Should -Be '10.0.12'
        ($components | Where-Object IsRuntime).Notice.Text | Should -Match 'Runtime third-party notices'
    }
}

Describe 'Multiple-version license comparisons' {
    It 'WhenOneVersionChangesLicenseThenReportsThatVersionWithoutCollapsingTheOthers' {
        $previous = @(
            @{ id = 'Example'; ecosystem = 'npm'; version = '1.0.0'; license = 'MIT'; textSha256 = 'one' },
            @{ id = 'Example'; ecosystem = 'npm'; version = '2.0.0'; license = 'MIT'; textSha256 = 'two' }
        )
        $current = @(
            @{ id = 'Example'; ecosystem = 'npm'; version = '1.0.0'; license = 'ISC'; textSha256 = 'one' },
            @{ id = 'Example'; ecosystem = 'npm'; version = '2.0.0'; license = 'MIT'; textSha256 = 'two' }
        )

        $changes = @(Compare-LicenseLock -Current $current -Previous $previous -SeparateVersions)

        $changes.Count | Should -Be 1
        $changes[0].Kind | Should -Be 'LicenseChanged'
        $changes[0].Before | Should -Be 'MIT'
        $changes[0].After | Should -Be 'ISC'
    }
}

Describe 'Assembly versions in separate host directories' {
    It 'WhenTwoVersionsOfOneFileNameShipThenMatchesBothVersions' {
        $one = Join-Path $TestDrive 'one'
        $two = Join-Path $TestDrive 'two'
        New-Item -ItemType Directory -Path $one, $two -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSHOME 'System.Management.Automation.dll') -Destination (Join-Path $one 'Shared.dll')
        Copy-Item -LiteralPath (Join-Path $PSHOME 'System.Text.Json.dll') -Destination (Join-Path $two 'Shared.dll')
        $firstInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $one 'Shared.dll'))
        $secondInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $two 'Shared.dll'))
        $first = [version]::new($firstInfo.FileMajorPart, $firstInfo.FileMinorPart, $firstInfo.FileBuildPart, $firstInfo.FilePrivatePart)
        $second = [version]::new($secondInfo.FileMajorPart, $secondInfo.FileMinorPart, $secondInfo.FileBuildPart, $secondInfo.FilePrivatePart)

        $shipped = Get-ShippedFiles -Directory $TestDrive
        $matches = @(
            Test-ShippedVersion -Component ([pscustomobject]@{ AssetFileVersions = @{ 'Shared.dll' = $first } }) -Shipped $shipped
            Test-ShippedVersion -Component ([pscustomobject]@{ AssetFileVersions = @{ 'Shared.dll' = $second } }) -Shipped $shipped
        )

        $first | Should -Not -Be $second
        $matches | Should -Be @($true, $true)
    }
}

Describe 'Shared shipped-version selection for notices and SBOM' {
    It 'WhenOnlyOnePackageVersionShipsThenOmitsTheOtherFromEveryInventory (<Name>)' -ForEach @(
        @{ Name = 'Example'; Attributed = $true; LibraryType = 'package' }
        @{ Name = 'Devolutions.Example'; Attributed = $false; LibraryType = 'package' }
        @{ Name = 'OwnApplication'; Attributed = $false; LibraryType = 'project' }
    ) {
        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $published = Join-Path $root 'publish'
        $cache = Join-Path $root 'packages'
        New-Item -ItemType Directory -Path $published, $cache -Force | Out-Null
        $binary = Join-Path $published 'Shared.dll'
        Copy-Item -LiteralPath (Join-Path $PSHOME 'System.Text.Json.dll') -Destination $binary
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($binary)
        $actualVersion = [version]::new($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart)
        $oldVersion = [version]::new($info.FileMajorPart + 1, 0, 0, 0)
        $artifacts = @()
        foreach ($version in @('1.0.0', '2.0.0'))
        {
            $package = Join-Path $cache "$($Name.ToLowerInvariant())\$version"
            New-Item -ItemType Directory -Path $package -Force | Out-Null
            "<package><metadata><id>$Name</id><version>$version</version><license type=`"expression`">MIT</license><projectUrl>https://example.com/package</projectUrl></metadata></package>" |
                Set-Content -LiteralPath (Join-Path $package 'package.nuspec')
            "Copyright (c) Example`nPackage license text" | Set-Content -LiteralPath (Join-Path $package 'LICENSE')
            $manifest = Join-Path $published "$version.deps.json"
            $fileVersion = if ($version -eq '2.0.0') { $actualVersion.ToString() } else { $oldVersion.ToString() }
            @{
                runtimeTarget = @{ name = 'net10.0/win-x64' }
                libraries = @{ "$Name/$version" = @{ type = $LibraryType; path = "$($Name.ToLowerInvariant())/$version" } }
                targets = @{ 'net10.0/win-x64' = @{ "$Name/$version" = @{ runtime = @{ 'lib/net10.0/Shared.dll' = @{ fileVersion = $fileVersion } } } } }
            } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifest
            $artifacts += $manifest
        }

        $previousCache = $env:NUGET_PACKAGES
        $outputPath = Join-Path $root 'THIRD-PARTY-NOTICES.txt'
        try
        {
            $env:NUGET_PACKAGES = $cache
            $pwsh = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
            $arguments = "@{ ArtifactPath = @('$($artifacts -join "','")'); OutputPath = '$outputPath'; BinariesPath = '$published'; AppName = 'Example'; AppVersion = '3.0.0'; ShippedOnly = `$true; Json = `$true; Sbom = `$true; SeparateVersions = `$true }"
            $output = & $pwsh -NoProfile -Command "`$arguments = $arguments; & '$(Join-Path $toolRoot 'Invoke-ThirdPartyLicenses.ps1')' @arguments" 2>&1
            $exitCode = $LASTEXITCODE
        }
        finally { $env:NUGET_PACKAGES = $previousCache }
        $document = Get-Content ([IO.Path]::ChangeExtension($outputPath, '.json')) -Raw | ConvertFrom-Json
        $bom = Get-Content ([IO.Path]::ChangeExtension($outputPath, '.sbom.json')) -Raw | ConvertFrom-Json

        $exitCode | Should -Be 0 -Because ($output -join "`n")
        $expectedVersion = if ($LibraryType -eq 'project') { ($info.ProductVersion -split '\+')[0] } else { '2.0.0' }
        @($document.inventory | Where-Object name -eq $Name).version | Should -Be @($expectedVersion)
        @($bom.components | Where-Object name -eq $Name).version | Should -Be @($expectedVersion)
        @($document.components | Where-Object name -eq $Name).Count | Should -Be ([int]$Attributed)
    }
}
