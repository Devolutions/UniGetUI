Describe 'Licence texts and notices the main script resolves' {
    BeforeAll {
        $toolSource = Split-Path -Parent $PSScriptRoot
        . (Join-Path $toolSource 'Private' 'Get-RepositoryNotice.ps1')
        . (Join-Path $toolSource 'Private' 'Find-Approval.ps1')
        Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')

        # A copy of the tool, so each run can give it its own package-overrides.json
        $script:tool = Join-Path $TestDrive 'tool'
        $null = New-Item -ItemType Directory -Path $script:tool
        foreach ($item in 'Invoke-ThirdPartyLicenses.ps1', 'Private', 'templates', 'config') {
            Copy-Item -LiteralPath (Join-Path $toolSource $item) -Destination $script:tool -Recurse
        }

        $script:packagesRoot = Join-Path $TestDrive 'nuget'
        $null = New-Item -ItemType Directory -Path $script:packagesRoot
        $script:savedPackagesRoot = $env:NUGET_PACKAGES
        $env:NUGET_PACKAGES = $script:packagesRoot

        # What GitHub would answer for a repository, so no run reaches the network
        function New-Repository {
            param([Parameter(Mandatory)][string]$Name, [string]$LicenseText, [string]$NoticeText)

            $names = @(if ($LicenseText) { 'LICENSE' }; if ($NoticeText) { 'NOTICE' })
            $files = @{}
            if ($LicenseText) {
                $files["$Name@/$script:LicenseFilePattern".ToLowerInvariant()] = @{ Repository = $Name; File = 'LICENSE'; Url = "https://github.com/$Name/blob/HEAD/LICENSE"; Text = $LicenseText }
            }
            if ($NoticeText) {
                $files["$Name@/$script:NoticeFilePattern".ToLowerInvariant()] = @{ Repository = $Name; File = 'NOTICE'; Url = "https://github.com/$Name/blob/HEAD/NOTICE"; Text = $NoticeText }
            }

            @{ Listings = @{ "$Name@".ToLowerInvariant() = @{ Ref = ''; Names = $names } }; Files = $files }
        }

        function New-OwnAssembly {
            param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][hashtable]$Resources, [string]$Company = 'Devolutions inc.')

            $source = "[assembly: System.Reflection.AssemblyCompany(""$Company"")] public class Probe {}"
            $compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(
                [IO.Path]::GetFileNameWithoutExtension($Path),
                [Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source)),
                [Microsoft.CodeAnalysis.MetadataReference[]]@([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile([object].Assembly.Location)),
                [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary))
            # Sorted, so the reader meets the resources in the same order on every run
            $manifest = foreach ($resource in ($Resources.GetEnumerator() | Sort-Object Key)) {
                $bytes = [Text.Encoding]::UTF8.GetBytes($resource.Value)
                [Microsoft.CodeAnalysis.ResourceDescription]::new($resource.Key, [Func[IO.Stream]] { [IO.MemoryStream]::new($bytes) }.GetNewClosure(), $true)
            }

            $win32 = $compilation.CreateDefaultWin32Resources($true, $true, $null, $null)
            $stream = [IO.File]::Create($Path)
            try { $result = $compilation.Emit($stream, $null, $null, $win32, [Microsoft.CodeAnalysis.ResourceDescription[]]@($manifest)) } finally { $stream.Dispose() }
            if (-not $result.Success) { throw ($result.Diagnostics -join "`n") }
        }

        # An app folder with its deps.json; its packages go in the NuGet cache, its own assembly carries the resources
        function New-App {
            param([hashtable]$Packages = @{}, [hashtable]$Resources)

            $app = Join-Path $TestDrive "app-$([guid]::NewGuid().ToString('N'))"
            $null = New-Item -ItemType Directory -Path $app
            $target = @{ 'TestApp/1.0.0' = @{ runtime = @{ 'TestApp.dll' = @{} } } }
            $libraries = @{ 'TestApp/1.0.0' = @{ type = 'project' } }
            foreach ($package in $Packages.GetEnumerator()) {
                $id, $version = $package.Key -split '/'
                $target[$package.Key] = @{ runtime = @{ "lib/net8.0/$id.dll" = @{} } }
                $libraries[$package.Key] = @{ type = 'package'; path = "$($id.ToLowerInvariant())/$version" }
                $folder = Join-Path $script:packagesRoot $id.ToLowerInvariant() $version
                $null = New-Item -ItemType Directory -Force -Path $folder
                foreach ($file in $package.Value.GetEnumerator()) { Set-Content -LiteralPath (Join-Path $folder $file.Key) -Value $file.Value -NoNewline }
            }

            @{ runtimeTarget = @{ name = '.NETCoreApp,Version=v8.0' }; targets = @{ '.NETCoreApp,Version=v8.0' = $target }; libraries = $libraries } |
                ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $app 'TestApp.deps.json')
            if ($Resources) { New-OwnAssembly -Path (Join-Path $app 'RemoteDesktopManager.Assets.dll') -Resources $Resources }
            return $app
        }

        function New-WebpackInventory {
            param([Parameter(Mandatory)][hashtable[]]$Components)

            # The script knows the inventory by this exact file name
            $folder = New-Item -ItemType Directory -Path (Join-Path $TestDrive "webpack-$([guid]::NewGuid().ToString('N'))")
            $path = Join-Path $folder 'webpack-components.json'
            @{ format = 'devolutions-webpack-components/1'; components = $Components } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $path
            return $path
        }

        function Invoke-Tool {
            param(
                [Parameter(Mandatory)][string[]]$Arguments,
                [hashtable]$Overrides = @{},
                [hashtable]$Approvals = @{},
                [hashtable]$Excludes = @{},
                [hashtable[]]$Repositories = @(),
                [hashtable]$SpdxTexts = @{},
                # -File passes one value per parameter: several build outputs go through -Command
                [string[]]$ArtifactPaths = @()
            )

            @{ packages = $Overrides } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $script:tool 'config' 'package-overrides.json')
            @{ packages = $Approvals } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $script:tool 'config' 'approved-packages.json')
            @{ packages = $Excludes } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $script:tool 'config' 'excluded-packages.json')
            $cache = @{ Listings = @{}; Files = @{}; SpdxTexts = $SpdxTexts }
            foreach ($repository in $Repositories) {
                foreach ($key in $repository.Listings.Keys) { $cache.Listings[$key] = $repository.Listings[$key] }
                foreach ($key in $repository.Files.Keys) { $cache.Files[$key] = $repository.Files[$key] }
            }

            $run = Join-Path $TestDrive "run-$([guid]::NewGuid().ToString('N'))"
            $null = New-Item -ItemType Directory -Path $run
            $cachePath = Join-Path $run 'cache.json'
            ConvertTo-Json -InputObject $cache -Depth 8 | Set-Content -LiteralPath $cachePath
            $outputPath = Join-Path $run 'THIRD-PARTY-NOTICES.txt'
            $pwsh = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })

            if ($ArtifactPaths) {
                $quoted = @(@('-RepoRoot', $TestDrive, '-AppName', 'Test App', '-AppVersion', '1.0.0', '-OutputPath', $outputPath, '-CachePath', $cachePath, '-Json') + $Arguments |
                    ForEach-Object { if ($_ -match '^-\w+$') { $_ } else { "'$($_ -replace "'", "''")'" } }) -join ' '
                $paths = @($ArtifactPaths | ForEach-Object { "'$($_ -replace "'", "''")'" }) -join ','
                $output = & $pwsh -NoProfile -Command "& '$(Join-Path $script:tool 'Invoke-ThirdPartyLicenses.ps1')' -ArtifactPath @($paths) $quoted" 2>&1
            }
            else {
                $output = & $pwsh -NoProfile -File (Join-Path $script:tool 'Invoke-ThirdPartyLicenses.ps1') -RepoRoot $TestDrive `
                    -AppName 'Test App' -AppVersion '1.0.0' -OutputPath $outputPath -CachePath $cachePath -Json @Arguments 2>&1
            }
            $exitCode = $LASTEXITCODE
            $jsonPath = [IO.Path]::ChangeExtension($outputPath, '.json')
            $json = if (Test-Path -LiteralPath $jsonPath) { Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json }
            [pscustomobject]@{
                Output      = $output -join "`n"
                ExitCode    = $exitCode
                NoticesPath = $outputPath
                Json        = $json
                Components  = @(if ($json) { $json.components })
            }
        }
    }

    AfterAll {
        $env:NUGET_PACKAGES = $script:savedPackagesRoot
    }

    Context 'a licence text written by hand' {
        BeforeAll {
            $script:assetList = Join-Path $TestDrive 'assets.json'
            '{ "include": [ "*.js" ] }' | Set-Content -LiteralPath $script:assetList
            $script:sidecar = "Component : Lib 1.0`nSource    : https://github.com/test/lib`nLicense   : MIT`n`nHand-written text."
        }

        It 'gives way to the LICENSE the repository has, with a warning' {
            $app = New-App -Resources @{ 'Assets.lib.js' = '1'; 'Assets.lib.js.LICENSE.txt' = $script:sidecar }

            $run = Invoke-Tool -Arguments '-ArtifactPath', (Join-Path $app 'TestApp.deps.json'), '-BinariesPath', $app, '-EmbeddedAssetsPath', $script:assetList `
                -Repositories (New-Repository 'test/lib' -LicenseText "MIT License`n`nCopyright (c) Lib Authors")

            $lib = $run.Components | Where-Object name -eq 'Lib'
            $lib.licenseText | Should -Be "MIT License`n`nCopyright (c) Lib Authors"
            $lib.licenseTextOrigin | Should -Be 'repository'
            $run.Output | Should -Match '\| 🟡 Warning \| UnusedLicenseText \|'
        }

        It 'stays when the repository has no LICENSE' {
            $app = New-App -Resources @{ 'Assets.lib.js' = '1'; 'Assets.lib.js.LICENSE.txt' = $script:sidecar }

            $run = Invoke-Tool -Arguments '-ArtifactPath', (Join-Path $app 'TestApp.deps.json'), '-BinariesPath', $app, '-EmbeddedAssetsPath', $script:assetList `
                -Repositories (New-Repository 'test/lib')

            $lib = $run.Components | Where-Object name -eq 'Lib'
            $lib.licenseText | Should -Be 'Hand-written text.'
            $lib.licenseTextOrigin | Should -Be 'sidecar'
            $run.Output | Should -Not -Match 'UnusedLicenseText'
        }

        It 'is reported as a warning when its header cannot be read' {
            $app = New-App -Resources @{ 'Assets.lib.js' = '1'; 'Assets.lib.js.LICENSE.txt' = "Licence : MIT`n`nHand-written text." }

            $run = Invoke-Tool -Arguments '-ArtifactPath', (Join-Path $app 'TestApp.deps.json'), '-BinariesPath', $app, '-EmbeddedAssetsPath', $script:assetList

            $run.Output | Should -Match '\| 🟡 Warning \| InvalidSidecar \|'
        }
    }

    Context 'a sidecar of a package another reader found' {
        BeforeAll {
            $script:joinAssets = Join-Path $TestDrive 'join-assets.json'
            '{ "include": [ "*.js", "*.css" ], "bundles": [ "*.editor.js" ] }' | Set-Content -LiteralPath $script:joinAssets
            $script:packageText = "BSD 3-Clause License`n`nCopyright (c) 2006, Hljs Authors`n`nRedistribution and use are permitted."
            $script:bundleHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('1'))).ToLowerInvariant()

            # A stylesheet with its sidecar beside a bundled script whose list names the same project
            function New-JoinApp([string]$Sidecar, [string]$PackageText = $script:packageText, [hashtable]$Packages = @{}) {
                $list = "sha256:$script:bundleHash`nPackage: hljs`nVersion: 11.4.0`nSource: git://github.com/test/hljs.git`nLicense: BSD-3-Clause`n`n$PackageText"
                New-App -Packages $Packages -Resources @{
                    'Assets.editor.js' = '1'; 'Assets.editor.js.3rdpartylicenses.txt' = $list
                    'Assets.theme.css' = 'x'; 'Assets.theme.css.LICENSE.txt' = $Sidecar
                }
            }

            function Invoke-JoinRun([string]$App, [string[]]$More = @(), [hashtable[]]$Repositories = @(New-Repository 'test/hljs' -LicenseText 'REPOSITORY TEXT')) {
                Invoke-Tool -Arguments (@('-ArtifactPath', (Join-Path $App 'TestApp.deps.json'), '-BinariesPath', $App, '-EmbeddedAssetsPath', $script:joinAssets) + $More) `
                    -Repositories $Repositories
            }
        }

        It 'gives the project one block, with the package''s data and both versions' {
            $app = New-JoinApp "Component : hljs unversioned`nSource    : https://github.com/test/hljs`nLicense   : BSD-3-Clause"

            $blocks = @((Invoke-JoinRun $app).Components | Where-Object name -eq 'hljs')

            $blocks.Count | Should -Be 1
            $blocks[0].ecosystem | Should -Be 'npm'
            $blocks[0].version | Should -Be '11.4.0, unversioned'
            $blocks[0].licenseTextOrigin | Should -Be 'package'
        }

        It 'keeps the sidecar apart when its <Case> differs' -ForEach @(
            @{ Case = 'repository'; Sidecar = "Component : hljs unversioned`nSource    : https://github.com/other/hljs`nLicense   : BSD-3-Clause" }
            @{ Case = 'licence'; Sidecar = "Component : hljs unversioned`nSource    : https://github.com/test/hljs`nLicense   : MIT" }
        ) {
            $app = New-JoinApp $Sidecar

            $run = Invoke-JoinRun $app -Repositories @((New-Repository 'test/hljs' -LicenseText 'REPOSITORY TEXT'), (New-Repository 'other/hljs' -LicenseText 'OTHER TEXT'))

            @($run.Components | Where-Object name -eq 'hljs').Count | Should -Be 2
        }

        It 'joins whatever the case of the name' {
            $app = New-JoinApp "Component : HLJS unversioned`nSource    : https://github.com/test/hljs`nLicense   : BSD-3-Clause"

            @((Invoke-JoinRun $app).Components | Where-Object name -eq 'hljs').Count | Should -Be 1
        }

        It 'keeps the sidecar apart when packages of both ecosystems match it' {
            $nuspec = '<package><metadata><id>hljs</id><version>11.4.0</version><license type="expression">BSD-3-Clause</license>' +
                '<copyright>Copyright (c) 2006, Hljs Authors</copyright><repository url="https://github.com/test/hljs" /></metadata></package>'
            $app = New-JoinApp "Component : hljs unversioned`nSource    : https://github.com/test/hljs`nLicense   : BSD-3-Clause" `
                -Packages @{ 'hljs/11.4.0' = @{ 'hljs.nuspec' = $nuspec } }

            $blocks = @((Invoke-JoinRun $app).Components | Where-Object name -eq 'hljs')

            $blocks.Count | Should -Be 3
            @($blocks | Where-Object ecosystem -eq 'embedded').Count | Should -Be 1
        }

        It 'never joins a manual entry' {
            $app = New-App -Resources @{ 'Assets.theme.css' = 'x'; 'Assets.theme.css.LICENSE.txt' = "Component : hljs unversioned`nSource    : https://github.com/test/hljs`nLicense   : BSD-3-Clause" }
            $manual = Join-Path $TestDrive 'join-manual.json'
            '{ "components": [ { "name": "hljs", "version": "1.0.0", "license": "BSD-3-Clause", "source": "https://github.com/test/hljs", "copyright": "Copyright (c) Hljs" } ] }' |
                Set-Content -LiteralPath $manual

            $run = Invoke-JoinRun $app -More '-ManualComponentsPath', $manual

            @($run.Components | Where-Object name -eq 'hljs').Count | Should -Be 2
        }

        It 'never joins a notice in copied code' {
            $app = New-App -Resources @{ 'Assets.editor.js' = '1'; 'Assets.editor.js.3rdpartylicenses.txt' = "sha256:$script:bundleHash`nPackage: hljs`nVersion: 11.4.0`nSource: git://github.com/test/hljs.git`nLicense: BSD-3-Clause`n`n$script:packageText" }
            $source = Join-Path $TestDrive "copied-$([guid]::NewGuid().ToString('N'))" 'Copied.cs'
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $source)
            Set-Content -LiteralPath $source -Value @('// THIRD-PARTY-NOTICE', '// Component : hljs 1.0', '// Source    : https://github.com/test/hljs', '// License   : BSD-3-Clause', '// END THIRD-PARTY-NOTICE', 'namespace Copied;')
            # The PDB beside our assembly names the copied file, as the build's would
            $metadata = [Reflection.Metadata.Ecma335.MetadataBuilder]::new()
            $null = $metadata.AddDocument($metadata.GetOrAddDocumentName($source), $metadata.GetOrAddGuid([guid]::Empty), $metadata.GetOrAddBlob([byte[]]@()), $metadata.GetOrAddGuid([guid]::Empty))
            $rowCounts = [Collections.Immutable.ImmutableArray[int]]::Empty.AddRange([int[]]::new([Reflection.Metadata.Ecma335.MetadataTokens]::TableCount))
            $pdb = [Reflection.Metadata.BlobBuilder]::new()
            $null = [Reflection.Metadata.Ecma335.PortablePdbBuilder]::new($metadata, $rowCounts, [Reflection.Metadata.MethodDefinitionHandle]::new()).Serialize($pdb)
            [IO.File]::WriteAllBytes((Join-Path $app 'RemoteDesktopManager.Assets.pdb'), $pdb.ToArray())

            $blocks = @((Invoke-JoinRun $app -More '-IncludeCopiedSource').Components | Where-Object name -eq 'hljs')

            $blocks.Count | Should -Be 2
            @($blocks | Where-Object ecosystem -eq 'source').Count | Should -Be 1
        }

        It 'shares the block of the package''s version only, with separate versions' -ForEach @(
            @{ Version = '11.4.0'; Blocks = 1 }
            @{ Version = 'unversioned'; Blocks = 2 }
        ) {
            $app = New-JoinApp "Component : hljs $Version`nSource    : https://github.com/test/hljs`nLicense   : BSD-3-Clause"

            @((Invoke-JoinRun $app -More '-SeparateVersions').Components | Where-Object name -eq 'hljs').Count | Should -Be $Blocks
        }

        It 'leaves the blocks of a notices JSON as they are' {
            # Written by a run before joins existed: the package and the sidecar of one project are two blocks
            $json = Join-Path $TestDrive "older-$([guid]::NewGuid().ToString('N')).json"
            $package = @{ ecosystem = 'npm'; name = 'hljs'; version = '11.4.0'; license = 'BSD-3-Clause'; repository = 'https://github.com/test/hljs'; copyright = 'Copyright (c) 2006, Hljs Authors'; licenseText = $script:packageText; licenseTextOrigin = 'package' }
            $sidecar = @{ ecosystem = 'embedded'; name = 'hljs'; version = 'unversioned'; license = 'BSD-3-Clause'; repository = 'https://github.com/test/hljs'; copyright = 'Copyright (c) 2006, Hljs Authors'; licenseText = $script:packageText; licenseTextOrigin = 'repository'; notice = @{ text = 'Pinned notice.' } }
            @{ format = 'devolutions-third-party-notices/1'; appName = 'Test App'; appVersion = '1.0.0'; components = @($package, $sidecar) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $json

            $blocks = @((Invoke-Tool -Arguments '-ArtifactPath', $json).Components | Where-Object name -eq 'hljs')

            $blocks.Count | Should -Be 2
            ($blocks | Where-Object ecosystem -eq 'embedded').notice.text | Should -Be 'Pinned notice.'
        }

        It 'keeps its own block beside a package of its name read back from a notices JSON' {
            # This run's package matches the sidecar; the merged JSON's higher version is another project
            $app = New-JoinApp "Component : hljs unversioned`nSource    : https://github.com/test/hljs`nLicense   : BSD-3-Clause"
            $json = Join-Path $TestDrive "merged-$([guid]::NewGuid().ToString('N')).json"
            $other = @{ ecosystem = 'npm'; name = 'hljs'; version = '12.0.0'; license = 'MIT'; repository = 'https://github.com/other/hljs'; copyright = 'Copyright (c) Other'; licenseText = 'MIT License'; licenseTextOrigin = 'package' }
            @{ format = 'devolutions-third-party-notices/1'; appName = 'Test App'; appVersion = '1.0.0'; components = @($other) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $json

            $run = Invoke-Tool -ArtifactPaths (Join-Path $app 'TestApp.deps.json'), $json -Arguments '-BinariesPath', $app, '-EmbeddedAssetsPath', $script:joinAssets `
                -Repositories (New-Repository 'test/hljs' -LicenseText 'REPOSITORY TEXT')

            $sidecar = @($run.Components | Where-Object { $_.name -eq 'hljs' -and $_.ecosystem -eq 'embedded' })
            $sidecar.Count | Should -Be 1
            $sidecar[0].license | Should -Be 'BSD-3-Clause'
        }

        It 'prints the package''s data even when the sidecar gives a higher version' {
            $app = New-JoinApp "Component : hljs 99.0`nSource    : https://github.com/test/hljs`nLicense   : BSD-3-Clause"

            $block = @((Invoke-JoinRun $app).Components | Where-Object name -eq 'hljs')[0]

            $block.version | Should -Be '11.4.0, 99.0'
            $block.ecosystem | Should -Be 'npm'
            $block.licenseText | Should -BeLike 'BSD 3-Clause License*'
        }

        It 'takes the sidecar''s copyright line when the package gives none' {
            $app = New-JoinApp "Component : hljs unversioned`nSource    : https://github.com/test/hljs`nLicense   : BSD-3-Clause`nCopyright : Copyright (c) Sidecar Holder" `
                -PackageText "BSD 3-Clause License`n`nRedistribution and use are permitted."

            $blocks = @((Invoke-JoinRun $app).Components | Where-Object name -eq 'hljs')

            $blocks.Count | Should -Be 1
            $blocks[0].copyright | Should -Be 'Copyright (c) Sidecar Holder'
        }

        It 'takes the sidecar''s copyright line when the package''s text holds only a template line' {
            $app = New-JoinApp "Component : hljs unversioned`nSource    : https://github.com/test/hljs`nLicense   : BSD-3-Clause`nCopyright : Copyright (c) Sidecar Holder" `
                -PackageText "BSD 3-Clause License`n`nCopyright (c) 2024 [fullname]`n`nRedistribution and use are permitted."

            $blocks = @((Invoke-JoinRun $app).Components | Where-Object name -eq 'hljs')

            $blocks.Count | Should -Be 1
            $blocks[0].copyright | Should -Be 'Copyright (c) Sidecar Holder'
            $blocks[0].licenseText | Should -Match 'Copyright \(c\) Sidecar Holder'
        }

        It 'keeps the package''s copyright line over the sidecar''s' {
            $app = New-JoinApp "Component : hljs unversioned`nSource    : https://github.com/test/hljs`nLicense   : BSD-3-Clause`nCopyright : Copyright (c) Sidecar Holder"

            $blocks = @((Invoke-JoinRun $app).Components | Where-Object name -eq 'hljs')

            $blocks.Count | Should -Be 1
            $blocks[0].copyright | Should -Be 'Copyright (c) 2006, Hljs Authors'
        }

        It 'keeps its own block when a package of that name comes from another repository<Case>' -ForEach @(
            @{ Case = ''; More = @() }
            @{ Case = ', with separate versions'; More = @('-SeparateVersions') }
        ) {
            # The higher version of the name, which the block would print, is another project under another licence
            $other = "`n" + ('-' * 80) + "`nPackage: hljs`nVersion: 12.0.0`nSource: https://github.com/other/hljs`nLicense: MIT`n`nMIT License"
            $app = New-JoinApp "Component : hljs 12.0.0`nSource    : https://github.com/test/hljs`nLicense   : BSD-3-Clause" -PackageText ($script:packageText + $other)

            $run = Invoke-JoinRun $app -More $More -Repositories @((New-Repository 'test/hljs' -LicenseText 'REPOSITORY TEXT'), (New-Repository 'other/hljs' -LicenseText 'OTHER TEXT'))

            $sidecar = @($run.Components | Where-Object { $_.name -eq 'hljs' -and $_.ecosystem -eq 'embedded' })
            $sidecar.Count | Should -Be 1
            $sidecar[0].license | Should -Be 'BSD-3-Clause'
        }

        It 'keeps the NOTICE the package''s repository gives over the one at the commit the sidecar pins' {
            $app = New-JoinApp "Component : hljs unversioned`nSource    : https://github.com/test/hljs`nLicense   : BSD-3-Clause`nCommit    : v1"
            $repository = New-Repository 'test/hljs' -LicenseText 'REPOSITORY TEXT' -NoticeText 'Head notice.'
            $repository.Listings['test/hljs@v1'] = @{ Ref = 'v1'; Names = @('NOTICE') }
            $repository.Files["test/hljs@v1/$script:NoticeFilePattern".ToLowerInvariant()] = @{ Repository = 'test/hljs'; File = 'NOTICE'; Url = 'https://github.com/test/hljs/blob/v1/NOTICE'; Text = 'Pinned notice.' }

            $blocks = @((Invoke-JoinRun $app -Repositories @($repository)).Components | Where-Object name -eq 'hljs')

            $blocks.Count | Should -Be 1
            $blocks[0].notice.text | Should -Be 'Head notice.'
        }

        It 'reads the NOTICE at the commit a later sidecar pins when the first one''s has none' {
            $sidecar = { param($commit) "Component : hljs unversioned`nSource    : https://github.com/test/hljs`nLicense   : BSD-3-Clause`nCommit    : $commit" }
            $list = "sha256:$script:bundleHash`nPackage: hljs`nVersion: 11.4.0`nSource: git://github.com/test/hljs.git`nLicense: BSD-3-Clause`n`n$script:packageText"
            $app = New-App -Resources @{
                'Assets.editor.js' = '1'; 'Assets.editor.js.3rdpartylicenses.txt' = $list
                'Assets.a.css' = 'x'; 'Assets.a.css.LICENSE.txt' = (& $sidecar 'v1')
                'Assets.b.css' = 'y'; 'Assets.b.css.LICENSE.txt' = (& $sidecar 'v2')
            }
            $repository = New-Repository 'test/hljs' -LicenseText 'REPOSITORY TEXT'
            $repository.Listings['test/hljs@v1'] = @{ Ref = 'v1'; Names = @() }
            $repository.Listings['test/hljs@v2'] = @{ Ref = 'v2'; Names = @('NOTICE') }
            $repository.Files["test/hljs@v2/$script:NoticeFilePattern".ToLowerInvariant()] = @{ Repository = 'test/hljs'; File = 'NOTICE'; Url = 'https://github.com/test/hljs/blob/v2/NOTICE'; Text = 'Second notice.' }

            $blocks = @((Invoke-JoinRun $app -Repositories @($repository)).Components | Where-Object name -eq 'hljs')

            $blocks.Count | Should -Be 1
            $blocks[0].notice.text | Should -Be 'Second notice.'
        }

        It 'reads the NOTICE at the commit the sidecar pins when the repository has none at its head' {
            $app = New-JoinApp "Component : hljs unversioned`nSource    : https://github.com/test/hljs`nLicense   : BSD-3-Clause`nCommit    : v1"
            $repository = New-Repository 'test/hljs' -LicenseText 'REPOSITORY TEXT'
            $repository.Listings['test/hljs@v1'] = @{ Ref = 'v1'; Names = @('NOTICE') }
            $repository.Files["test/hljs@v1/$script:NoticeFilePattern".ToLowerInvariant()] = @{ Repository = 'test/hljs'; File = 'NOTICE'; Url = 'https://github.com/test/hljs/blob/v1/NOTICE'; Text = 'Pinned notice.' }

            $blocks = @((Invoke-JoinRun $app -Repositories @($repository)).Components | Where-Object name -eq 'hljs')

            $blocks.Count | Should -Be 1
            $blocks[0].notice.text | Should -Be 'Pinned notice.'
        }
    }

    Context 'a licence text an override leaves out' {
        It 'is looked up neither in the repository nor in SPDX' {
            $inventory = New-WebpackInventory @(@{ name = 'omitted-lib'; version = '1.0.0'; source = 'https://github.com/test/omitted' })

            $run = Invoke-Tool -Arguments '-ArtifactPath', $inventory -Overrides @{ 'omitted-lib' = @{ license = 'LicenseRef-Test'; omitLicenseText = $true } } `
                -Repositories (New-Repository 'test/omitted' -LicenseText 'REPOSITORY TEXT') -SpdxTexts @{ 'LicenseRef-Test' = 'SPDX TEXT' }

            $run.Components[0].licenseText | Should -BeNullOrEmpty
            $run.Components[0].licenseTextOrigin | Should -Be 'omitted'
        }

        It 'is not reported missing' {
            $inventory = New-WebpackInventory @(@{ name = 'omitted-lib'; version = '1.0.0'; copyright = 'Copyright (c) Omitted'; source = 'https://example.com/omitted' })
            $emptyText = 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'

            $run = Invoke-Tool -Arguments '-ArtifactPath', $inventory -Overrides @{ 'omitted-lib' = @{ license = 'LicenseRef-Test'; omitLicenseText = $true } } `
                -Approvals @{ 'omitted-lib' = @{ reason = 'Test'; license = 'LicenseRef-Test'; licenseTextSha256 = $emptyText } }

            $run.Output | Should -Not -Match 'MissingLicenseText'
            $run.ExitCode | Should -Be 0
        }

        It 'still has its approval checked against the licence the package ships' {
            $approvedText = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('EULA, VERSION 1'))).ToLowerInvariant()
            $approvals = @{ 'omitted-lib' = @{ reason = 'Test'; license = 'LicenseRef-Test'; licenseTextSha256 = $approvedText } }
            $overrides = @{ 'omitted-lib' = @{ license = 'LicenseRef-Test'; omitLicenseText = $true } }
            $shipping = { param($text) New-WebpackInventory @(@{ name = 'omitted-lib'; version = '1.0.0'; licenseText = $text; copyright = 'Copyright (c) Omitted'; source = 'https://example.com/omitted' }) }

            $same = Invoke-Tool -Arguments '-ArtifactPath', (& $shipping 'EULA, VERSION 1') -Overrides $overrides -Approvals $approvals
            $changed = Invoke-Tool -Arguments '-ArtifactPath', (& $shipping 'EULA, VERSION 2') -Overrides $overrides -Approvals $approvals

            $same.Output | Should -Not -Match 'ApprovedLicenseChanged'
            $changed.Output | Should -Match 'ApprovedLicenseChanged'
            $same.Components[0].licenseText | Should -BeNullOrEmpty
        }
    }

    Context 'a copyright template line' {
        It 'gives way to the holder in the text a <Origin> gives' -ForEach @(
            @{ Origin = 'package'; Component = @{ licenseText = "MIT License`n`nCopyright (c) <year> <copyright holders>`n`nPermission"; source = 'https://example.com/lib' } }
            @{ Origin = 'repository'; Component = @{ source = 'https://github.com/test/lib' } }
            @{ Origin = 'spdx'; Component = @{ source = 'https://example.com/lib' } }
        ) {
            $Component += @{ name = 'lib'; version = '1.0.0'; license = 'MIT'; copyright = 'Copyright (c) Lib Holder' }
            $run = Invoke-Tool -Arguments '-ArtifactPath', (New-WebpackInventory @($Component)) `
                -Repositories (New-Repository 'test/lib' -LicenseText "MIT License`n`nCopyright (c) [year] [fullname]`n`nPermission") `
                -SpdxTexts @{ MIT = "MIT License`n`nCopyright (c) <year> <copyright holders>`n`nPermission" }

            $run.Components[0].licenseTextOrigin | Should -Be $Origin
            $run.Components[0].licenseText | Should -Be "MIT License`n`nCopyright (c) Lib Holder`n`nPermission"
        }
    }

    Context 'a choice between the LGPL and another licence' {
        It 'prints the other licence''s text and no LGPL notice for <License>' -ForEach @(
            @{ License = '(MIT OR LGPL-3.0-only)' }
            @{ License = 'LGPL-3.0-only OR MIT' }
        ) {
            $inventory = New-WebpackInventory @(@{ name = 'dual-lib'; version = '1.0.0'; license = $License; copyright = 'Copyright (c) Dual'; source = 'https://example.com/dual' })

            $run = Invoke-Tool -Arguments '-ArtifactPath', $inventory -SpdxTexts @{ MIT = 'MIT License'; 'LGPL-3.0-only' = 'GNU LESSER GENERAL PUBLIC LICENSE'; 'GPL-3.0-only' = 'GNU GENERAL PUBLIC LICENSE' }

            $run.Components[0].licenseText | Should -Be 'MIT License'
            $run.Components[0].notice.text | Should -Not -Match 'Lesser General Public License'
        }

        It 'prints the LGPL notice and every text when each choice needs the LGPL' {
            $inventory = New-WebpackInventory @(@{ name = 'grouped-lib'; version = '1.0.0'; license = '(MIT OR Apache-2.0) AND LGPL-3.0-only'; copyright = 'Copyright (c) Grouped'; source = 'https://example.com/grouped' })

            $run = Invoke-Tool -Arguments '-ArtifactPath', $inventory -SpdxTexts @{ MIT = 'MIT License'; 'LGPL-3.0-only' = 'GNU LESSER GENERAL PUBLIC LICENSE'; 'GPL-3.0-only' = 'GNU GENERAL PUBLIC LICENSE' }

            $run.Components[0].licenseText | Should -Be "MIT License`n`nGNU LESSER GENERAL PUBLIC LICENSE"
            $run.Components[0].notice.text | Should -Match 'Lesser General Public License, version 3;'
        }

        It 'names the LGPL version of the choice whose texts it prints, when the first choice has no SPDX text' {
            $inventory = New-WebpackInventory @(@{ name = 'mixed-lib'; version = '1.0.0'; license = '(LicenseRef-NoSpdx AND LGPL-3.0-or-later) OR (MIT AND LGPL-3.0-only)'; copyright = 'Copyright (c) Mixed'; source = 'https://example.com/mixed' })

            $run = Invoke-Tool -Arguments '-ArtifactPath', $inventory -SpdxTexts @{ 'LicenseRef-NoSpdx' = ''; 'LGPL-3.0-or-later' = 'GNU LESSER GENERAL PUBLIC LICENSE, OR LATER'; MIT = 'MIT License'; 'LGPL-3.0-only' = 'GNU LESSER GENERAL PUBLIC LICENSE'; 'GPL-3.0-only' = 'GNU GENERAL PUBLIC LICENSE' }

            $run.Components[0].licenseText | Should -Be "MIT License`n`nGNU LESSER GENERAL PUBLIC LICENSE"
            $run.Components[0].notice.text | Should -Match 'Lesser General Public License, version 3;'
        }

        It 'prints the LGPL text and its notice when the other licence has no SPDX text' {
            $inventory = New-WebpackInventory @(@{ name = 'dual-lib'; version = '1.0.0'; license = 'LGPL-3.0-or-later OR LicenseRef-Commercial'; copyright = 'Copyright (c) Dual'; source = 'https://example.com/dual' })

            $run = Invoke-Tool -Arguments '-ArtifactPath', $inventory -SpdxTexts @{ 'LicenseRef-Commercial' = ''; 'LGPL-3.0-or-later' = 'GNU LESSER GENERAL PUBLIC LICENSE'; 'GPL-3.0-only' = 'GNU GENERAL PUBLIC LICENSE' }

            $run.Components[0].licenseText | Should -Be 'GNU LESSER GENERAL PUBLIC LICENSE'
            $run.Components[0].notice.text | Should -Match 'Lesser General Public License, version 3 or later;'
        }

        It 'prints the LGPL notice when the text the package ships for a choice is the LGPL' {
            $inventory = New-WebpackInventory @(@{ name = 'shipped-lgpl'; version = '1.0.0'; license = 'LGPL-3.0-only OR MIT'; licenseText = 'GNU LESSER GENERAL PUBLIC LICENSE, AS THE PACKAGE SHIPS IT'; copyright = 'Copyright (c) Shipped'; source = 'https://example.com/shipped' })

            $run = Invoke-Tool -Arguments '-ArtifactPath', $inventory -SpdxTexts @{ MIT = 'MIT License'; 'LGPL-3.0-only' = 'GNU LESSER GENERAL PUBLIC LICENSE'; 'GPL-3.0-only' = 'GNU GENERAL PUBLIC LICENSE' }

            $run.Components[0].licenseText | Should -Be 'GNU LESSER GENERAL PUBLIC LICENSE, AS THE PACKAGE SHIPS IT'
            $run.Components[0].notice.text | Should -Match 'GNU Lesser General Public License, version 3'
            @($run.Components[0].sharedTexts.id) | Should -Be @('GPL-3.0')
        }

        It 'adds the LGPL text under its own label when the licence text it prints is not the LGPL' {
            $inventory = New-WebpackInventory @(@{ name = 'gnu-lib'; version = '1.0.0'; license = 'LGPL-3.0-or-later'; licenseText = 'GNU GENERAL PUBLIC LICENSE, AS COPYING GIVES IT'; copyright = 'Copyright (c) Gnu'; source = 'https://example.com/gnu' })

            $run = Invoke-Tool -Arguments '-ArtifactPath', $inventory -SpdxTexts @{ 'LGPL-3.0-only' = 'GNU LESSER GENERAL PUBLIC LICENSE'; 'GPL-3.0-only' = 'GNU GENERAL PUBLIC LICENSE' }

            $run.Components[0].notice.text | Should -Match 'GNU LGPL v3: see \[LGPL-3\.0\]'
            @($run.Components[0].sharedTexts.id) | Should -Be @('GPL-3.0', 'LGPL-3.0')
        }
    }

    Context 'a notices JSON from another run' {
        BeforeAll {
            function New-NoticesJson([string] $Notice) {
                $json = Join-Path $TestDrive "other-$([guid]::NewGuid().ToString('N')).json"
                $lib = @{ ecosystem = 'npm'; name = 'Lib'; version = '1.0.0'; license = 'MIT'; repository = 'https://github.com/test/lib'; copyright = 'Copyright (c) Lib Authors'; licenseText = 'MIT text'; licenseTextOrigin = 'spdx'; notice = @{ text = $Notice } }
                @{ format = 'devolutions-third-party-notices/1'; appName = 'Other App'; appVersion = '1.0.0'; components = @($lib) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $json
                $json
            }
        }

        It 'is reported when a notice names the app it was written for' {
            $json = New-NoticesJson -Notice 'Other App uses Lib, which is licensed under the GNU LGPL.'

            $run = Invoke-Tool -Arguments '-ArtifactPath', $json

            $run.Output | Should -Match '\| 🟡 Warning \| OtherAppNotices \|'
        }

        It 'merges as it is when no notice names that app' {
            $json = New-NoticesJson -Notice 'Lib includes software developed by the Lib authors.'

            $run = Invoke-Tool -Arguments '-ArtifactPath', $json

            $run.Output | Should -Not -Match 'OtherAppNotices'
            ($run.Components | Where-Object name -eq 'Lib').notice.text | Should -Be 'Lib includes software developed by the Lib authors.'
        }
    }

    Context 'an approval for named apps' {
        It 'covers the component in the notices of <Apps> only' -ForEach @(
            @{ Apps = 'Test App'; Approved = $true }
            @{ Apps = 'Other App'; Approved = $false }
        ) {
            $inventory = New-WebpackInventory @(@{ name = 'gated-lib'; version = '1.0.0'; license = 'LicenseRef-Gated'; licenseText = 'GATED TEXT'; copyright = 'Copyright (c) Gated'; source = 'https://example.com/gated' })
            $approval = @{ license = 'LicenseRef-Gated'; licenseTextSha256 = (Get-LicenseTextHash -Text 'GATED TEXT'); apps = @($Apps) }

            $run = Invoke-Tool -Arguments '-ArtifactPath', $inventory -Approvals @{ 'gated-lib' = $approval }

            $run.Output -match 'LicenseNotAllowed' | Should -Be (-not $Approved)
        }
    }

    Context 'an exclusion or an override for named apps' {
        BeforeAll {
            $script:scopedInventory = New-WebpackInventory @(@{ name = 'scoped-lib'; version = '1.0.0'; license = 'Custom'; licenseText = 'SCOPED TEXT'; source = 'https://example.com/scoped' })
        }

        It 'leaves the component out of the notices of <Apps> only' -ForEach @(
            @{ Apps = 'Test App'; Listed = $false }
            @{ Apps = 'Other App'; Listed = $true }
        ) {
            $run = Invoke-Tool -Arguments '-ArtifactPath', $scopedInventory -Excludes @{ 'scoped-lib' = @{ apps = @($Apps) } }

            [bool]($run.Components | Where-Object name -eq 'scoped-lib') | Should -Be $Listed
        }

        It 'keeps a component that a wildcard exclusion lists under except' {
            $run = Invoke-Tool -Arguments '-ArtifactPath', $scopedInventory -Excludes @{ 'scoped-*' = @{ reason = 'Internal'; except = @('scoped-lib') } }

            [bool]($run.Components | Where-Object name -eq 'scoped-lib') | Should -BeTrue
        }

        It 'does not apply an except that belongs to another app''s entry' {
            $excludes = @{ 'scoped-*' = @(@{ reason = 'Internal' }, @{ apps = @('Other App'); except = @('scoped-lib') }) }

            $run = Invoke-Tool -Arguments '-ArtifactPath', $scopedInventory -Excludes $excludes

            [bool]($run.Components | Where-Object name -eq 'scoped-lib') | Should -BeFalse
        }

        It 'applies the parts of an override list that cover the app, in order' {
            $overrides = @{ 'scoped-lib' = @(
                    @{ copyright = 'Copyright (c) Scoped'; license = 'MIT' }
                    @{ apps = @('Test App'); license = 'Apache-2.0' }
                    @{ apps = @('Other App'); license = 'BSD-3-Clause' }) }

            $component = (Invoke-Tool -Arguments '-ArtifactPath', $scopedInventory -Overrides $overrides).Components[0]

            $component.license | Should -Be 'Apache-2.0'
            $component.copyright | Should -Be 'Copyright (c) Scoped'
        }

        It 'does not apply an override for other apps' {
            $overrides = @{ 'scoped-lib' = @{ apps = @('Other App'); license = 'MIT' } }

            (Invoke-Tool -Arguments '-ArtifactPath', $scopedInventory -Overrides $overrides).Components[0].license | Should -Be 'Custom'
        }
    }

    Context 'notices' {
        It 'prints a statement for signed builds only in a signed build' {
            $inventory = New-WebpackInventory @(@{ name = 'signed-lib'; version = '1.0.0'; license = 'MIT'; licenseText = 'MIT License'; source = 'https://example.com/lib' })
            $overrides = @{ 'signed-lib' = @{ statement = @{ apps = @('Test App'); signedOnly = $true; text = 'Signed statement.' } } }

            (Invoke-Tool -Arguments '-ArtifactPath', $inventory, '-Signed' -Overrides $overrides).Components[0].notice.text | Should -Be 'Signed statement.'
            (Invoke-Tool -Arguments '-ArtifactPath', $inventory -Overrides $overrides).Components[0].notice.text | Should -BeNullOrEmpty
        }

        It 'prints legal''s statement before the component''s own NOTICE' {
            $inventory = New-WebpackInventory @(@{ name = 'stated-lib'; version = '1.0.0'; license = 'MIT'; licenseText = 'MIT License'; source = 'https://example.com/lib'; noticeText = 'Its own notice.' })

            $run = Invoke-Tool -Arguments '-ArtifactPath', $inventory -Overrides @{ 'stated-lib' = @{ statement = @{ apps = @('Test App'); text = 'Legal statement for {{app_name}}.' } } }

            $run.Components[0].notice.text | Should -Be "Legal statement for Test App.`n`nIts own notice."
        }

        It 'prints an override''s notices after the NOTICE a package ships' {
            $nuspec = '<package><metadata><id>Sample.Package</id><version>1.0.0</version><license type="expression">MIT</license>' +
                '<copyright>Copyright (c) Sample</copyright><repository url="https://github.com/test/sample" /></metadata></package>'
            $app = New-App -Packages @{ 'Sample.Package/1.0.0' = @{ 'sample.package.nuspec' = $nuspec; 'NOTICE' = 'Package notice.' } }
            Set-Content -LiteralPath (Join-Path $script:tool 'config' 'texts' 'test-bundled.txt') -Value 'Bundled text.'

            $run = Invoke-Tool -Arguments '-ArtifactPath', (Join-Path $app 'TestApp.deps.json') `
                -Overrides @{ 'Sample.Package' = @{ notices = @(@{ name = 'Bundled code'; path = 'config/texts/test-bundled.txt' }) } } `
                -Repositories (New-Repository 'test/sample' -LicenseText 'MIT License')

            ($run.Components | Where-Object name -eq 'Sample.Package').notice.text | Should -Be "Package notice.`n`nBundled code:`nBundled text."
        }

        It 'prints a NOTICE the package and its repository both give once, before an override''s notices' {
            $nuspec = '<package><metadata><id>Shared.Package</id><version>1.0.0</version><license type="expression">MIT</license>' +
                '<copyright>Copyright (c) Shared</copyright><repository url="https://github.com/test/shared" /></metadata></package>'
            $app = New-App -Packages @{ 'Shared.Package/1.0.0' = @{ 'shared.package.nuspec' = $nuspec; 'NOTICE' = 'Shared notice.' } }
            Set-Content -LiteralPath (Join-Path $script:tool 'config' 'texts' 'test-bundled.txt') -Value 'Bundled text.'

            $run = Invoke-Tool -Arguments '-ArtifactPath', (Join-Path $app 'TestApp.deps.json') `
                -Overrides @{ 'Shared.Package' = @{ notices = @(@{ name = 'Bundled code'; path = 'config/texts/test-bundled.txt' }) } } `
                -Repositories (New-Repository 'test/shared' -LicenseText 'MIT License' -NoticeText "Shared notice.`n")

            ($run.Components | Where-Object name -eq 'Shared.Package').notice.text | Should -Be "Shared notice.`n`nBundled code:`nBundled text."
        }
    }

    Context 'the SBOM' {
        BeforeAll {
            function Set-RuntimeConfig([string]$Folder, [string]$Version = '10.0.0') {
                @{ runtimeOptions = @{ frameworks = @(@{ name = 'Microsoft.NETCore.App'; version = $Version }, @{ name = 'Microsoft.WindowsDesktop.App'; version = $Version }) } } |
                    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Folder 'TestApp.runtimeconfig.json')
            }

            function Read-Sbom([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
        }

        It 'is written beside the notices file, with the list in the notices JSON, without -SbomPath' {
            $inventory = New-WebpackInventory @(@{ name = 'sbom-lib'; version = '1.0.0'; license = 'MIT'; licenseText = 'MIT License'; source = 'https://example.com/lib' })

            $run = Invoke-Tool -Arguments '-ArtifactPath', $inventory, '-Sbom'

            $bom = Read-Sbom ([IO.Path]::ChangeExtension($run.NoticesPath, '.sbom.json'))
            $bom.components.name | Should -Contain 'sbom-lib'
            $run.Json.inventory.name | Should -Contain 'sbom-lib'
            $run.Json.inventory[0].PSObject.Properties.Name | Should -Not -Contain 'isShipped'
            $bom.components[0].PSObject.Properties.Name | Should -Not -Contain 'scope'
        }

        It 'is written to -SbomPath, and the notices JSON gets no list' {
            $app = New-App
            $sbomPath = Join-Path $TestDrive "out-$([guid]::NewGuid().ToString('N'))" 'bom.json'

            $run = Invoke-Tool -Arguments '-ArtifactPath', (Join-Path $app 'TestApp.deps.json'), '-Sbom', '-SbomPath', $sbomPath

            (Read-Sbom $sbomPath).components.name | Should -Contain 'TestApp'
            Test-Path -LiteralPath ([IO.Path]::ChangeExtension($run.NoticesPath, '.sbom.json')) | Should -BeFalse
            $run.Json.PSObject.Properties.Name | Should -Not -Contain 'inventory'
        }

        It 'lists the frameworks the app runs on with -SbomRuntime, as required and not shipped, in the SBOM only' {
            $app = New-App
            Set-RuntimeConfig $app
            $sbomPath = Join-Path $TestDrive "out-$([guid]::NewGuid().ToString('N'))" 'bom.json'

            $run = Invoke-Tool -Arguments '-ArtifactPath', (Join-Path $app 'TestApp.deps.json'), '-Sbom', '-SbomPath', $sbomPath, '-SbomRuntime'

            $frameworks = @((Read-Sbom $sbomPath).components | Where-Object name -like 'Microsoft.*.App')
            $frameworks.name | Should -Be @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')
            $frameworks | ForEach-Object {
                $_.version | Should -Be '10.0.0'
                $_.scope | Should -Be 'required'
                $_.properties[0].name | Should -Be 'devolutions:shipped'
                $_.properties[0].value | Should -Be 'false'
            }
            $run.Components.name | Should -Not -Contain 'Microsoft.NETCore.App'
            Get-Content -LiteralPath $run.NoticesPath -Raw | Should -Not -Match 'Microsoft\.NETCore\.App'
            $run.ExitCode | Should -Be 0 -Because $run.Output
        }

        It 'reads the runtimeconfig.json from -BinariesPath, the folder the installer packs' {
            $app = New-App
            Set-RuntimeConfig $app '10.0.0'
            $staged = New-Item -ItemType Directory -Path (Join-Path $TestDrive "staged-$([guid]::NewGuid().ToString('N'))")
            Set-RuntimeConfig $staged '10.0.5'
            $sbomPath = Join-Path $TestDrive "out-$([guid]::NewGuid().ToString('N'))" 'bom.json'

            $null = Invoke-Tool -Arguments '-ArtifactPath', (Join-Path $app 'TestApp.deps.json'), '-BinariesPath', $staged.FullName, '-Sbom', '-SbomPath', $sbomPath, '-SbomRuntime'

            ((Read-Sbom $sbomPath).components | Where-Object name -eq 'Microsoft.NETCore.App').version | Should -Be '10.0.5'
        }

        It 'is written to -SbomPath in a -Check run' {
            $app = New-App
            $sbomPath = Join-Path $TestDrive "out-$([guid]::NewGuid().ToString('N'))" 'bom.json'
            $check = Join-Path $TestDrive "check-$([guid]::NewGuid().ToString('N'))"

            $null = Invoke-Tool -Arguments '-ArtifactPath', (Join-Path $app 'TestApp.deps.json'), '-Check', '-CheckOutputDirectory', $check, '-Sbom', '-SbomPath', $sbomPath

            Test-Path -LiteralPath $sbomPath | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $check 'THIRD-PARTY-NOTICES.sbom.json') | Should -BeFalse
        }

        It 'adds no framework with -SbomRuntime when the app ships the runtime' {
            $app = New-App
            Set-RuntimeConfig $app
            $depsPath = Join-Path $app 'TestApp.deps.json'
            $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json -AsHashtable
            $deps.libraries['runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.0'] = @{ type = 'runtimepack' }
            $deps | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $depsPath
            $sbomPath = Join-Path $TestDrive "out-$([guid]::NewGuid().ToString('N'))" 'bom.json'

            $null = Invoke-Tool -Arguments '-ArtifactPath', $depsPath, '-Sbom', '-SbomPath', $sbomPath, '-SbomRuntime'

            (Read-Sbom $sbomPath).components.name | Should -Not -Contain 'Microsoft.NETCore.App'
        }

        Context 'our own binaries' {
            BeforeAll {
                # Our tool beside the app, named as the internal exclusion matches it, our assembly and a vendor's that only a
                # reference entry lists
                function New-AppWithBinaries {
                    $app = New-App
                    New-OwnAssembly -Path (Join-Path $app 'Devolutions.Tool.dll') -Resources @{}
                    New-OwnAssembly -Path (Join-Path $app 'Own.Reference.dll') -Resources @{}
                    New-OwnAssembly -Path (Join-Path $app 'Vendor.Reference.dll') -Resources @{} -Company 'Vendor Ltd.'
                    $depsPath = Join-Path $app 'TestApp.deps.json'
                    $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json -AsHashtable
                    foreach ($name in 'Own.Reference', 'Vendor.Reference') {
                        $deps.targets['.NETCoreApp,Version=v8.0']["$name/1.0.0"] = @{ runtime = @{ "$name.dll" = @{} } }
                        $deps.libraries["$name/1.0.0"] = @{ type = 'reference' }
                    }
                    $deps | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $depsPath
                    return $depsPath
                }
            }

            It 'lists them with -SbomOwnBinaries, in the SBOM only, and leaves a vendor''s reference out' {
                $depsPath = New-AppWithBinaries
                $sbomPath = Join-Path $TestDrive "out-$([guid]::NewGuid().ToString('N'))" 'bom.json'

                $run = Invoke-Tool -Arguments '-ArtifactPath', $depsPath, '-Sbom', '-SbomPath', $sbomPath, '-SbomOwnBinaries' `
                    -Excludes @{ 'Devolutions.*' = @{ reason = 'Internal Devolutions packages' } }

                $names = (Read-Sbom $sbomPath).components.name
                $names | Should -Contain 'Devolutions.Tool.dll'
                $names | Should -Contain 'Own.Reference.dll'
                $names | Should -Not -Contain 'Vendor.Reference.dll'
                $run.Components.name | Should -Not -Contain 'Devolutions.Tool.dll'
                $run.Components.name | Should -Not -Contain 'Own.Reference.dll'
                $run.Components.name | Should -Not -Contain 'Vendor.Reference.dll'
            }

            It 'leaves the SBOM and the notices as they were without -SbomOwnBinaries' {
                $depsPath = New-AppWithBinaries
                $withPath = Join-Path $TestDrive "out-$([guid]::NewGuid().ToString('N'))" 'bom.json'
                $withoutPath = Join-Path $TestDrive "out-$([guid]::NewGuid().ToString('N'))" 'bom.json'

                $with = Invoke-Tool -Arguments '-ArtifactPath', $depsPath, '-Sbom', '-SbomPath', $withPath, '-SbomOwnBinaries'
                $without = Invoke-Tool -Arguments '-ArtifactPath', $depsPath, '-Sbom', '-SbomPath', $withoutPath

                (Read-Sbom $withoutPath).components.name | Should -Not -Contain 'Devolutions.Tool.dll'
                (Read-Sbom $withoutPath).components.name | Should -Not -Contain 'Own.Reference.dll'
                Get-Content -LiteralPath $without.NoticesPath -Raw | Should -BeExactly (Get-Content -LiteralPath $with.NoticesPath -Raw)
            }
        }

        Context 'what CycloneDX for .NET wrote, with -SbomDetails' {
            BeforeAll {
                # Sample.A has a .nupkg.sha512 and a full .nuspec, Sample.B a damaged hash and no repository. The app depends on
                # Sample.A and on Helper, a project whose file version replaces its deps.json version; Sample.A on Sample.B
                # through Meta.Pkg, a package with no file the SBOM lists, and on a package the app does not ship
                function New-DetailedApp {
                    $nuspecA = '<package><metadata><id>Sample.A</id><version>1.0.0</version><authors>Jane Doe</authors>' +
                        '<description>Sample A.</description><copyright>Copyright (c) Jane Doe</copyright>' +
                        '<license type="expression">MIT</license><repository url="https://github.com/test/sample-a" /></metadata></package>'
                    $nuspecB = '<package><metadata><id>Sample.B</id><version>2.0.0</version><license type="expression">MIT</license>' +
                        '<copyright>Copyright (c) Sample B</copyright><projectUrl>https://example.com/sample-b</projectUrl></metadata></package>'
                    $app = New-App -Packages @{
                        'Sample.A/1.0.0' = @{ 'sample.a.nuspec' = $nuspecA; 'sample.a.1.0.0.nupkg.sha512' = [Convert]::ToBase64String([byte[]](1..64)) }
                        # Valid Base64 of 3 bytes, not a SHA-512
                        'Sample.B/2.0.0' = @{ 'sample.b.nuspec' = $nuspecB; 'sample.b.2.0.0.nupkg.sha512' = 'AQID' }
                    }
                    $depsPath = Join-Path $app 'TestApp.deps.json'
                    $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json -AsHashtable
                    $target = $deps.targets['.NETCoreApp,Version=v8.0']
                    $target['TestApp/1.0.0'].dependencies = @{ 'Sample.A' = '1.0.0'; 'Helper' = '2.5.0' }
                    $target['Sample.A/1.0.0'].dependencies = @{ 'Meta.Pkg' = '1.0.0'; 'Not.Shipped' = '3.0.0' }
                    $target['Meta.Pkg/1.0.0'] = @{ dependencies = @{ 'Sample.B' = '2.0.0' } }
                    $target['Helper/2.5.0'] = @{ runtime = @{ 'Helper.dll' = @{} } }
                    $deps.libraries['Meta.Pkg/1.0.0'] = @{ type = 'package'; path = 'meta.pkg/1.0.0' }
                    $deps.libraries['Helper/2.5.0'] = @{ type = 'project' }
                    $deps | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $depsPath
                    New-OwnAssembly -Path (Join-Path $app 'Helper.dll') -Resources @{}
                    return $depsPath
                }

                function Invoke-Detailed([string[]]$Extra = @()) {
                    $depsPath = New-DetailedApp
                    $sbomPath = Join-Path $TestDrive "out-$([guid]::NewGuid().ToString('N'))" 'bom.json'
                    $run = Invoke-Tool -Arguments (@('-ArtifactPath', $depsPath, '-Sbom', '-SbomPath', $sbomPath) + $Extra) `
                        -Repositories (New-Repository 'test/sample-a' -LicenseText 'MIT License') -SpdxTexts @{ MIT = 'MIT License' }
                    [pscustomobject]@{ Run = $run; Path = $sbomPath; Bom = Read-Sbom $sbomPath }
                }
            }

            It 'gives each package its .nupkg hash and its .nuspec author, description, copyright and repository' {
                $result = Invoke-Detailed '-SbomDetails'

                $a = $result.Bom.components | Where-Object name -eq 'Sample.A'
                $a.hashes[0].alg | Should -Be 'SHA-512'
                $a.hashes[0].content | Should -Be ([Convert]::ToHexString([byte[]](1..64)))
                $a.author | Should -Be 'Jane Doe'
                $a.description | Should -Be 'Sample A.'
                $a.copyright | Should -Be 'Copyright (c) Jane Doe'
                ($a.externalReferences | Where-Object type -eq 'vcs').url | Should -Be 'https://github.com/test/sample-a'
                $app = ($result.Bom.components | Where-Object name -eq 'TestApp').PSObject.Properties.Name
                $app | Should -Not -Contain 'hashes'
                $app | Should -Not -Contain 'isProject'
            }

            It 'adds the timestamp, a serial number and the tool to the metadata' {
                $result = Invoke-Detailed '-SbomDetails'
                $bom = $result.Bom

                $bom.serialNumber | Should -Match '^urn:uuid:[0-9a-f-]{36}$'
                # Read as text: ConvertFrom-Json turns the date into a DateTime
                Get-Content -LiteralPath $result.Path -Raw | Should -Match '"timestamp":\s*"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ"'
                $bom.metadata.tools.components[0].name | Should -Match 'ThirdPartyLicenses'
                $bom.metadata.component.name | Should -Be 'Test App'
            }

            It 'writes the deps.json graph between the components it lists, from the app down, through a package it does not list' {
                $bom = (Invoke-Detailed '-SbomDetails').Bom

                $refs = @{}
                foreach ($component in $bom.components) { $refs[$component.name] = $component.'bom-ref' }
                # Only Windows reads the file version that replaces the deps.json one, so only there do the two differ
                if ($IsWindows) { ($bom.components | Where-Object name -eq 'Helper').version | Should -Not -Be '2.5.0' }
                $bom.components.name | Should -Not -Contain 'Meta.Pkg'
                $bom.dependencies.Count | Should -Be ($bom.components.Count + 1)
                ($bom.dependencies | Where-Object ref -eq $bom.metadata.component.'bom-ref').dependsOn | Should -Be @($refs['TestApp'])
                @(($bom.dependencies | Where-Object ref -eq $refs['TestApp']).dependsOn | Sort-Object) | Should -Be @(@($refs['Helper'], $refs['Sample.A']) | Sort-Object)
                ($bom.dependencies | Where-Object ref -eq $refs['Sample.A']).dependsOn | Should -Be @($refs['Sample.B'])
                ($bom.dependencies | Where-Object ref -eq $refs['Sample.B']).PSObject.Properties.Name | Should -Not -Contain 'dependsOn'
            }

            It 'only warns for a package with no usable hash in the NuGet cache, and leaves its hash out' {
                $result = Invoke-Detailed '-SbomDetails'

                $result.Run.ExitCode | Should -Be 0 -Because $result.Run.Output
                $result.Run.Output | Should -Match '-SbomDetails: 1 NuGet package\(s\) have no \.nupkg\.sha512 .*Sample\.B'
                ($result.Bom.components | Where-Object name -eq 'Sample.B').PSObject.Properties.Name | Should -Not -Contain 'hashes'
            }

            It 'leaves the SBOM as it was without it' {
                $bom = (Invoke-Detailed).Bom

                $bom.PSObject.Properties.Name | Should -Not -Contain 'serialNumber'
                $bom.PSObject.Properties.Name | Should -Not -Contain 'dependencies'
                $bom.metadata.PSObject.Properties.Name | Should -Be @('component')
                foreach ($field in 'hashes', 'author', 'description', 'copyright') {
                    ($bom.components | Where-Object name -eq 'Sample.A').PSObject.Properties.Name | Should -Not -Contain $field
                }
            }
        }

        It 'only warns when the runtimeconfig.json is missing' {
            $app = New-App
            $sbomPath = Join-Path $TestDrive "out-$([guid]::NewGuid().ToString('N'))" 'bom.json'

            $run = Invoke-Tool -Arguments '-ArtifactPath', (Join-Path $app 'TestApp.deps.json'), '-Sbom', '-SbomPath', $sbomPath, '-SbomRuntime'

            $run.ExitCode | Should -Be 0 -Because $run.Output
            $run.Output | Should -Match '-SbomRuntime: .*TestApp\.runtimeconfig\.json.* not found'
            (Read-Sbom $sbomPath).components.name | Should -Not -Contain 'Microsoft.NETCore.App'
        }

        It 'stops on <Name>' -ForEach @(
            @{ Name = '-SbomPath without -Sbom'; Arguments = @('-SbomPath', 'bom.json'); Message = '-SbomPath needs -Sbom' }
            @{ Name = '-SbomRuntime without -SbomPath'; Arguments = @('-Sbom', '-SbomRuntime'); Message = '-SbomRuntime needs -SbomPath' }
            @{ Name = '-SbomRuntime with -IncludeRuntime'; Arguments = @('-Sbom', '-SbomPath', 'bom.json', '-SbomRuntime', '-IncludeRuntime')
                Message = '-SbomRuntime and -IncludeRuntime cannot be used together' }
            @{ Name = '-SbomOwnBinaries without -SbomPath'; Arguments = @('-Sbom', '-SbomOwnBinaries'); Message = '-SbomOwnBinaries needs -SbomPath' }
            @{ Name = '-SbomDetails without -Sbom'; Arguments = @('-SbomDetails'); Message = '-SbomDetails needs -Sbom' }
        ) {
            $app = New-App

            $run = Invoke-Tool -Arguments (@('-ArtifactPath', (Join-Path $app 'TestApp.deps.json')) + $Arguments)

            $run.ExitCode | Should -Not -Be 0
            $run.Output | Should -Match ([regex]::Escape($Message))
        }
    }
}
