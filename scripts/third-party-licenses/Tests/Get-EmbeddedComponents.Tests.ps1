Describe 'Embedded content reader' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Get-EmbeddedComponents.ps1')
        . (Join-Path $privateDirectory 'Get-FileVersionProperties.ps1')
        . (Join-Path $privateDirectory 'Get-AngularComponents.ps1')

        $script:assetList = @{ include = @('*.min.css', '*.js', '*.zip', '*.wordlist.txt', '*.ttf'); exclude = @('*.Devolutions.*'); bundles = @('*.markdown-it.js') }

        function ConvertTo-Bytes {
            param([Parameter(Mandatory)][string]$Text)

            [Text.Encoding]::UTF8.GetBytes($Text)
        }

        function New-Sidecar {
            param(
                [string]$Component = 'Bootstrap 5.3.3',
                [string]$Source = 'https://github.com/twbs/bootstrap',
                [string]$License = 'MIT',
                [string]$Text = "The MIT License (MIT)`n`nCopyright (c) 2011-2024 The Bootstrap Authors"
            )

            ConvertTo-Bytes "Component : $Component`nSource    : $Source`nLicense   : $License`n`n$Text`n"
        }

        function New-AngularList {
            param([string[]]$Packages)

            ($Packages | ForEach-Object { "Package: $_`nLicense: `"MIT`"`n`nCopyright (c) $_ authors`n" }) -join ('-' * 80 + "`n")
        }

        function New-Zip {
            param([hashtable]$Entries = @{ 'index.js' = 'console.log(1);' })

            $stream = [IO.MemoryStream]::new()
            $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
            foreach ($entry in $Entries.GetEnumerator()) {
                $writer = [IO.StreamWriter]::new($zip.CreateEntry($entry.Key).Open())
                $writer.Write($entry.Value)
                $writer.Dispose()
            }

            $zip.Dispose()
            return $stream.ToArray()
        }

        function Invoke-Reader {
            param([Parameter(Mandatory)][Collections.Specialized.OrderedDictionary]$Resources)

            Mock Get-OwnAssemblies { 'C:\app\RemoteDesktopManager.Assets.dll' }
            Mock Get-AssemblyResources { $Resources }.GetNewClosure()

            $script:issues = [Collections.Generic.List[object]]::new()
            @(Get-EmbeddedComponents -Directory 'C:\app' -AssetList $script:assetList -Issues $script:issues)
        }
    }

    Context 'sidecars' {
        It 'describes a listed asset from its sidecar' {
            $components = Invoke-Reader ([ordered]@{
                'Assets.bootstrap.min.css'             = (ConvertTo-Bytes 'body{}')
                'Assets.bootstrap.min.css.LICENSE.txt' = (New-Sidecar)
            })

            $components.Count | Should -Be 1
            $components[0].Name | Should -Be 'Bootstrap'
            $components[0].Version | Should -Be '5.3.3'
            $components[0].License | Should -Be 'MIT'
            $components[0].Source | Should -Be 'https://github.com/twbs/bootstrap'
            $components[0].LicenseText | Should -BeLike 'The MIT License*Bootstrap Authors'
            $components[0].Ecosystem | Should -Be 'embedded'
            $components[0].Path | Should -Be 'RemoteDesktopManager.Assets.dll!Assets.bootstrap.min.css'
            $script:issues.Count | Should -Be 0
        }

        It 'gives one block per upstream project when one file carries two' {
            $components = Invoke-Reader ([ordered]@{
                'Assets.bootstrap.min.css'                       = (ConvertTo-Bytes 'body{}')
                'Assets.bootstrap.min.css.LICENSE.txt'           = (New-Sidecar)
                'Assets.bootstrap.min.css.normalize.LICENSE.txt' = (New-Sidecar -Component 'normalize.css 8.0.1' -Source 'https://github.com/necolas/normalize.css')
            })

            @($components.Name) | Should -Be @('Bootstrap', 'normalize.css')
        }

        It 'gives a sidecar to the longest resource it extends' {
            $components = Invoke-Reader ([ordered]@{
                'Assets.app.js'                 = (ConvertTo-Bytes '1')
                'Assets.app.js.map'             = (ConvertTo-Bytes '{}')
                'Assets.app.js.map.LICENSE.txt' = (New-Sidecar -Component 'SourceMapLib 1.0' -Source 'https://example.com/smlib')
            })

            @($components.Path) | Should -Be @('RemoteDesktopManager.Assets.dll!Assets.app.js.map')
            @($script:issues.Kind) | Should -Be @('UnattributedAsset')
        }

        It 'keeps a name with spaces and no version' {
            $components = Invoke-Reader ([ordered]@{
                'Assets.Diceware.wordlist.txt'             = (ConvertTo-Bytes 'abacus')
                'Assets.Diceware.wordlist.txt.LICENSE.txt' = (New-Sidecar -Component "EFF's Long Wordlist" -License 'CC-BY-3.0')
            })

            $components[0].Name | Should -Be "EFF's Long Wordlist"
            $components[0].Version | Should -Be ''
        }

        It 'reads unversioned as the recorded version' {
            $components = Invoke-Reader ([ordered]@{
                'Assets.x.js'             = (ConvertTo-Bytes '1')
                'Assets.x.js.LICENSE.txt' = (New-Sidecar -Component 'Some Lib unversioned')
            })

            $components[0].Name | Should -Be 'Some Lib'
            $components[0].Version | Should -Be 'unversioned'
        }

        It 'lists an asset that has a sidecar even when the asset list misses it' {
            $components = Invoke-Reader ([ordered]@{
                'Assets.OpenSans.woff2'             = (ConvertTo-Bytes 'font')
                'Assets.OpenSans.woff2.LICENSE.txt' = (New-Sidecar -Component 'Open Sans 3.000' -License 'OFL-1.1')
            })

            $components[0].Name | Should -Be 'Open Sans'
        }

        It 'reads a sidecar saved with a byte order mark and Windows line ends' {
            $bytes = [byte[]](0xEF, 0xBB, 0xBF) + (ConvertTo-Bytes "Component : Lib 1.0`r`nSource    : https://example.com`r`nLicense   : MIT`r`n`r`nText`r`n")
            $components = Invoke-Reader ([ordered]@{ 'Assets.lib.js' = (ConvertTo-Bytes '1'); 'Assets.lib.js.LICENSE.txt' = $bytes })

            $components[0].Name | Should -Be 'Lib'
            $components[0].LicenseText | Should -Be 'Text'
        }

        It 'leaves the text to the tool when the sidecar has only its header' {
            $sidecar = ConvertTo-Bytes "Component : Bootstrap 3.3.7`nSource    : https://github.com/twbs/bootstrap`nLicense   : MIT`nCopyright : Copyright 2011-2016 Twitter, Inc.`nCommit    : v3.3.7`n"
            $components = Invoke-Reader ([ordered]@{ 'Assets.bootstrap.min.css' = (ConvertTo-Bytes 'body{}'); 'Assets.bootstrap.min.css.LICENSE.txt' = $sidecar })

            $components[0].Name | Should -Be 'Bootstrap'
            $components[0].LicenseText | Should -Be ''
            $components[0].LicenseTextOrigin | Should -Be 'none'
            $components[0].Copyright | Should -Be 'Copyright 2011-2016 Twitter, Inc.'
            $components[0].RepositoryCommit | Should -Be 'v3.3.7'
            $script:issues.Count | Should -Be 0
        }

        It 'takes a header without copyright or commit, and no trailing line end' {
            $components = Invoke-Reader ([ordered]@{
                'Assets.x.js'             = (ConvertTo-Bytes '1')
                'Assets.x.js.LICENSE.txt' = (ConvertTo-Bytes "Component : Lib 2.0`nSource    : https://github.com/o/lib`nLicense   : MIT")
            })

            $components[0].Name | Should -Be 'Lib'
            $components[0].Copyright | Should -Be ''
            $components[0].RepositoryCommit | Should -Be ''
            $components[0].LicenseText | Should -Be ''
        }

        It 'keeps a full text after the optional lines' {
            $sidecar = ConvertTo-Bytes "Component : Words unversioned`nSource    : https://example.org/words`nLicense   : CC-BY-3.0`nCopyright : Copyright Example`n`nFull terms here.`n"
            $components = Invoke-Reader ([ordered]@{ 'Assets.list.wordlist.txt' = (ConvertTo-Bytes 'a'); 'Assets.list.wordlist.txt.LICENSE.txt' = $sidecar })

            $components[0].LicenseText | Should -Be 'Full terms here.'
            $components[0].LicenseTextOrigin | Should -Be 'sidecar'
            $components[0].Copyright | Should -Be 'Copyright Example'
        }

        It 'reports a sidecar without the header and leaves the asset unattributed' {
            $components = Invoke-Reader ([ordered]@{
                'Assets.x.js'             = (ConvertTo-Bytes '1')
                'Assets.x.js.LICENSE.txt' = (ConvertTo-Bytes 'MIT License, no header')
            })

            @($script:issues.Kind) | Should -Be @('InvalidSidecar', 'UnattributedAsset')
            $components | Should -BeNullOrEmpty
        }

        It 'reports an empty sidecar, or one with only a byte order mark, instead of stopping' {
            foreach ($sidecar in [byte[]]@(), [byte[]](0xEF, 0xBB, 0xBF))
            {
                $components = Invoke-Reader ([ordered]@{ 'Assets.x.js' = (ConvertTo-Bytes '1'); 'Assets.x.js.LICENSE.txt' = $sidecar })

                @($script:issues.Kind) | Should -Be @('InvalidSidecar', 'UnattributedAsset')
                $components | Should -BeNullOrEmpty
            }
        }

        It 'reports a sidecar whose asset is not in the assembly' {
            Invoke-Reader ([ordered]@{ 'Assets.gone.js.LICENSE.txt' = (New-Sidecar) }) | Should -BeNullOrEmpty

            @($script:issues.Kind) | Should -Be @('OrphanSidecar')
        }
    }

    Context 'asset list' {
        It 'reports a matching asset with no sidecar and gives it no block' {
            Invoke-Reader ([ordered]@{ 'Assets.jsencryption.js' = (ConvertTo-Bytes '1') }) | Should -BeNullOrEmpty

            @($script:issues.Kind) | Should -Be @('UnattributedAsset')
        }

        It 'ignores resources the list does not match' {
            Invoke-Reader ([ordered]@{ 'Assets.Strings.resources' = (ConvertTo-Bytes '1') }) | Should -BeNullOrEmpty

            $script:issues.Count | Should -Be 0
        }

        It 'ignores excluded resources' {
            Invoke-Reader ([ordered]@{ 'Assets.Devolutions.editor.js' = (ConvertTo-Bytes '1') }) | Should -BeNullOrEmpty

            $script:issues.Count | Should -Be 0
        }
    }

    Context 'bundles' {
        It 'reads a 3rdpartylicenses.txt inside the zip' {
            $zip = New-Zip @{ 'index.js' = '1'; '3rdpartylicenses.txt' = (New-AngularList 'jquery', 'jquery-ui') }
            $components = Invoke-Reader ([ordered]@{ 'Assets.formbuilder.zip' = $zip })

            @($components.Name) | Should -Be @('jquery', 'jquery-ui')
            $components[0].Ecosystem | Should -Be 'npm'
            $components[0].Path | Should -Be 'RemoteDesktopManager.Assets.dll!Assets.formbuilder.zip'
            $script:issues.Count | Should -Be 0
        }

        It 'reads an empty bundle list as no components' {
            $zip = New-Zip @{ 'index.js' = '1'; '3rdpartylicenses.txt' = '' }

            $components = Invoke-Reader ([ordered]@{ 'Assets.formbuilder.zip' = $zip })

            $components | Should -BeNullOrEmpty
        }

        It 'reads a bundle list as text, without a temp file or a node_modules folder' {
            Mock Get-AngularComponents { }
            Mock New-TemporaryFile { throw 'no temp file expected' }
            $zip = New-Zip @{ 'index.js' = '1'; '3rdpartylicenses.txt' = (New-AngularList 'jquery') }

            $null = Invoke-Reader ([ordered]@{ 'Assets.formbuilder.zip' = $zip })

            Should -Invoke Get-AngularComponents -Times 1 -Exactly -ParameterFilter { $Text -match 'Package: jquery' }
        }

        It 'never makes blocks from the bundler banners' {
            $zip = New-Zip @{ 'index.js' = '1'; 'index.js.LICENSE.txt' = '/*! @license MIT prism */' }
            Invoke-Reader ([ordered]@{ 'Assets.tinymce.zip' = $zip }) | Should -BeNullOrEmpty

            @($script:issues.Kind) | Should -Be @('UnattributedAsset')
        }

        It 'reads the stamped list beside the zip' {
            $zip = New-Zip
            $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($zip)).ToLowerInvariant()
            $components = Invoke-Reader ([ordered]@{
                'Assets.toastui.zip'                      = $zip
                'Assets.toastui.zip.3rdpartylicenses.txt' = (ConvertTo-Bytes "sha256:$sha`n$(New-AngularList 'prosemirror-model')")
            })

            @($components.Name) | Should -Be @('prosemirror-model')
            $script:issues.Count | Should -Be 0
        }

        It 'reads the stamped list beside a bundle that is not a zip' {
            $script = ConvertTo-Bytes '!function(){}()'
            $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($script)).ToLowerInvariant()
            $components = Invoke-Reader ([ordered]@{
                'Assets.markdown-it.js'                      = $script
                'Assets.markdown-it.js.3rdpartylicenses.txt' = (ConvertTo-Bytes "sha256:$sha`n$(New-AngularList 'markdown-it', 'linkify-it')")
            })

            @($components.Name) | Should -Be @('markdown-it', 'linkify-it')
            $script:issues.Count | Should -Be 0
        }

        It 'reports a zip it cannot read and goes on' {
            Invoke-Reader ([ordered]@{ 'Assets.broken.zip' = (ConvertTo-Bytes 'not a zip') }) | Should -BeNullOrEmpty

            @($script:issues.Kind) | Should -Be @('UnreadableBundle', 'UnattributedAsset')
        }

        It 'reports a zip whose list cannot be read, and still reads the stamped list' {
            $zip = New-Zip @{ '3rdpartylicenses.txt' = (New-AngularList 'jquery') }
            $zip[0] = 0
            $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($zip)).ToLowerInvariant()
            $components = Invoke-Reader ([ordered]@{
                'Assets.formbuilder.zip'                      = $zip
                'Assets.formbuilder.zip.3rdpartylicenses.txt' = (ConvertTo-Bytes "sha256:$sha`n$(New-AngularList 'prosemirror-model')")
            })

            @($components.Name) | Should -Be @('prosemirror-model')
            @($script:issues.Kind) | Should -Be @('UnreadableBundle')
        }

        It 'gives no block to a listed bundle without its list' {
            Invoke-Reader ([ordered]@{ 'Assets.markdown-it.js' = (ConvertTo-Bytes '!function(){}()') }) | Should -BeNullOrEmpty

            @($script:issues.Kind) | Should -Be @('UnattributedAsset')
        }

        It 'warns when the zip no longer matches its stamp, and still reads the list' {
            $components = Invoke-Reader ([ordered]@{
                'Assets.toastui.zip'                      = (New-Zip)
                'Assets.toastui.zip.3rdpartylicenses.txt' = (ConvertTo-Bytes "sha256:0000`n$(New-AngularList 'prosemirror-model')")
            })

            @($components.Name) | Should -Be @('prosemirror-model')
            @($script:issues.Kind) | Should -Be @('BundleStampMismatch')
        }
    }

    Context 'Avalonia resource bundles' {
        BeforeAll {
            function New-AvaloniaResources {
                param(
                    [Parameter(Mandatory)][Collections.Specialized.OrderedDictionary]$Files,
                    [int]$Version = 2
                )

                $index = [IO.MemoryStream]::new()
                $indexWriter = [IO.BinaryWriter]::new($index)
                $indexWriter.Write([int]$Version)
                $indexWriter.Write([int]$Files.Count)
                $offset = 0
                foreach ($file in $Files.GetEnumerator()) {
                    $indexWriter.Write([string]$file.Key)
                    $indexWriter.Write([int]$offset)
                    $indexWriter.Write([int]$file.Value.Length)
                    $offset += $file.Value.Length
                }

                $indexWriter.Flush()
                $container = [IO.MemoryStream]::new()
                $writer = [IO.BinaryWriter]::new($container)
                $writer.Write([int]$index.Length)
                $writer.Write($index.ToArray())
                foreach ($file in $Files.GetEnumerator()) { $writer.Write([byte[]]$file.Value) }
                $writer.Flush()
                return $container.ToArray()
            }
        }

        It 'lists a font inside the bundle and reads its sidecar there' {
            $bundle = New-AvaloniaResources ([ordered]@{
                '/!AvaloniaResourceXamlInfo'             = (ConvertTo-Bytes '<xaml/>')
                '/Assets/Fonts/Inter.ttf'                = [byte[]](0, 1, 0, 0, 9, 9)
                '/Assets/Fonts/Inter.ttf.LICENSE.txt'    = (New-Sidecar -Component 'Inter 4.0' -Source 'https://github.com/rsms/inter' -License 'OFL-1.1')
                '/Assets/Fonts/Unknown.ttf'              = [byte[]](0, 1, 0, 0)
            })
            $components = Invoke-Reader ([ordered]@{ '!AvaloniaResources' = $bundle })

            @($components.Name) | Should -Be @('Inter')
            $components[0].Path | Should -Be 'RemoteDesktopManager.Assets.dll!!AvaloniaResources/Assets/Fonts/Inter.ttf'
            @($script:issues.Kind) | Should -Be @('UnattributedAsset')
        }

        It 'hands each file its exact bytes' {
            $resources = [ordered]@{ '!AvaloniaResources' = (New-AvaloniaResources ([ordered]@{ '/a.bin' = [byte[]](1, 2); '/b.bin' = [byte[]](3, 4, 5) })) }
            $issues = [Collections.Generic.List[object]]::new()

            Expand-AvaloniaResources -Resources $resources -AssemblyName 'X.dll' -Issues $issues

            @($resources.Keys) | Should -Be @('!AvaloniaResources/a.bin', '!AvaloniaResources/b.bin')
            $resources['!AvaloniaResources/b.bin'] | Should -Be @(3, 4, 5)
        }

        It 'warns and leaves the bundle closed when its format is unknown' {
            $bundle = New-AvaloniaResources ([ordered]@{ '/Assets/Fonts/Inter.ttf' = [byte[]](0, 1, 0, 0) }) -Version 3
            Invoke-Reader ([ordered]@{ '!AvaloniaResources' = $bundle }) | Should -BeNullOrEmpty

            @($script:issues.Kind) | Should -Be @('UnreadableAvaloniaResources')
        }
    }

    Context 'finding our own assemblies' {
        BeforeAll {
            Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')

            # The company name is read from the file's version resource, which Add-Type does not write
            function New-TestAssembly {
                param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Company)

                $source = "[assembly: System.Reflection.AssemblyCompany(""$Company"")] public class Probe {}"
                $compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(
                    [IO.Path]::GetFileNameWithoutExtension($Path),
                    [Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source)),
                    [Microsoft.CodeAnalysis.MetadataReference[]]@([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile([object].Assembly.Location)),
                    [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary))
                $resources = $compilation.CreateDefaultWin32Resources($true, $true, $null, $null)
                $stream = [IO.File]::Create($Path)
                try { $result = $compilation.Emit($stream, $null, $null, $resources) } finally { $stream.Dispose() }
                if (-not $result.Success) { throw ($result.Diagnostics -join "`n") }
            }
        }

        It 'lists our libraries and executables, and nothing else' {
            $app = Join-Path $TestDrive 'app'
            $ref = New-Item -ItemType Directory -Path (Join-Path $app 'ref')
            New-TestAssembly -Path (Join-Path $TestDrive 'own.bin') -Company 'Devolutions inc.'
            New-TestAssembly -Path (Join-Path $TestDrive 'other.bin') -Company 'Contoso'
            foreach ($target in 'RemoteDesktopManager.Assets.dll', 'Devolutions.Updater.exe', 'notes.txt') {
                Copy-Item (Join-Path $TestDrive 'own.bin') (Join-Path $app $target)
            }
            Copy-Item (Join-Path $TestDrive 'own.bin') (Join-Path $ref 'RemoteDesktopManager.Assets.dll')
            foreach ($target in 'gsudo.exe', 'Newtonsoft.Json.dll') {
                Copy-Item (Join-Path $TestDrive 'other.bin') (Join-Path $app $target)
            }

            @(Get-OwnAssemblies -Directory $app | Split-Path -Leaf | Sort-Object) | Should -Be @('Devolutions.Updater.exe', 'RemoteDesktopManager.Assets.dll')
        }

        It 'finds them in a folder that sits under one named ref' {
            $app = New-Item -ItemType Directory -Path (Join-Path $TestDrive 'ref' 'app')
            New-TestAssembly -Path (Join-Path $app 'RemoteDesktopManager.Assets.dll') -Company 'Devolutions inc.'

            @(Get-OwnAssemblies -Directory $app.FullName | Split-Path -Leaf) | Should -Be @('RemoteDesktopManager.Assets.dll')
        }
    }

    Context 'reading resources from an assembly' {
        It 'returns the embedded resources of a real assembly' {
            $assembly = [object].Assembly
            $resources = Get-AssemblyResources -Path $assembly.Location

            @($resources.Keys | Sort-Object) | Should -Be @($assembly.GetManifestResourceNames() | Sort-Object)
            foreach ($name in $resources.Keys) {
                $resources[$name].Length | Should -Be $assembly.GetManifestResourceStream($name).Length
            }
        }
    }
}
