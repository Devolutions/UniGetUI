[CmdletBinding()]
param(
    [string] $Repository = 'Devolutions/UniGetUI',

    [string] $AssetName = 'UniGetUI.Installer.x64.exe',

    [Parameter(Mandatory)]
    [string] $DownloadDir,

    [Parameter(Mandatory)]
    [string] $ArtifactsDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Must match AppId in UniGetUI.iss; Inno Setup appends "_is1" to the uninstall key.
$uninstallKeyName = '{889610CC-4337-4BDB-AC3B-4F21806C0BDE}_is1'

function Invoke-Gh {
    param(
        [Parameter(ValueFromRemainingArguments)]
        [string[]] $Arguments
    )

    $output = & gh @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $message = ($output | Out-String).Trim()
        throw "gh $($Arguments -join ' ') failed: $message"
    }
    return $output
}

New-Item -Path $DownloadDir -ItemType Directory -Force | Out-Null
New-Item -Path $ArtifactsDir -ItemType Directory -Force | Out-Null

$tag = ((Invoke-Gh release view --repo $Repository --json tagName --jq .tagName) | Out-String).Trim()
if ([string]::IsNullOrWhiteSpace($tag)) {
    throw "Could not resolve the latest release of $Repository"
}
Write-Host "Latest $Repository release: $tag"

Invoke-Gh release download $tag --repo $Repository --pattern $AssetName --pattern 'checksums.txt' --dir $DownloadDir --clobber | Out-Null

$installerPath = Join-Path $DownloadDir $AssetName
$checksumsPath = Join-Path $DownloadDir 'checksums.txt'

$expectedHash = $null
foreach ($line in Get-Content -Path $checksumsPath) {
    if ($line -match '^(?<hash>[0-9A-Fa-f]{64})\s+\*?(?<name>.+)$' -and $Matches.name.Trim() -eq $AssetName) {
        $expectedHash = $Matches.hash.ToUpperInvariant()
        break
    }
}
if ($null -eq $expectedHash) {
    throw "$AssetName is not listed in checksums.txt of $tag"
}

$actualHash = (Get-FileHash -Path $installerPath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualHash -ne $expectedHash) {
    throw "SHA256 mismatch for $AssetName`: expected $expectedHash, got $actualHash"
}

$signature = Get-AuthenticodeSignature -FilePath $installerPath
if ($signature.Status -ne 'Valid') {
    throw "$AssetName signature is $($signature.Status): $($signature.StatusMessage)"
}
Write-Host "Installer signed by: $($signature.SignerCertificate.Subject)"

# Inno Setup flags plus the custom switches handled in UniGetUI.iss: no launch in the
# CI service session, and no Run key so logging on to the RDP session doesn't start it.
$installLogPath = Join-Path $ArtifactsDir 'unigetui-install.log'
$installerArguments = @(
    '/VERYSILENT',
    '/SUPPRESSMSGBOXES',
    '/NORESTART',
    '/ALLUSERS',
    '/NoAutoStart',
    '/NoRunOnStartup',
    "/LOG=`"$installLogPath`""
)
$process = Start-Process -FilePath $installerPath -ArgumentList $installerArguments -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "$AssetName exited with code $($process.ExitCode); see $installLogPath"
}

# UniGetUI.iss does not set ArchitecturesInstallIn64BitMode, so an all-users install
# registers itself in the 32-bit registry view.
$installLocation = $null
$uninstallRoots = @(
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
)
foreach ($root in $uninstallRoots) {
    $keyPath = Join-Path $root $uninstallKeyName
    if (Test-Path $keyPath) {
        $installLocation = (Get-Item -Path $keyPath).GetValue('InstallLocation', $null)
        break
    }
}
if ([string]::IsNullOrWhiteSpace($installLocation)) {
    throw "UniGetUI uninstall entry $uninstallKeyName was not found after installing"
}

$exePath = Join-Path $installLocation 'UniGetUI.exe'
if (-not (Test-Path $exePath)) {
    throw "UniGetUI.exe was not found in $installLocation"
}

$result = [pscustomobject]@{
    Tag = $tag
    InstallerSha256 = $actualHash
    ExePath = $exePath
    FileVersion = (Get-Item -Path $exePath).VersionInfo.FileVersion
}
$result | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $ArtifactsDir 'unigetui-install.json') -Encoding utf8NoBOM
$result | ConvertTo-Json -Compress
