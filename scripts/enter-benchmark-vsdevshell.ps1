$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installation)) {
    throw 'Could not locate Visual Studio with the x64 C++ build tools.'
}
& (Join-Path $installation 'Common7\Tools\Launch-VsDevShell.ps1') `
    -SkipAutomaticLocation -Arch amd64 -HostArch amd64 -NoLogo
if (-not (Get-Command dumpbin.exe -ErrorAction SilentlyContinue)) {
    throw 'The Visual Studio developer shell did not expose the native build tools.'
}
