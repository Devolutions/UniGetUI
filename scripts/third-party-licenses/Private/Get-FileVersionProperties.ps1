$script:VersionStringFields = 'CompanyName', 'ProductName', 'ProductVersion', 'FileVersion', 'LegalCopyright', 'FileDescription', 'OriginalFilename'
$script:VersionNumberFields = 'FileMajorPart', 'FileMinorPart', 'FileBuildPart', 'FilePrivatePart', 'ProductMajorPart', 'ProductMinorPart', 'ProductBuildPart', 'ProductPrivatePart'

# FileVersionInfo off Windows reads only managed metadata: a native PE (DevolutionsRdp.dll) comes back empty
function Get-FileVersionProperties
{
    param([Parameter(Mandatory)] [string] $Path)

    if (-not $IsWindows)
    {
        $properties = Read-PEVersionResource -Path $Path -NativeOnly
        if ($properties) { return $properties }
    }

    return [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
}

# Same property names as FileVersionInfo, so a call site only swaps the function; $null when the file has no version
# resource, cannot be read, or is managed and -NativeOnly is set
function Read-PEVersionResource
{
    param(
        [Parameter(Mandatory)] [string] $Path,
        [switch] $NativeOnly
    )

    $data = Get-PEVersionBlock -Path $Path -NativeOnly:$NativeOnly
    if (-not $data) { return $null }

    return ConvertFrom-VersionBlock -Data $data -Path $Path
}

function ConvertFrom-VersionBlock
{
    param(
        [Parameter(Mandatory)] [byte[]] $Data,
        [string] $Path
    )

    if ($Data.Length -lt 6) { return $null }

    try
    {
        $root = Read-VersionNode -Data $Data -Offset 0
        if ($root.Key -ne 'VS_VERSION_INFO') { return $null }

        $properties = [ordered]@{ FileName = $Path }
        foreach ($field in $script:VersionStringFields) { $properties[$field] = '' }
        foreach ($field in $script:VersionNumberFields) { $properties[$field] = 0 }

        # VS_FIXEDFILEINFO: signature 0xFEEF04BD, then the file and product versions as two DWORDs each
        if ($root.ValueLength -ge 52 -and [BitConverter]::ToUInt32($Data, $root.ValueOffset) -eq 0xFEEF04BDu)
        {
            $offset = $root.ValueOffset + 8
            foreach ($pair in ('FileMajorPart', 'FileMinorPart'), ('FileBuildPart', 'FilePrivatePart'), ('ProductMajorPart', 'ProductMinorPart'), ('ProductBuildPart', 'ProductPrivatePart'))
            {
                $value = [BitConverter]::ToUInt32($Data, $offset)
                $properties[$pair[0]] = [int]($value -shr 16)
                $properties[$pair[1]] = [int]($value -band 0xFFFF)
                $offset += 4
            }
        }

        $strings = Get-VersionStrings -Data $Data -Root $root
        foreach ($field in $script:VersionStringFields) { $properties[$field] = [string]$strings[$field] }

        return [pscustomobject]$properties
    }
    catch [ArgumentException]
    {
        # A truncated or malformed block, whose lengths point past its end: nothing reliable to read
        return $null
    }
}

# The lookup of FileVersionInfo on Windows, so both OSes write the same notices: the first Translation pair (040904E4
# without one), then the US English code pages; a table counts only with a FileVersion, else the last one tried wins
function Get-VersionStrings
{
    param([byte[]] $Data, $Root)

    $sections = @(Get-VersionChildren -Data $Data -Node $Root)
    $tables = @(foreach ($stringFileInfo in @($sections | Where-Object Key -eq 'StringFileInfo')) { Get-VersionChildren -Data $Data -Node $stringFileInfo })
    $translation = foreach ($varFileInfo in @($sections | Where-Object Key -eq 'VarFileInfo'))
    {
        foreach ($var in @(Get-VersionChildren -Data $Data -Node $varFileInfo | Where-Object { $_.Key -eq 'Translation' -and $_.ValueLength -ge 4 }))
        {
            '{0:x4}{1:x4}' -f [BitConverter]::ToUInt16($Data, $var.ValueOffset), [BitConverter]::ToUInt16($Data, $var.ValueOffset + 2)
        }
    }

    $first = if ($translation) { @($translation)[0] } else { '040904e4' }
    $strings = @{}
    foreach ($key in @($first) + @(@('040904b0', '040904e4', '04090000') | Where-Object { $_ -ne $first }))
    {
        $strings = @{}
        $table = $tables | Where-Object Key -eq $key | Select-Object -First 1
        foreach ($entry in @(if ($table) { Get-VersionChildren -Data $Data -Node $table }))
        {
            if ($entry.ValueOffset -lt $entry.End)
            {
                # wValueLength counts characters in most files and bytes in some: read up to the NUL inside the node instead
                $text = [Text.Encoding]::Unicode.GetString($Data, $entry.ValueOffset, $entry.End - $entry.ValueOffset)
                $strings[$entry.Key] = ($text -split "`0", 2)[0]
            }
        }

        if ($strings['FileVersion']) { break }
    }

    return $strings
}

function Get-VersionChildren
{
    param([byte[]] $Data, $Node)

    $offset = $Node.ChildOffset
    while ($offset + 6 -le $Node.End)
    {
        $child = Read-VersionNode -Data $Data -Offset $offset
        if ($child.Length -eq 0) { break }

        $child
        $offset = ($child.End + 3) -band -bnot 3
    }
}

# A VS_VERSIONINFO node: wLength, wValueLength, wType, a NUL-terminated UTF-16 key, then value and children on 4-byte boundaries
function Read-VersionNode
{
    param([byte[]] $Data, [int] $Offset)

    $length = [BitConverter]::ToUInt16($Data, $Offset)
    $valueLength = [BitConverter]::ToUInt16($Data, $Offset + 2)
    $isText = [BitConverter]::ToUInt16($Data, $Offset + 4) -eq 1
    $end = [Math]::Min($Offset + $length, $Data.Length)

    $keyEnd = $Offset + 6
    while ($keyEnd + 1 -lt $end -and ($Data[$keyEnd] -ne 0 -or $Data[$keyEnd + 1] -ne 0)) { $keyEnd += 2 }

    $valueOffset = ($keyEnd + 2 + 3) -band -bnot 3
    $valueBytes = if ($isText) { $valueLength * 2 } else { $valueLength }

    [pscustomobject]@{
        Key         = [Text.Encoding]::Unicode.GetString($Data, $Offset + 6, $keyEnd - $Offset - 6)
        Length      = $length
        ValueOffset = $valueOffset
        ValueLength = [int]$valueBytes
        ChildOffset = ($valueOffset + $valueBytes + 3) -band -bnot 3
        End         = $end
    }
}

# The RT_VERSION resource bytes, through the three levels of the resource directory: type 16, first name, first language
function Get-PEVersionBlock
{
    param(
        [string] $Path,
        [switch] $NativeOnly
    )

    $stream = $null
    $peReader = $null
    try
    {
        $stream = [IO.File]::OpenRead($Path)
        $peReader = [Reflection.PortableExecutable.PEReader]::new($stream)

        # Managed assemblies keep their metadata version: it is what the SDK writes into deps.json on the same OS
        if ($NativeOnly -and $peReader.HasMetadata) { return $null }

        $directory = $peReader.PEHeaders.PEHeader.ResourceTableDirectory
        if ($directory.RelativeVirtualAddress -eq 0 -or $directory.Size -eq 0) { return $null }

        $section = $peReader.GetSectionData($directory.RelativeVirtualAddress)
        $resources = $section.GetReader().ReadBytes($section.Length)
        $entry = 0
        foreach ($id in 16, $null, $null)
        {
            $entry = Find-ResourceEntry -Resources $resources -DirectoryOffset $entry -Id $id
            if ($null -eq $entry) { return $null }
        }

        # IMAGE_RESOURCE_DATA_ENTRY: the data RVA, then its size
        $dataRva = [BitConverter]::ToInt32($resources, $entry)
        $size = [BitConverter]::ToInt32($resources, $entry + 4)
        $block = $peReader.GetSectionData($dataRva)
        if ($block.Length -lt $size) { return $null }

        return $block.GetReader(0, $size).ReadBytes($size)
    }
    catch [BadImageFormatException], [InvalidOperationException], [ArgumentException]
    {
        # Not a PE file (ELF, Mach-O), or a truncated or malformed one: no version resource to read
        return $null
    }
    catch [IO.IOException], [UnauthorizedAccessException]
    {
        # Unreadable here: FileVersionInfo gets the file next, as on Windows
        return $null
    }
    finally
    {
        if ($peReader) { $peReader.Dispose() }
        if ($stream) { $stream.Dispose() }
    }
}

function Find-ResourceEntry
{
    param([byte[]] $Resources, [int] $DirectoryOffset, $Id)

    # IMAGE_RESOURCE_DIRECTORY: a 16-byte header, then named entries and ID entries of 8 bytes each
    $count = [BitConverter]::ToUInt16($Resources, $DirectoryOffset + 12) + [BitConverter]::ToUInt16($Resources, $DirectoryOffset + 14)
    for ($index = 0; $index -lt $count; $index++)
    {
        $entryOffset = $DirectoryOffset + 16 + 8 * $index
        if ($null -eq $Id -or [BitConverter]::ToUInt32($Resources, $entryOffset) -eq $Id)
        {
            # The high bit flags a subdirectory; both kinds of offset are relative to the start of the resource section
            return [int]([BitConverter]::ToUInt32($Resources, $entryOffset + 4) -band 0x7FFFFFFFu)
        }
    }

    return $null
}
