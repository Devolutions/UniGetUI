function Get-ComponentPage
{
    param([Parameter(Mandatory)] [pscustomobject] $Component)

    if ($Component.Source)
    {
        return ([string]$Component.Source).Trim() -replace '^git\+', '' -replace '^git://', 'https://' -replace '^git@([^:]+):', 'https://$1/' -replace '\.git/?$', ''
    }

    if ($Component.IsFirstParty)
    {
        return ''
    }

    if ($Component.Ecosystem -eq 'nuget' -and -not $Component.IsRuntime)
    {
        return "https://www.nuget.org/packages/$($Component.Name)"
    }

    if ($Component.Ecosystem -eq 'npm')
    {
        return "https://www.npmjs.com/package/$($Component.Name)"
    }

    return $Component.Source
}

function Format-Notices
{
    param(
        [Parameter(Mandatory)] [string] $TemplatePath,
        [Parameter(Mandatory)] [string] $AppName,
        [string] $AppVersion,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Components
    )

    $template = (Get-Content -LiteralPath $TemplatePath -Raw) -replace "`r`n", "`n"
    $blockPattern = '(?s)\{\{#packages\}\}\n?(?<block>.*?)\{\{/packages\}\}\n?'
    $match = [regex]::Match($template, $blockPattern)
    if (-not $match.Success)
    {
        throw "Template '$TemplatePath' has no {{#packages}}...{{/packages}} block."
    }

    $expandHeader = {
        param($text)
        $text = if ($AppVersion) { $text.Replace('{{app_version}}', $AppVersion) } else { $text.Replace(' {{app_version}}', '').Replace('{{app_version}}', '') }
        $text.Replace('{{app_name}}', $AppName).Replace('{{package_count}}', [string]$Components.Count)
    }

    $sharedNotices = [ordered]@{}
    $noticeGroups = $Components | Where-Object { $_.Notice -and $_.Notice.Text } | Group-Object { $_.Notice.Text } | Where-Object Count -gt 1
    foreach ($group in $noticeGroups)
    {
        $sha = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($group.Name))
        $sharedNotices[$group.Name] = [pscustomobject]@{
            Id         = 'NOTICE-' + [Convert]::ToHexString($sha).Substring(0, 8).ToLowerInvariant()
            Components = @($group.Group | ForEach-Object { "$($_.Name) $($_.Version)" })
        }
    }

    # A text a notice refers to by label (the GPL v3 of an LGPL notice) is printed once, like a shared NOTICE
    foreach ($component in $Components)
    {
        foreach ($shared in @($component.SharedTexts | Where-Object { $_ }))
        {
            if ($sharedNotices.Contains($shared.Text)) { $sharedNotices[$shared.Text].Id = $shared.Id }
            else
            {
                $holders = @($Components | Where-Object { $_.Notice -and $_.Notice.Text -eq $shared.Text } | ForEach-Object { "$($_.Name) $($_.Version)" })
                $sharedNotices[$shared.Text] = [pscustomobject]@{ Id = $shared.Id; Components = $holders }
            }
            $sharedNotices[$shared.Text].Components += "$($component.Name) $($component.Version)"
        }
    }

    $rendered = [Text.StringBuilder]::new()
    foreach ($component in $Components)
    {
        $notice = ''
        if ($component.Notice -and $component.Notice.Text)
        {
            $shared = $sharedNotices[$component.Notice.Text]
            $notice = if ($shared) { "`n`nNotice: see [$($shared.Id)]" } else { "`n`nNotice:`n$($component.Notice.Text)" }
        }

        $values = [ordered]@{
            name                = $component.Name
            version             = $component.Version
            license             = $component.License
            source              = Get-ComponentPage -Component $component
            repository          = $component.Source
            website             = $component.Website
            copyright           = $component.Copyright
            ecosystem           = $component.Ecosystem
            license_text        = $component.LicenseText
            notice              = $notice
        }

        $entry = [regex]::Replace($match.Groups['block'].Value, '\{\{(?<token>[a-z_]+)\}\}', {
            param($token)
            $key = $token.Groups['token'].Value
            if ($values.Contains($key)) { [string]$values[$key] } else { $token.Value }
        })

        [void]$rendered.Append($entry)
    }

    $noticesSection = ''
    if ($sharedNotices.Count)
    {
        $rule = '=' * 60
        $section = [Text.StringBuilder]::new()
        [void]$section.Append("`n$rule`nATTRIBUTION NOTICES (NOTICE)`n$rule`n")
        foreach ($entry in ($sharedNotices.GetEnumerator() | Sort-Object { $_.Value.Id }))
        {
            $indent = ' ' * 'Applies to: '.Length
            $appliesTo = $entry.Value.Components -join ",`n$indent"
            [void]$section.Append("`n[$($entry.Value.Id)]`nApplies to: $appliesTo`n`n$($entry.Key)`n")
        }

        $noticesSection = $section.ToString()
    }

    $header = & $expandHeader $template.Substring(0, $match.Index)
    $footer = (& $expandHeader $template.Substring($match.Index + $match.Length)).Replace('{{notices}}', $noticesSection)
    return ($header + $rendered.ToString() + $footer).TrimEnd() + "`n"
}
