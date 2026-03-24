$ErrorActionPreference = 'Stop'

$PackageArgs = @{
  packageName   = $env:ChocolateyPackageName
  softwareName  = 'unigetui*'
  fileType      = 'exe'
  silentArgs    = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes= @(0, 3010, 1605, 1614, 1641)
}

[array]$Key = Get-UninstallRegistryKey -SoftwareName $PackageArgs['softwareName']

if ($Key.Count -eq 1) {
  $Key | % {
    $PackageArgs['file'] = "$($_.UninstallString)"
    Uninstall-ChocolateyPackage @PackageArgs
  }
} elseif ($Key.Count -eq 0) {
  Write-Warning "$($PackageArgs['packageName']) has already been uninstalled by other means."
} elseif ($Key.Count -gt 1) {
  Write-Warning "$($Key.Count) matches found!"
  Write-Warning "To prevent accidental data loss, no programs will be uninstalled."
  Write-Warning "Please alert package maintainer the following keys were matched:"
  $Key | % { Write-Warning "- $($_.DisplayName)" }
}
