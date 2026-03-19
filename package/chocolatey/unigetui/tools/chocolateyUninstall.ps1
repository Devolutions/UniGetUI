$ErrorActionPreference = 'Stop'

$PackageName = 'unigetui'

$PackageArgs = @{
  packageName   = $PackageName
  softwareName  = 'UniGetUI'
  fileType      = 'exe'
  silentArgs    = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
  validExitCodes= @(0, 3010)
}

[array]$Key = Get-UninstallRegistryKey -SoftwareName $PackageArgs['softwareName']

if ($Key.Count -eq 1) {
  $Key | % {
    $uninstallExe = $_.UninstallString -replace '"', ''
    if (Test-Path $uninstallExe) {
      $PackageArgs['file'] = $uninstallExe
      Uninstall-ChocolateyPackage @PackageArgs
    } else {
      Write-Warning "Uninstaller not found at $uninstallExe"
    }
  }
} elseif ($Key.Count -eq 0) {
  Write-Warning "$PackageName has already been uninstalled."
} elseif ($Key.Count -gt 1) {
  Write-Warning "$($Key.Count) matches found!"
  Write-Warning "To prevent accidental data loss, no programs will be uninstalled."
  Write-Warning "The following keys were matched:"
  $Key | % {Write-Warning "- $($_.DisplayName)"}
}
