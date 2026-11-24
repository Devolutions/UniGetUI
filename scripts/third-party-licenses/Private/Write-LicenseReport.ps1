function Write-LicenseReport
{
    param(
        [Parameter(Mandatory)] [string] $AppName,
        [Parameter(Mandatory)] [int] $ComponentCount,
        [Parameter(Mandatory)] [string] $NoticesPath,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Changes,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Issues,
        [bool] $HasLock
    )

    $licenseChanges = @($Changes | Where-Object Kind -eq 'LicenseChanged')
    $errors = @($Issues | Where-Object Severity -eq 'Error')
    $warnings = @($Issues | Where-Object Severity -eq 'Warning')
    $status = if ($errors.Count) { '🔴' } elseif ($warnings.Count) { '🟡' } else { '🟢' }
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("### $status $AppName - $($errors.Count) errors, $($warnings.Count) warnings")
    $lines.Add('')
    $lines.Add('<details><summary>Details</summary>')
    $lines.Add('')
    $lines.Add("- Components: $ComponentCount")
    $lines.Add("- Notices file: ``$NoticesPath``")
    $lines.Add("- License changes: $($licenseChanges.Count)")
    $lines.Add("- Other changes: $($Changes.Count - $licenseChanges.Count)")
    $lines.Add("- Issues: $($Issues.Count)")

    if ($Changes.Count)
    {
        $lines.Add('')
        $lines.Add('| Change | Component | Before | After |')
        $lines.Add('|---|---|---|---|')
        foreach ($change in $Changes)
        {
            $kind = if ($change.Kind -eq 'LicenseChanged') { '**License changed**' } else { $change.Kind }
            $lines.Add("| $kind | $($change.Ecosystem):$($change.Id) | $($change.Before) | $($change.After) |")
        }
    }

    if ($Issues.Count)
    {
        $lines.Add('')
        $lines.Add('| Severity | Issue | Component | Detail |')
        $lines.Add('|---|---|---|---|')
        foreach ($issue in $Issues | Sort-Object -Stable { @{ Error = 0; Warning = 1 }[$_.Severity] ?? 2 })
        {
            $icon = @{ Error = '🔴 '; Warning = '🟡 ' }[$issue.Severity]
            $lines.Add("| $icon$($issue.Severity) | $($issue.Kind) | $($issue.Ecosystem):$($issue.Id) | $($issue.Detail) |")
        }
    }

    if (-not $Changes.Count -and $HasLock)
    {
        $lines.Add('')
        $lines.Add('No change since the committed lock file.')
    }

    $lines.Add('')
    $lines.Add('</details>')
    $lines.Add('')

    $lines | ForEach-Object { Write-Host $_ }

    if ($env:GITHUB_ACTIONS -eq 'true')
    {
        if ($env:GITHUB_STEP_SUMMARY)
        {
            $lines | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
        }

        foreach ($change in $licenseChanges)
        {
            Write-Host "::warning title=License changed::$(Format-AnnotationMessage "$($change.Ecosystem):$($change.Id) changed from '$($change.Before)' to '$($change.After)' ($AppName)")"
        }

        foreach ($issue in $errors)
        {
            Write-Host "::error title=$($issue.Kind)::$(Format-AnnotationMessage "$($issue.Ecosystem):$($issue.Id) - $($issue.Detail) ($AppName)")"
        }

        foreach ($issue in $warnings)
        {
            Write-Host "::warning title=$($issue.Kind)::$(Format-AnnotationMessage "$($issue.Ecosystem):$($issue.Id) - $($issue.Detail) ($AppName)")"
        }
    }
}

function Format-AnnotationMessage
{
    param([string] $Message)

    return $Message.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
}
