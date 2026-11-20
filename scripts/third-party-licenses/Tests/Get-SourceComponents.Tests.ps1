Describe 'Copied source reader' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Get-EmbeddedComponents.ps1')
        . (Join-Path $privateDirectory 'Get-FileVersionProperties.ps1')
        . (Join-Path $privateDirectory 'Get-SourceComponents.ps1')
        . (Join-Path $privateDirectory 'Get-DotnetDepsComponents.ps1')

        function New-TestPdb {
            param(
                [Parameter(Mandatory)][string]$Path,
                [Parameter(Mandatory)][string[]]$Documents
            )

            $metadata = [Reflection.Metadata.Ecma335.MetadataBuilder]::new()
            foreach ($document in $Documents) {
                $null = $metadata.AddDocument($metadata.GetOrAddDocumentName($document), $metadata.GetOrAddGuid([guid]::Empty), $metadata.GetOrAddBlob([byte[]]@()), $metadata.GetOrAddGuid([guid]::Empty))
            }

            $rowCounts = [Collections.Immutable.ImmutableArray[int]]::Empty.AddRange([int[]]::new([Reflection.Metadata.Ecma335.MetadataTokens]::TableCount))
            $builder = [Reflection.Metadata.Ecma335.PortablePdbBuilder]::new($metadata, $rowCounts, [Reflection.Metadata.MethodDefinitionHandle]::new())
            $blob = [Reflection.Metadata.BlobBuilder]::new()
            $null = $builder.Serialize($blob)
            [IO.File]::WriteAllBytes($Path, $blob.ToArray())
        }

        function New-SourceFile {
            param(
                [Parameter(Mandatory)][string]$Name,
                [string[]]$Lines = @('namespace Foo;')
            )

            $path = Join-Path $TestDrive 'repo' $Name
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
            Set-Content -LiteralPath $path -Value $Lines
            return $path
        }

        $script:noticeLines = @(
            'using System;'
            ''
            '// THIRD-PARTY-NOTICE'
            '// Component : OTP Sharp 1.0'
            '// Source    : https://github.com/kspearrin/Otp.NET'
            '// License   : MIT'
            '//'
            '// MIT License'
            '//'
            '// Copyright (c) 2012 Devin Martin'
            '// END THIRD-PARTY-NOTICE'
            'namespace OtpSharp;'
        )

        function Invoke-Reader {
            param([Parameter(Mandatory)][string[]]$Documents, [object[]]$DepsComponents = @([pscustomobject]@{ Name = 'Listed.Package'; Version = '1.0.0' }), [string[]]$ExcludeExceptions = @())

            $binaries = Join-Path $TestDrive 'bin'
            New-Item -ItemType Directory -Force -Path $binaries | Out-Null
            New-TestPdb -Path (Join-Path $binaries 'RemoteDesktopManager.Core.pdb') -Documents $Documents
            Mock Get-OwnAssemblies { Join-Path $TestDrive 'bin' 'RemoteDesktopManager.Core.dll' }

            $script:issues = [Collections.Generic.List[object]]::new()
            @(Get-SourceComponents -Directory $binaries -DepsComponents $DepsComponents -PackagesRoot (Join-Path $TestDrive 'nuget') -Excludes @('Devolutions.*') -ExcludeExceptions $ExcludeExceptions -Issues $script:issues)
        }
    }

    Context 'repository files' {
        It 'reads the notice block of a copied file' {
            $components = Invoke-Reader @(New-SourceFile 'OtpSharp/Totp.cs' $script:noticeLines)

            $components.Count | Should -Be 1
            $components[0].Name | Should -Be 'OTP Sharp'
            $components[0].Version | Should -Be '1.0'
            $components[0].License | Should -Be 'MIT'
            $components[0].Source | Should -Be 'https://github.com/kspearrin/Otp.NET'
            $components[0].LicenseText | Should -Be "MIT License`n`nCopyright (c) 2012 Devin Martin"
            $components[0].Ecosystem | Should -Be 'source'
            $components[0].LicenseTextOrigin | Should -Be 'source'
            $components[0].Path | Should -Be 'RemoteDesktopManager.Core.dll!Totp.cs'
            $script:issues.Count | Should -Be 0
        }

        It 'reads a header-only notice block and leaves the text to the tool' {
            $lines = @('// THIRD-PARTY-NOTICE', '// Component : ZipStorer 3.4', '// Source    : https://github.com/jaime-olivares/zipstorer', '// License   : MIT', '// Commit    : v3.4', '// END THIRD-PARTY-NOTICE')
            $components = Invoke-Reader @(New-SourceFile 'ZipStorer.cs' $lines)

            $components[0].Name | Should -Be 'ZipStorer'
            $components[0].LicenseText | Should -Be ''
            $components[0].RepositoryCommit | Should -Be 'v3.4'
        }

        It 'ignores our own files' {
            Invoke-Reader @(New-SourceFile 'Ours.cs') | Should -BeNullOrEmpty

            $script:issues.Count | Should -Be 0
        }

        It 'reads only the top of a file that has no marker' {
            $script:longFile = New-SourceFile 'Long.cs' @(1..500 | ForEach-Object { "// line $_" })
            Mock Get-Content { 'namespace Foo;' } -ParameterFilter { $LiteralPath -eq $script:longFile }

            Invoke-Reader @($script:longFile) | Should -BeNullOrEmpty

            Should -Invoke Get-Content -Times 1 -Exactly -ParameterFilter { $LiteralPath -eq $script:longFile }
            Should -Invoke Get-Content -Times 1 -Exactly -ParameterFilter { $LiteralPath -eq $script:longFile -and $TotalCount -eq 200 }
        }

        It 'ignores a marker past the top of the file' {
            $lines = @(1..250 | ForEach-Object { "// line $_" }) + $script:noticeLines
            Invoke-Reader @(New-SourceFile 'Late.cs' $lines) | Should -BeNullOrEmpty
        }

        It 'reports a marker that is never closed' {
            Invoke-Reader @(New-SourceFile 'Open.cs' ($script:noticeLines | Where-Object { $_ -ne '// END THIRD-PARTY-NOTICE' })) | Should -BeNullOrEmpty

            @($script:issues.Kind) | Should -Be @('InvalidSourceNotice')
        }

        It 'reads every notice block of a file' {
            $second = @('// THIRD-PARTY-NOTICE', '// Component : Base32 2.0', '// Source    : https://example.com/base32', '// License   : MIT', '//', '// MIT License', '// END THIRD-PARTY-NOTICE')

            $components = Invoke-Reader @(New-SourceFile 'Two.cs' ($script:noticeLines + $second))

            @($components.Name) | Should -Be @('OTP Sharp', 'Base32')
        }

        It 'reports a marker without the header' {
            Invoke-Reader @(New-SourceFile 'Bad.cs' @('// THIRD-PARTY-NOTICE', '// MIT License', '// END THIRD-PARTY-NOTICE')) | Should -BeNullOrEmpty

            @($script:issues.Kind) | Should -Be @('InvalidSourceNotice')
        }

        It 'reports an empty marker block' {
            Invoke-Reader @(New-SourceFile 'Empty.cs' @('// THIRD-PARTY-NOTICE', '// END THIRD-PARTY-NOTICE')) | Should -BeNullOrEmpty

            @($script:issues.Kind) | Should -Be @('InvalidSourceNotice')
        }

        It 'reads a file listed by several documents once' {
            $path = New-SourceFile 'OtpSharp/Hotp.cs' $script:noticeLines

            @(Invoke-Reader @($path, $path)).Count | Should -Be 1
        }

        It 'skips generated files under obj/' {
            Invoke-Reader @('Z:\repo\Project\obj\Release\net10.0\Generator\File.g.cs') | Should -BeNullOrEmpty

            $script:issues.Count | Should -Be 0
        }

        It 'reports source files that are not on this machine, once' {
            Invoke-Reader @('Z:\build\agent\A.cs', 'Z:\build\agent\B.cs') | Should -BeNullOrEmpty

            @($script:issues.Kind) | Should -Be @('SourceNotFound')
            $script:issues[0].Id | Should -Be '2 files'
        }

        It 'reports own assemblies that have no PDB beside them, once' {
            $binaries = Join-Path $TestDrive 'bin'
            New-Item -ItemType Directory -Force -Path $binaries | Out-Null
            Mock Get-OwnAssemblies { @((Join-Path $TestDrive 'bin' 'Devolutions.Updater.exe'), (Join-Path $TestDrive 'bin' 'Other.Own.dll')) }
            $script:issues = [Collections.Generic.List[object]]::new()

            @(Get-SourceComponents -Directory $binaries -DepsComponents @() -PackagesRoot (Join-Path $TestDrive 'nuget') -Issues $script:issues) | Should -BeNullOrEmpty

            @($script:issues.Kind) | Should -Be @('PdbNotFound')
            $script:issues[0].Id | Should -Be '2 assemblies'
            $script:issues[0].Detail | Should -Match 'Devolutions\.Updater\.exe'
        }

        It 'names every assembly that has no PDB, however many' {
            $binaries = Join-Path $TestDrive 'bin'
            New-Item -ItemType Directory -Force -Path $binaries | Out-Null
            Mock Get-OwnAssemblies { 1..12 | ForEach-Object { Join-Path $TestDrive 'bin' "Own$_.dll" } }
            $script:issues = [Collections.Generic.List[object]]::new()

            $null = Get-SourceComponents -Directory $binaries -DepsComponents @() -PackagesRoot (Join-Path $TestDrive 'nuget') -Issues $script:issues

            $script:issues[0].Detail | Should -Match 'Own12\.dll'
        }
    }

    Context 'source-only packages' {
        It 'lists a package whose contentFiles are compiled in' {
            $document = Join-Path $TestDrive 'nuget' 'EmbeddedBuildTime' '1.2.0' 'contentFiles' 'cs' 'any' 'BuildTime.cs'
            $components = Invoke-Reader @($document)

            $components.Count | Should -Be 1
            $components[0].Ecosystem | Should -Be 'nuget'
            $components[0].Name | Should -Be 'EmbeddedBuildTime'
            $components[0].Version | Should -Be '1.2.0'
            $components[0].PackagePath | Should -Be 'embeddedbuildtime/1.2.0'
        }

        It 'takes the package id casing from the nuspec of the lower-case cache folder' {
            $package = Join-Path $TestDrive 'nuget' 'embeddedbuildtime' '1.1.1'
            New-Item -ItemType Directory -Force -Path $package | Out-Null
            Set-Content -LiteralPath (Join-Path $package 'embeddedbuildtime.nuspec') -Value '<?xml version="1.0"?><package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>EmbeddedBuildTime</id><version>1.1.1</version></metadata></package>'

            $components = Invoke-Reader @(Join-Path $package 'build' 'EmbeddedBuildTime.cs')

            $components[0].Name | Should -Be 'EmbeddedBuildTime'
            $components[0].PackagePath | Should -Be 'embeddedbuildtime/1.1.1'
        }

        It 'lists a package once however many of its files are compiled in' {
            $root = Join-Path $TestDrive 'nuget' 'Avalonia.Controls.MediaPlayer' '1.0.0' 'contentFiles' 'cs' 'any'
            @(Invoke-Reader @((Join-Path $root 'A.cs'), (Join-Path $root 'B.cs'))).Count | Should -Be 1
        }

        It 'lists each version of a package whose code is compiled in' {
            $components = Invoke-Reader @(
                (Join-Path $TestDrive 'nuget' 'Two.Versions' '1.0.0' 'contentFiles' 'cs' 'any' 'A.cs'),
                (Join-Path $TestDrive 'nuget' 'Two.Versions' '2.0.0' 'contentFiles' 'cs' 'any' 'A.cs'))

            @($components.Version) | Should -Be @('1.0.0', '2.0.0')
        }

        It 'skips a package the deps reader already lists' {
            Invoke-Reader @(Join-Path $TestDrive 'nuget' 'Listed.Package' '1.0.0' 'contentFiles' 'cs' 'any' 'X.cs') | Should -BeNullOrEmpty
        }

        It 'lists another version of a package the deps reader lists' {
            $components = Invoke-Reader @(Join-Path $TestDrive 'nuget' 'Listed.Package' '2.0.0' 'contentFiles' 'cs' 'any' 'X.cs')

            $components.Version | Should -Be '2.0.0'
        }

        It 'lists a package deps.json names without shipped files, which the deps reader skips' {
            $deps = [pscustomobject]@{ Libraries = @{ 'Source.Only/1.0.0' = @{ type = 'package' } }; Target = @{ 'Source.Only/1.0.0' = @{} } }

            $components = Invoke-Reader @(Join-Path $TestDrive 'nuget' 'Source.Only' '1.0.0' 'contentFiles' 'cs' 'any' 'X.cs') -DepsComponents @(Get-DotnetDepsComponents -Deps $deps)

            $components.Name | Should -Be 'Source.Only'
        }

        It 'skips excluded packages' {
            Invoke-Reader @(Join-Path $TestDrive 'nuget' 'Devolutions.Roslyn' '1.0.0' 'contentFiles' 'cs' 'any' 'X.cs') | Should -BeNullOrEmpty
        }

        It 'lists an excluded package that an exception names' {
            $components = Invoke-Reader @(Join-Path $TestDrive 'nuget' 'Devolutions.Roslyn' '1.0.0' 'contentFiles' 'cs' 'any' 'X.cs') -ExcludeExceptions @('Devolutions.Roslyn')

            $components.Name | Should -Be 'Devolutions.Roslyn'
        }

        It 'lists a package that ships its code under build/' {
            $components = Invoke-Reader @(Join-Path $TestDrive 'nuget' 'EmbeddedBuildTime' '1.1.1' 'build' 'EmbeddedBuildTime.cs')

            $components[0].Name | Should -Be 'EmbeddedBuildTime'
        }

        It 'skips analyzer code (it runs at compile time and ships nothing)' {
            Invoke-Reader @(Join-Path $TestDrive 'nuget' 'Some.Generator' '1.0.0' 'analyzers' 'dotnet' 'cs' 'Gen.cs') | Should -BeNullOrEmpty
        }
    }

    Context 'reading a PDB' {
        It 'returns the documents of a portable PDB' {
            $path = Join-Path $TestDrive 'test.pdb'
            New-TestPdb -Path $path -Documents @('C:\src\A.cs', 'C:\src\B.cs')

            @(Get-PdbDocuments -PdbPath $path) | Should -Be @('C:\src\A.cs', 'C:\src\B.cs')
        }

        It 'returns nothing for a PDB that is not portable' {
            $path = Join-Path $TestDrive 'windows.pdb'
            [IO.File]::WriteAllBytes($path, [Text.Encoding]::ASCII.GetBytes('Microsoft C/C++ MSF 7.00'))

            Get-PdbDocuments -PdbPath $path | Should -BeNullOrEmpty
        }
    }
}
