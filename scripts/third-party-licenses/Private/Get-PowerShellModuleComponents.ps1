function Get-PowerShellModuleComponents
{
    param(
        [Parameter(Mandatory)] [string] $Path,
        [hashtable] $UrlMappings
    )

    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count)
    {
        throw "Cannot parse module manifest '$Path': $($errors[0].Message)"
    }

    $statement = $ast.EndBlock.Statements | Select-Object -First 1
    if ($ast.EndBlock.Statements.Count -ne 1 -or $statement -isnot [Management.Automation.Language.PipelineAst] -or
        $statement.PipelineElements.Count -ne 1 -or
        $statement.PipelineElements[0] -isnot [Management.Automation.Language.CommandExpressionAst] -or
        $statement.PipelineElements[0].Expression -isnot [Management.Automation.Language.HashtableAst])
    {
        throw "Module manifest '$Path' must contain a root metadata table."
    }
    $rootTable = $statement.PipelineElements[0].Expression

    $getValueAst = {
        param([Management.Automation.Language.HashtableAst]$Table, [string]$Name)

        $pair = $Table.KeyValuePairs | Where-Object { $_.Item1.SafeGetValue() -eq $Name } | Select-Object -First 1
        if ($pair)
        {
            $pipeline = $pair.Item2
            if ($pipeline -is [Management.Automation.Language.PipelineAst] -and $pipeline.PipelineElements.Count -eq 1 -and
                $pipeline.PipelineElements[0] -is [Management.Automation.Language.CommandExpressionAst])
            {
                $pipeline.PipelineElements[0].Expression
            }
            else
            {
                throw "Module manifest '$Path' must declare '$Name' as static metadata."
            }
        }
    }

    $metadata = @{}
    foreach ($key in 'ModuleVersion', 'CompanyName', 'Copyright')
    {
        $value = & $getValueAst $rootTable $key
        if ($value) { $metadata[$key] = $value.SafeGetValue() }
    }
    $privateData = & $getValueAst $rootTable 'PrivateData'
    if ($privateData -is [Management.Automation.Language.HashtableAst])
    {
        $psData = & $getValueAst $privateData 'PSData'
        if ($psData -is [Management.Automation.Language.HashtableAst])
        {
            foreach ($key in 'ProjectUri', 'LicenseUri')
            {
                $value = & $getValueAst $psData $key
                if ($value) { $metadata[$key] = $value.SafeGetValue() }
            }
        }
    }
    if (-not $metadata.ModuleVersion) { return }

    $directory = Split-Path -Parent $Path
    $licenseFile = Get-ChildItem -LiteralPath $directory -File |
        Where-Object Name -Match '^(licen[cs]e|copying)(\.(txt|md))?$' | Select-Object -First 1
    $licenseText = if ($licenseFile) { Get-Content -LiteralPath $licenseFile.FullName -Raw } else { '' }
    $manifestText = Get-Content -LiteralPath $Path -Raw
    $license = if ($manifestText -match '(?i)Licensed under the MIT License' -or $licenseText -match '(?i)\bMIT License\b')
    {
        'MIT'
    }
    elseif ($metadata.LicenseUri -and $UrlMappings)
    {
        Resolve-LicenseFromUrl -Url $metadata.LicenseUri -UrlMappings $urlMappings
    }
    else { '' }
    $noticeFile = Get-ChildItem -LiteralPath $directory -File |
        Where-Object Name -Match '^(notice|thirdpartynotices)(\.(txt|md))?$' | Select-Object -First 1

    [pscustomobject]@{
        Ecosystem         = 'powershell'
        Name              = [IO.Path]::GetFileNameWithoutExtension($Path)
        Version           = [string]$metadata.ModuleVersion
        License           = $license
        Source            = [string]$metadata.ProjectUri
        Website           = ''
        Copyright         = [string]$metadata.Copyright
        LicenseText       = $licenseText
        LicenseTextOrigin = if ($licenseText) { 'package' } else { 'none' }
        RepositoryCommit  = ''
        IsFirstParty      = $metadata.CompanyName -match 'Devolutions|Ironman Software'
        IsRuntime         = $false
        Notice            = if ($noticeFile)
        {
            [pscustomobject]@{ Repository = ''; File = $noticeFile.Name; Url = ''; Text = Get-Content -LiteralPath $noticeFile.FullName -Raw }
        }
    }
}
