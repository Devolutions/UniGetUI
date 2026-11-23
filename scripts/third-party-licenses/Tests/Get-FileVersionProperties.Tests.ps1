Describe 'PE version resource reader' {
    BeforeAll {
        $privateDirectory = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath 'Private'
        . (Join-Path $privateDirectory 'Get-FileVersionProperties.ps1')
        . (Join-Path $privateDirectory 'Get-DotnetDepsComponents.ps1')

        $script:fields = 'CompanyName', 'ProductName', 'ProductVersion', 'FileVersion', 'LegalCopyright', 'FileMajorPart', 'FileMinorPart', 'FileBuildPart', 'FilePrivatePart'

        # A VS_VERSIONINFO node: wLength, wValueLength (characters for text), wType, the key, then value and children,
        # each on a 4-byte boundary
        function New-VersionNode
        {
            param([string] $Key, [byte[]] $Value = [byte[]]::new(0), [switch] $Text, [object[]] $Children = @())

            $body = [IO.BinaryWriter]::new([IO.MemoryStream]::new())
            $body.Write([Text.Encoding]::Unicode.GetBytes("$Key`0"))
            while (($body.BaseStream.Length + 6) % 4) { $body.Write([byte]0) }
            $body.Write($Value)
            foreach ($child in $Children)
            {
                while (($body.BaseStream.Length + 6) % 4) { $body.Write([byte]0) }
                $body.Write([byte[]]$child)
            }
            $bytes = $body.BaseStream.ToArray()

            $node = [IO.BinaryWriter]::new([IO.MemoryStream]::new())
            $node.Write([uint16]($bytes.Length + 6))
            $node.Write([uint16]$(if ($Text) { $Value.Length / 2 } else { $Value.Length }))
            $node.Write([uint16]$(if ($Text) { 1 } else { 0 }))
            $node.Write($bytes)
            return , $node.BaseStream.ToArray()
        }

        # The smallest native PE32 the Windows loader and PEReader both accept: headers and one .rsrc section holding an
        # RT_VERSION resource (type 16, name 1, language 0x409); no CLI header, so it has no metadata
        function New-NativePE
        {
            param([string] $Path, [Collections.IDictionary] $Strings, [version] $FileVersion, [version] $ProductVersion)

            $fixed = [IO.BinaryWriter]::new([IO.MemoryStream]::new())
            foreach ($dword in @(0xFEEF04BDu, 0x00010000u,
                    (($FileVersion.Major -shl 16) -bor $FileVersion.Minor), (($FileVersion.Build -shl 16) -bor $FileVersion.Revision),
                    (($ProductVersion.Major -shl 16) -bor $ProductVersion.Minor), (($ProductVersion.Build -shl 16) -bor $ProductVersion.Revision),
                    0x3F, 0, 0x4, 0x2, 0, 0, 0))
            {
                $fixed.Write([uint32]$dword)
            }

            $entries = @(foreach ($entry in $Strings.GetEnumerator()) { , (New-VersionNode -Key $entry.Key -Value ([Text.Encoding]::Unicode.GetBytes("$($entry.Value)`0")) -Text) })
            $table = New-VersionNode -Key '040904b0' -Text -Children $entries
            $stringFileInfo = New-VersionNode -Key 'StringFileInfo' -Text -Children @(, $table)
            $translation = New-VersionNode -Key 'Translation' -Value ([byte[]](0x09, 0x04, 0xB0, 0x04))
            $varFileInfo = New-VersionNode -Key 'VarFileInfo' -Text -Children @(, $translation)
            $block = New-VersionNode -Key 'VS_VERSION_INFO' -Value $fixed.BaseStream.ToArray() -Children @($stringFileInfo, $varFileInfo)

            # Three resource directories of one ID entry each (the high bit flags a subdirectory), the data entry, the block
            $sectionRva = 0x1000
            $resources = [IO.BinaryWriter]::new([IO.MemoryStream]::new())
            foreach ($level in @(@(16, 0x80000018u), @(1, 0x80000030u), @(0x409, 0x48)))
            {
                $resources.Write([byte[]]::new(14))
                $resources.Write([uint16]1)
                $resources.Write([uint32]$level[0])
                $resources.Write([uint32]$level[1])
            }
            $resources.Write([uint32]($sectionRva + 0x58))
            $resources.Write([uint32]$block.Length)
            $resources.Write([byte[]]::new(8))
            $resources.Write($block)
            $section = $resources.BaseStream.ToArray()
            $rawSize = [int]([Math]::Ceiling($section.Length / 0x200) * 0x200)

            $pe = [IO.BinaryWriter]::new([IO.File]::Create($Path))
            try
            {
                $pe.Write([byte[]](0x4D, 0x5A)); $pe.Write([byte[]]::new(58)); $pe.Write([uint32]0x40)
                $pe.Write([byte[]](0x50, 0x45, 0, 0))
                # COFF header: i386, one section, a 224-byte optional header, an executable 32-bit DLL
                $pe.Write([uint16]0x14C); $pe.Write([uint16]1); $pe.Write([byte[]]::new(12)); $pe.Write([uint16]0xE0); $pe.Write([uint16]0x2102)
                # PE32 optional header: no code, no entry point, then image base and alignments
                $pe.Write([uint16]0x10B); $pe.Write([byte[]]::new(26))
                $pe.Write([uint32]0x10000000); $pe.Write([uint32]0x1000); $pe.Write([uint32]0x200)
                $pe.Write([uint16]6); $pe.Write([uint16]0); $pe.Write([uint16]0); $pe.Write([uint16]0); $pe.Write([uint16]6); $pe.Write([uint16]0)
                $pe.Write([uint32]0); $pe.Write([uint32]($sectionRva + 0x1000)); $pe.Write([uint32]0x200); $pe.Write([uint32]0)
                $pe.Write([uint16]2); $pe.Write([uint16]0x540)
                $pe.Write([uint32]0x100000); $pe.Write([uint32]0x1000); $pe.Write([uint32]0x100000); $pe.Write([uint32]0x1000)
                $pe.Write([uint32]0); $pe.Write([uint32]16)
                # 16 data directories: only the resource table (the third) is set
                $pe.Write([byte[]]::new(16)); $pe.Write([uint32]$sectionRva); $pe.Write([uint32]$section.Length); $pe.Write([byte[]]::new(13 * 8))
                $pe.Write([Text.Encoding]::ASCII.GetBytes(".rsrc`0`0`0"))
                $pe.Write([uint32]$section.Length); $pe.Write([uint32]$sectionRva); $pe.Write([uint32]$rawSize); $pe.Write([uint32]0x200)
                $pe.Write([byte[]]::new(12)); $pe.Write([uint32]0x40000040u)
                $pe.Write([byte[]]::new(0x200 - $pe.BaseStream.Position))
                $pe.Write($section)
                $pe.Write([byte[]]::new($rawSize - $section.Length))
            }
            finally
            {
                $pe.Dispose()
            }
        }
    }

    # Only Windows reads the version resource of a managed assembly; elsewhere FileVersionInfo reads its metadata, which can differ
    It 'reads what FileVersionInfo reads from <Name>' -Skip:(-not $IsWindows) -TestCases @(
        @{ Name = 'System.Management.Automation.dll' }
        @{ Name = 'Microsoft.PowerShell.ConsoleHost.dll' }
    ) {
        $path = Join-Path $PSHOME $Name
        $expected = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)

        $actual = Read-PEVersionResource -Path $path

        $actual | Should -Not -BeNullOrEmpty
        foreach ($field in $script:fields) { [string]$actual.$field | Should -Be ([string]$expected.$field) -Because $field }
    }

    It 'reads what FileVersionInfo reads from a native PE' -Skip:(-not $IsWindows) {
        $path = Join-Path $PSHOME 'pwsh.exe'
        $expected = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)

        $actual = Read-PEVersionResource -Path $path

        $actual.CompanyName | Should -Not -BeNullOrEmpty
        foreach ($field in $script:fields) { [string]$actual.$field | Should -Be ([string]$expected.$field) -Because $field }
    }

    It 'returns nothing for <Case>' -TestCases @(
        @{ Case = 'an ELF file'; Bytes = [byte[]](0x7F, 0x45, 0x4C, 0x46, 2, 1, 1, 0) + [byte[]]::new(56) }
        @{ Case = 'a Mach-O file'; Bytes = [byte[]](0xCF, 0xFA, 0xED, 0xFE) + [byte[]]::new(28) }
        @{ Case = 'a truncated PE'; Bytes = [byte[]](0x4D, 0x5A) + [byte[]]::new(30) }
        @{ Case = 'a text file'; Bytes = [Text.Encoding]::UTF8.GetBytes('not a binary') }
        @{ Case = 'an empty file'; Bytes = [byte[]]::new(0) }
    ) {
        $path = Join-Path $TestDrive 'sample.bin'
        [IO.File]::WriteAllBytes($path, $Bytes)

        Read-PEVersionResource -Path $path | Should -BeNullOrEmpty
    }

    # A Linux-built deps.json records the metadata version of a managed assembly: reading its resource there raised false ShippedVersionMismatch
    It 'keeps FileVersionInfo for a managed assembly on every OS' {
        $path = Join-Path $PSHOME 'System.Management.Automation.dll'

        Read-PEVersionResource -Path $path | Should -Not -BeNullOrEmpty
        Read-PEVersionResource -Path $path -NativeOnly | Should -BeNullOrEmpty
        (Get-FileVersionProperties -Path $path) | Should -BeOfType [Diagnostics.FileVersionInfo]
    }

    # The case the reader exists for: off Windows, FileVersionInfo returns these fields empty. On Windows the same
    # assertions check the fixture through FileVersionInfo
    It 'reads the file properties of a native PE on every OS' {
        $path = Join-Path $TestDrive 'Native.dll'
        $strings = [ordered]@{ CompanyName = 'Example Corp'; ProductName = 'Example'; ProductVersion = '5.6.7'; FileVersion = '1.2.3.4'; LegalCopyright = 'Copyright Example' }
        New-NativePE -Path $path -Strings $strings -FileVersion '1.2.3.4' -ProductVersion '5.6.7.8'

        $properties = Get-FileVersionProperties -Path $path

        foreach ($field in $strings.Keys) { $properties.$field | Should -Be $strings[$field] -Because $field }
        @($properties.FileMajorPart, $properties.FileMinorPart, $properties.FileBuildPart, $properties.FilePrivatePart) | Should -Be @(1, 2, 3, 4)
        @($properties.ProductMajorPart, $properties.ProductMinorPart, $properties.ProductBuildPart, $properties.ProductPrivatePart) | Should -Be @(5, 6, 7, 8)
        (Read-PEVersionResource -Path $path -NativeOnly).CompanyName | Should -Be 'Example Corp'
    }

    It 'reads a native PE with -NativeOnly' -Skip:(-not $IsWindows) {
        (Read-PEVersionResource -Path (Join-Path $PSHOME 'pwsh.exe') -NativeOnly).CompanyName | Should -Not -BeNullOrEmpty
    }

    # FileVersionInfo returns empty fields for such a block: the reader must not stop the tool instead
    It 'never throws on a truncated version block' {
        $block = Get-PEVersionBlock -Path (Join-Path $PSHOME 'System.Management.Automation.dll')
        $block | Should -Not -BeNullOrEmpty

        for ($length = 6; $length -lt $block.Length; $length += 5)
        {
            { ConvertFrom-VersionBlock -Data $block[0..($length - 1)] } | Should -Not -Throw -Because "truncated to $length bytes"
        }

        # Cut inside VS_FIXEDFILEINFO, whose length the header still claims
        ConvertFrom-VersionBlock -Data $block[0..59] | Should -BeNullOrEmpty
    }

    It 'returns nothing for a path it cannot open' {
        { Read-PEVersionResource -Path $TestDrive } | Should -Not -Throw
        Read-PEVersionResource -Path $TestDrive | Should -BeNullOrEmpty
    }

    It 'returns nothing for a PE without a version resource and falls back to FileVersionInfo' {
        $path = Join-Path $TestDrive 'NoVersion.dll'
        Add-Type -OutputAssembly $path -TypeDefinition 'public class NoVersionResource {}'

        Read-PEVersionResource -Path $path | Should -BeNullOrEmpty
        (Get-FileVersionProperties -Path $path) | Should -BeOfType [Diagnostics.FileVersionInfo]
    }

    It 'skips an unlisted binary whose file properties name Devolutions' {
        $directory = Join-Path $TestDrive 'unlisted'
        New-Item -Path (Join-Path $directory 'win-x64') -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $directory 'win-x64' 'DevolutionsRdp.dll') -Value 'native'
        Set-Content -Path (Join-Path $directory 'win-x64' 'ThirdParty.dll') -Value 'native'
        $deps = [pscustomobject]@{ Path = (Join-Path $directory 'App.deps.json'); Target = @{} }
        Mock Get-FileVersionProperties { [pscustomobject]@{ CompanyName = 'Devolutions Inc.'; ProductName = ''; ProductVersion = '1.0.0.0'; FileVersion = '1.0.0.0'; LegalCopyright = '' } } -ParameterFilter { $Path -like '*DevolutionsRdp.dll' }
        Mock Get-FileVersionProperties { [pscustomobject]@{ CompanyName = 'Example'; ProductName = ''; ProductVersion = '2.0.0'; FileVersion = '2.0.0'; LegalCopyright = 'Copyright Example' } } -ParameterFilter { $Path -like '*ThirdParty.dll' }

        $binaries = @(Get-UnlistedBinaries -Deps $deps -Directory $directory)

        $binaries.Name | Should -Be @('ThirdParty.dll')
        $binaries[0].Version | Should -Be '2.0.0'
        $binaries[0].Copyright | Should -Be 'Copyright Example'
    }
}
