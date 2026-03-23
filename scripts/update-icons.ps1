#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Exports or validates the icon database maintained in WebBasedData.

.DESCRIPTION
    Reads the checked-in screenshot_database.xlsx workbook directly through ZIP/XML APIs,
    regenerates screenshot-database-v2.json, and can validate icon URLs from the JSON output.

.EXAMPLE
    ./scripts/update-icons.ps1

    Regenerates WebBasedData/screenshot-database-v2.json and WebBasedData/new_urls.txt.

.EXAMPLE
    ./scripts/update-icons.ps1 -Validate -MaxPackages 25

    Validates the first 25 icon URLs from screenshot-database-v2.json without mutating invalid_urls.txt.

.EXAMPLE
    ./scripts/update-icons.ps1 -Validate -AppendInvalidUrls

    Validates icon URLs and appends any 404 URLs to WebBasedData/invalid_urls.txt.
#>

[CmdletBinding(DefaultParameterSetName = 'Export')]
param(
    [Parameter(ParameterSetName = 'Export')]
    [switch] $Export,

    [Parameter(ParameterSetName = 'Validate')]
    [switch] $Validate,

    [string] $WorkbookPath = 'WebBasedData/screenshot_database.xlsx',
    [string] $JsonPath = 'WebBasedData/screenshot-database-v2.json',
    [string] $InvalidUrlsPath = 'WebBasedData/invalid_urls.txt',
    [string] $NewUrlsPath = 'WebBasedData/new_urls.txt',
    [int] $MaxScreenshotsPerPackage = 23,
    [switch] $AppendInvalidUrls,
    [int] $MaxPackages,
    [int] $MaxRetries = 2,
    [int] $RetryDelayMilliseconds = 200,
    [int] $RequestTimeoutSeconds = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -TypeDefinition @"
using System.Collections.Generic;

namespace UniGetUI.IconTools
{
    public sealed class IconDatabaseDocument
    {
        public IconDatabaseDocument()
        {
            package_count = new PackageCount();
            icons_and_screenshots = new Dictionary<string, PackageEntry>(System.StringComparer.Ordinal);
        }

        public PackageCount package_count { get; set; }

        public Dictionary<string, PackageEntry> icons_and_screenshots { get; set; }
    }

    public sealed class PackageCount
    {
        public int total { get; set; }

        public int done { get; set; }

        public int packages_with_icon { get; set; }

        public int packages_with_screenshot { get; set; }

        public int total_screenshots { get; set; }
    }

    public sealed class PackageEntry
    {
        public PackageEntry()
        {
            icon = string.Empty;
            images = new List<string>();
        }

        public string icon { get; set; }

        public List<string> images { get; set; }
    }
}
"@

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$OpenXmlRelationshipNamespace = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
$OpenXmlPackageRelationshipNamespace = 'http://schemas.openxmlformats.org/package/2006/relationships'

if (-not $PSBoundParameters.ContainsKey('Export') -and -not $PSBoundParameters.ContainsKey('Validate')) {
    $Export = $true
}

function Resolve-RepoPath {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Get-UrlRegex {
    return [regex] 'https?://[^\s",]+'
}

function Get-UrlsFromText {
    param(
        [string] $Text
    )

    if ([string]::IsNullOrEmpty($Text)) {
        return @()
    }

    $urlRegex = Get-UrlRegex
    return @($urlRegex.Matches($Text) | ForEach-Object { $_.Value })
}

function ConvertFrom-JsonEscapedString {
    param(
        [Parameter(Mandatory)]
        [string] $Value
    )

    $jsonString = '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"'
    return [System.Text.Json.JsonSerializer]::Deserialize[string]($jsonString)
}

function ConvertTo-AsciiEscapedJson {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $Value
    )

    $builder = [System.Text.StringBuilder]::new($Value.Length)
    foreach ($character in $Value.ToCharArray()) {
        if ([int] $character -le 127) {
            [void] $builder.Append($character)
        }
        else {
            [void] $builder.AppendFormat('\u{0:x4}', [int] $character)
        }
    }

    return $builder.ToString()
}

function Get-UrlsFromIconDatabaseFile {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }

    $urls = New-Object System.Collections.Generic.List[string]
    $jsonDocument = [System.Text.Json.JsonDocument]::Parse([System.IO.File]::ReadAllText($Path))
    try {
        $iconsAndScreenshots = $jsonDocument.RootElement.GetProperty('icons_and_screenshots')

        foreach ($package in $iconsAndScreenshots.EnumerateObject()) {
            $iconUrl = $package.Value.GetProperty('icon').GetString()
            if (-not [string]::IsNullOrWhiteSpace($iconUrl)) {
                $urls.Add($iconUrl)
            }

            foreach ($image in $package.Value.GetProperty('images').EnumerateArray()) {
                $imageUrl = $image.GetString()
                if (-not [string]::IsNullOrWhiteSpace($imageUrl)) {
                    $urls.Add($imageUrl)
                }
            }
        }
    }
    finally {
        $jsonDocument.Dispose()
    }

    return (, $urls.ToArray())
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $Content
    )

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Read-ZipEntryText {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive] $Archive,

        [Parameter(Mandatory)]
        [string] $EntryPath
    )

    $entry = $Archive.GetEntry($EntryPath)
    if ($null -eq $entry) {
        throw "Zip entry not found: $EntryPath"
    }

    $reader = [System.IO.StreamReader]::new($entry.Open())
    try {
        return $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}

function New-XmlNamespaceManager {
    param(
        [Parameter(Mandatory)]
        [xml] $Document
    )

    $manager = [System.Xml.XmlNamespaceManager]::new($Document.NameTable)
    $manager.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
    $manager.AddNamespace('r', $OpenXmlRelationshipNamespace)
    $manager.AddNamespace('pr', $OpenXmlPackageRelationshipNamespace)
    return (, $manager)
}

function Resolve-OpenXmlPath {
    param(
        [Parameter(Mandatory)]
        [string] $BasePath,

        [Parameter(Mandatory)]
        [string] $TargetPath
    )

    $baseUri = [System.Uri]::new("https://openxml.local/$BasePath")
    $resolvedUri = [System.Uri]::new($baseUri, $TargetPath)
    return $resolvedUri.AbsolutePath.TrimStart('/')
}

function Convert-SharedStringNodeToText {
    param(
        [Parameter(Mandatory)]
        [System.Xml.XmlNode] $Node,

        [Parameter(Mandatory)]
        [System.Xml.XmlNamespaceManager] $NamespaceManager
    )

    $textNodes = $Node.SelectNodes('.//x:t', $NamespaceManager)
    if ($null -eq $textNodes -or $textNodes.Count -eq 0) {
        return ''
    }

    $builder = [System.Text.StringBuilder]::new()
    foreach ($textNode in $textNodes) {
        [void] $builder.Append($textNode.InnerText)
    }

    return $builder.ToString()
}

function Get-SharedStrings {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive] $Archive
    )

    $entry = $Archive.GetEntry('xl/sharedStrings.xml')
    if ($null -eq $entry) {
        return @()
    }

    [xml] $document = Read-ZipEntryText -Archive $Archive -EntryPath 'xl/sharedStrings.xml'
    $namespaceManager = New-XmlNamespaceManager -Document $document
    $items = $document.SelectNodes('/x:sst/x:si', $namespaceManager)
    $sharedStrings = [System.Collections.Generic.List[string]]::new()

    foreach ($item in $items) {
        $sharedStrings.Add((Convert-SharedStringNodeToText -Node $item -NamespaceManager $namespaceManager))
    }

    return (, $sharedStrings.ToArray())
}

function Get-FirstWorksheetPath {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive] $Archive
    )

    [xml] $workbookDocument = Read-ZipEntryText -Archive $Archive -EntryPath 'xl/workbook.xml'
    $workbookNamespaceManager = New-XmlNamespaceManager -Document $workbookDocument
    $firstSheet = $workbookDocument.SelectSingleNode('/x:workbook/x:sheets/x:sheet[1]', $workbookNamespaceManager)
    if ($null -eq $firstSheet) {
        throw 'Workbook does not contain any worksheets.'
    }

    $relationshipId = $firstSheet.GetAttribute('id', $OpenXmlRelationshipNamespace)
    if ([string]::IsNullOrWhiteSpace($relationshipId)) {
        throw 'First worksheet relationship id is missing from workbook.xml.'
    }

    [xml] $relationshipDocument = Read-ZipEntryText -Archive $Archive -EntryPath 'xl/_rels/workbook.xml.rels'
    $relationshipNamespaceManager = New-XmlNamespaceManager -Document $relationshipDocument
    $sheetRelationship = $relationshipDocument.SelectSingleNode(
        "/pr:Relationships/pr:Relationship[@Id='$relationshipId']",
        $relationshipNamespaceManager
    )

    if ($null -eq $sheetRelationship) {
        throw "Workbook relationship not found for worksheet id '$relationshipId'."
    }

    return Resolve-OpenXmlPath -BasePath 'xl/workbook.xml' -TargetPath $sheetRelationship.Attributes['Target'].Value
}

function Convert-ExcelColumnNameToIndex {
    param(
        [Parameter(Mandatory)]
        [string] $ColumnName
    )

    $index = 0
    foreach ($character in $ColumnName.ToCharArray()) {
        if ($character -lt 'A' -or $character -gt 'Z') {
            throw "Invalid Excel column name: $ColumnName"
        }

        $index = ($index * 26) + ([int] $character - [int] [char] 'A' + 1)
    }

    return $index - 1
}

function Get-SpreadsheetCellColumnIndex {
    param(
        [Parameter(Mandatory)]
        [System.Xml.XmlNode] $CellNode
    )

    $reference = $CellNode.Attributes['r'].Value
    $columnName = [regex]::Match($reference, '^[A-Z]+').Value
    if ([string]::IsNullOrWhiteSpace($columnName)) {
        throw "Could not resolve column name from cell reference '$reference'."
    }

    return Convert-ExcelColumnNameToIndex -ColumnName $columnName
}

function Get-SpreadsheetCellValue {
    param(
        [Parameter(Mandatory)]
        [System.Xml.XmlNode] $CellNode,

        [Parameter(Mandatory)]
        [string[]] $SharedStrings,

        [Parameter(Mandatory)]
        [System.Xml.XmlNamespaceManager] $NamespaceManager
    )

    $type = if ($CellNode.Attributes['t']) { $CellNode.Attributes['t'].Value } else { '' }
    $valueNode = $CellNode.SelectSingleNode('x:v', $NamespaceManager)

    switch ($type) {
        's' {
            if ($null -eq $valueNode -or [string]::IsNullOrWhiteSpace($valueNode.InnerText)) {
                return ''
            }

            $index = [int] $valueNode.InnerText
            if ($index -lt 0 -or $index -ge $SharedStrings.Length) {
                throw "Shared string index '$index' is out of range."
            }

            return $SharedStrings[$index]
        }
        'inlineStr' {
            return Convert-SharedStringNodeToText -Node $CellNode -NamespaceManager $NamespaceManager
        }
        default {
            if ($null -eq $valueNode) {
                return ''
            }

            return $valueNode.InnerText
        }
    }
}

function Read-WorksheetRows {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive] $Archive,

        [Parameter(Mandatory)]
        [string[]] $SharedStrings
    )

    $worksheetPath = Get-FirstWorksheetPath -Archive $Archive
    [xml] $worksheetDocument = Read-ZipEntryText -Archive $Archive -EntryPath $worksheetPath
    $namespaceManager = New-XmlNamespaceManager -Document $worksheetDocument
    $rowNodes = $worksheetDocument.SelectNodes('/x:worksheet/x:sheetData/x:row', $namespaceManager)

    foreach ($rowNode in $rowNodes) {
        $cellMap = [System.Collections.Generic.Dictionary[int, string]]::new()

        foreach ($cellNode in $rowNode.SelectNodes('x:c', $namespaceManager)) {
            $columnIndex = Get-SpreadsheetCellColumnIndex -CellNode $cellNode
            $cellMap[$columnIndex] = [string] (Get-SpreadsheetCellValue -CellNode $cellNode -SharedStrings $SharedStrings -NamespaceManager $namespaceManager)
        }

        [pscustomobject] @{
            RowNumber = [int] $rowNode.Attributes['r'].Value
            Cells     = $cellMap
        }
    }
}

function ConvertTo-PackageId {
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string] $Value
    )

    if ($null -eq $Value) {
        return ''
    }

    $trimmed = $Value.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        return ''
    }

    $numericValue = 0.0
    if ([double]::TryParse($trimmed, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref] $numericValue)) {
        if ($numericValue % 1 -eq 0) {
            return [string] ([int64] $numericValue)
        }
    }

    return $trimmed
}

function ConvertTo-TrimmedUrl {
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string] $Value
    )

    if ($null -eq $Value) {
        return ''
    }

    return $Value.Trim()
}

function Get-ForbiddenUrlSet {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $forbiddenUrls = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    if (-not (Test-Path -LiteralPath $Path)) {
        return (, $forbiddenUrls)
    }

    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if ($null -eq $line) {
            continue
        }

        $trimmed = $line.Trim()
        if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
            [void] $forbiddenUrls.Add($trimmed)
        }
    }

    return (, $forbiddenUrls)
}

function Export-IconDatabase {
    param(
        [Parameter(Mandatory)]
        [string] $WorkbookPath,

        [Parameter(Mandatory)]
        [string] $JsonPath,

        [Parameter(Mandatory)]
        [string] $InvalidUrlsPath,

        [Parameter(Mandatory)]
        [string] $NewUrlsPath,

        [Parameter(Mandatory)]
        [int] $MaxScreenshotsPerPackage
    )

    if (-not (Test-Path -LiteralPath $WorkbookPath)) {
        throw "Workbook not found: $WorkbookPath"
    }

    $forbiddenUrls = Get-ForbiddenUrlSet -Path $InvalidUrlsPath
    $oldContent = if (Test-Path -LiteralPath $JsonPath) { [System.IO.File]::ReadAllText($JsonPath) } else { '' }
    $oldUrlLookup = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    foreach ($url in Get-UrlsFromText -Text $oldContent) {
        [void] $oldUrlLookup.Add((ConvertFrom-JsonEscapedString -Value $url))
    }

    $document = [UniGetUI.IconTools.IconDatabaseDocument]::new()
    $totalCount = 0
    $doneCount = 0
    $packagesWithIcon = 0
    $packagesWithScreenshot = 0
    $totalScreenshots = 0

    $archive = [System.IO.Compression.ZipFile]::OpenRead($WorkbookPath)
    try {
        $sharedStrings = Get-SharedStrings -Archive $archive
        foreach ($row in Read-WorksheetRows -Archive $archive -SharedStrings $sharedStrings) {
            if ($row.RowNumber -le 1) {
                continue
            }

            $packageId = if ($row.Cells.ContainsKey(0)) { ConvertTo-PackageId -Value $row.Cells[0] } else { '' }
            if ([string]::IsNullOrWhiteSpace($packageId)) {
                continue
            }

            $iconUrl = if ($row.Cells.ContainsKey(1)) { ConvertTo-TrimmedUrl -Value $row.Cells[1] } else { '' }
            if (-not [string]::IsNullOrWhiteSpace($iconUrl)) {
                if ($forbiddenUrls.Contains($iconUrl)) {
                    $iconUrl = ''
                }
                else {
                    $doneCount++
                    $packagesWithIcon++
                }
            }

            $images = [System.Collections.Generic.List[string]]::new()
            for ($columnIndex = 2; $columnIndex -lt (2 + $MaxScreenshotsPerPackage); $columnIndex++) {
                if (-not $row.Cells.ContainsKey($columnIndex)) {
                    break
                }

                $imageUrl = ConvertTo-TrimmedUrl -Value $row.Cells[$columnIndex]
                if ([string]::IsNullOrWhiteSpace($imageUrl)) {
                    break
                }

                $images.Add($imageUrl)
                $totalScreenshots++
            }

            if ($images.Count -gt 0) {
                $packagesWithScreenshot++
            }

            if (-not $document.icons_and_screenshots.ContainsKey($packageId)) {
                $packageEntry = [UniGetUI.IconTools.PackageEntry]::new()
                $packageEntry.icon = $iconUrl
                $packageEntry.images.AddRange($images)
                $document.icons_and_screenshots[$packageId] = $packageEntry
            }
            else {
                $packageEntry = $document.icons_and_screenshots[$packageId]
                if ([string]::IsNullOrWhiteSpace($packageEntry.icon) -and -not [string]::IsNullOrWhiteSpace($iconUrl)) {
                    $packageEntry.icon = $iconUrl
                }

                if ($packageEntry.images.Count -eq 0 -and $images.Count -gt 0) {
                    $packageEntry.images.AddRange($images)
                }
            }

            $totalCount++
        }
    }
    finally {
        $archive.Dispose()
    }

    $document.package_count.total = $totalCount
    $document.package_count.done = $doneCount
    $document.package_count.packages_with_icon = $packagesWithIcon
    $document.package_count.packages_with_screenshot = $packagesWithScreenshot
    $document.package_count.total_screenshots = $totalScreenshots

    $serializerOptions = [System.Text.Json.JsonSerializerOptions]::new()
    $serializerOptions.WriteIndented = $true
    $serializerOptions.IndentCharacter = ' '
    $serializerOptions.IndentSize = 4
    $serializerOptions.NewLine = [Environment]::NewLine
    $serializerOptions.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
    $newContent = [System.Text.Json.JsonSerializer]::Serialize($document, $serializerOptions)
    $newContent = ConvertTo-AsciiEscapedJson -Value $newContent
    Write-Utf8File -Path $JsonPath -Content ($newContent + [Environment]::NewLine)

    $newUrls = New-Object System.Collections.Generic.List[string]
    $seenNewUrls = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    foreach ($packageEntry in $document.icons_and_screenshots.GetEnumerator()) {
        if (
            -not [string]::IsNullOrWhiteSpace($packageEntry.Value.icon) -and
            -not $oldUrlLookup.Contains($packageEntry.Value.icon) -and
            $seenNewUrls.Add($packageEntry.Value.icon)
        ) {
            $newUrls.Add($packageEntry.Value.icon)
        }

        foreach ($imageUrl in $packageEntry.Value.images) {
            if (
                -not [string]::IsNullOrWhiteSpace($imageUrl) -and
                -not $oldUrlLookup.Contains($imageUrl) -and
                $seenNewUrls.Add($imageUrl)
            ) {
                $newUrls.Add($imageUrl)
            }
        }
    }

    Write-Utf8File -Path $NewUrlsPath -Content (($newUrls -join [Environment]::NewLine) + $(if ($newUrls.Count -gt 0) { [Environment]::NewLine } else { '' }))

    Write-Host "Exported icon database from $WorkbookPath" -ForegroundColor Green
    Write-Host "Wrote JSON to $JsonPath" -ForegroundColor Green
    Write-Host "Wrote newly added URLs to $NewUrlsPath" -ForegroundColor Green
    Write-Host "Rows processed: $totalCount" -ForegroundColor Cyan
    Write-Host "Rows with icons: $packagesWithIcon" -ForegroundColor Cyan
    Write-Host "Rows with screenshots: $packagesWithScreenshot" -ForegroundColor Cyan
    Write-Host "Total screenshots: $totalScreenshots" -ForegroundColor Cyan
}

function Test-UrlStatus {
    param(
        [Parameter(Mandatory)]
        [System.Net.Http.HttpClient] $Client,

        [Parameter(Mandatory)]
        [string] $Url,

        [Parameter(Mandatory)]
        [int] $MaxRetries,

        [Parameter(Mandatory)]
        [int] $RetryDelayMilliseconds
    )

    for ($attempt = 0; $attempt -le $MaxRetries; $attempt++) {
        try {
            $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $Url)
            try {
                $response = $Client.Send($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead)
                try {
                    return [pscustomobject] @{
                        StatusCode = [int] $response.StatusCode
                        Error      = $null
                    }
                }
                finally {
                    $response.Dispose()
                }
            }
            finally {
                $request.Dispose()
            }
        }
        catch [System.Net.Http.HttpRequestException] {
            if ($attempt -lt $MaxRetries) {
                Start-Sleep -Milliseconds $RetryDelayMilliseconds
                continue
            }

            return [pscustomobject] @{
                StatusCode = $null
                Error      = $_.Exception.Message
            }
        }
        catch [System.Threading.Tasks.TaskCanceledException] {
            if ($attempt -lt $MaxRetries) {
                Start-Sleep -Milliseconds $RetryDelayMilliseconds
                continue
            }

            return [pscustomobject] @{
                StatusCode = $null
                Error      = 'Request timed out.'
            }
        }
    }
}

function Test-IconUrls {
    param(
        [Parameter(Mandatory)]
        [string] $JsonPath,

        [Parameter(Mandatory)]
        [string] $InvalidUrlsPath,

        [Parameter(Mandatory)]
        [switch] $AppendInvalidUrls,

        [Parameter(Mandatory)]
        [int] $MaxPackages,

        [Parameter(Mandatory)]
        [int] $MaxRetries,

        [Parameter(Mandatory)]
        [int] $RetryDelayMilliseconds,

        [Parameter(Mandatory)]
        [int] $RequestTimeoutSeconds
    )

    if (-not (Test-Path -LiteralPath $JsonPath)) {
        throw "JSON file not found: $JsonPath"
    }

    $existingInvalidUrls = Get-ForbiddenUrlSet -Path $InvalidUrlsPath
    $invalidToAppend = New-Object System.Collections.Generic.List[string]
    $httpClient = [System.Net.Http.HttpClient]::new()
    $httpClient.Timeout = [TimeSpan]::FromSeconds($RequestTimeoutSeconds)
    $httpClient.DefaultRequestHeaders.UserAgent.ParseAdd('UniGetUI-IconValidator/1.0')

    $processed = 0
    $accepted = 0
    $invalid = 0
    $unexpected = 0
    $errors = 0

    try {
        $jsonDocument = [System.Text.Json.JsonDocument]::Parse([System.IO.File]::ReadAllText($JsonPath))
        try {
            $packages = $jsonDocument.RootElement.GetProperty('icons_and_screenshots')
            foreach ($package in $packages.EnumerateObject()) {
                if ($MaxPackages -gt 0 -and $processed -ge $MaxPackages) {
                    break
                }

                $iconUrl = $package.Value.GetProperty('icon').GetString()
                if ([string]::IsNullOrWhiteSpace($iconUrl)) {
                    continue
                }

                $processed++
                $result = Test-UrlStatus -Client $httpClient -Url $iconUrl -MaxRetries $MaxRetries -RetryDelayMilliseconds $RetryDelayMilliseconds

                if ($null -eq $result.StatusCode) {
                    $errors++
                    Write-Warning "[$($package.Name)] $iconUrl -> $($result.Error)"
                    continue
                }

                switch ([int] $result.StatusCode) {
                    200 {
                        $accepted++
                    }
                    403 {
                        $accepted++
                    }
                    404 {
                        $invalid++
                        Write-Warning "[$($package.Name)] $iconUrl returned 404"
                        if ($AppendInvalidUrls -and -not $existingInvalidUrls.Contains($iconUrl)) {
                            [void] $existingInvalidUrls.Add($iconUrl)
                            $invalidToAppend.Add($iconUrl)
                        }
                    }
                    default {
                        $unexpected++
                        Write-Warning "[$($package.Name)] $iconUrl returned status code $($result.StatusCode)"
                    }
                }
            }
        }
        finally {
            $jsonDocument.Dispose()
        }
    }
    finally {
        $httpClient.Dispose()
    }

    if ($AppendInvalidUrls -and $invalidToAppend.Count -gt 0) {
        $linesToAppend = ($invalidToAppend -join [Environment]::NewLine) + [Environment]::NewLine
        $encoding = [System.Text.UTF8Encoding]::new($false)
        [System.IO.File]::AppendAllText($InvalidUrlsPath, $linesToAppend, $encoding)
        Write-Host "Appended $($invalidToAppend.Count) invalid URLs to $InvalidUrlsPath" -ForegroundColor Yellow
    }

    Write-Host "Validated $processed icon URLs from $JsonPath" -ForegroundColor Green
    Write-Host "Accepted (200/403): $accepted" -ForegroundColor Cyan
    Write-Host "Invalid (404): $invalid" -ForegroundColor Cyan
    Write-Host "Unexpected statuses: $unexpected" -ForegroundColor Cyan
    Write-Host "Request errors: $errors" -ForegroundColor Cyan
}

$resolvedWorkbookPath = Resolve-RepoPath -Path $WorkbookPath
$resolvedJsonPath = Resolve-RepoPath -Path $JsonPath
$resolvedInvalidUrlsPath = Resolve-RepoPath -Path $InvalidUrlsPath
$resolvedNewUrlsPath = Resolve-RepoPath -Path $NewUrlsPath

if ($Export) {
    Export-IconDatabase `
        -WorkbookPath $resolvedWorkbookPath `
        -JsonPath $resolvedJsonPath `
        -InvalidUrlsPath $resolvedInvalidUrlsPath `
        -NewUrlsPath $resolvedNewUrlsPath `
        -MaxScreenshotsPerPackage $MaxScreenshotsPerPackage
}
elseif ($Validate) {
    Test-IconUrls `
        -JsonPath $resolvedJsonPath `
        -InvalidUrlsPath $resolvedInvalidUrlsPath `
        -AppendInvalidUrls:$AppendInvalidUrls `
        -MaxPackages $MaxPackages `
        -MaxRetries $MaxRetries `
        -RetryDelayMilliseconds $RetryDelayMilliseconds `
        -RequestTimeoutSeconds $RequestTimeoutSeconds
}