$ErrorActionPreference = 'Stop'

$PackageName = 'unigetui'
$Url = 'https://cdn.devolutions.net/download/Devolutions.UniGetUI.win-x64.$VAR1$.exe'

$PackageArgs = @{
  packageName   = $PackageName
  url           = $Url
  fileType      = 'exe'
  silentArgs    = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /ALLUSERS /NoWinGet /NoAutoStart'
  validExitCodes= @(0, 3010)
  checksum      = '$VAR2$'
  checksumType  = 'sha256'
}

Install-ChocolateyPackage @PackageArgs
