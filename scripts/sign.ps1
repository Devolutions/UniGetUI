#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Code-signs binaries and installer using AzureSignTool (Azure Key Vault).

.PARAMETER BinDir
    Directory containing binaries to sign (exe/dll files).

.PARAMETER InstallerPath
    Path to the installer .exe to sign (optional).

.PARAMETER FileListPath
    Path to a text file containing one file path per line to sign.

.PARAMETER AzureTenantId
    Azure AD tenant ID.

.PARAMETER KeyVaultUrl
    Azure Key Vault URL.

.PARAMETER ClientId
    Azure AD application (client) ID.

.PARAMETER ClientSecret
    Azure AD application client secret.

.PARAMETER CertificateName
    Name of the code-signing certificate in Key Vault.

.PARAMETER TimestampServer
    RFC 3161 timestamping server URL. Default: http://timestamp.digicert.com

.PARAMETER Install
    Install required code-signing tools before signing.
#>

[CmdletBinding()]
param(
    [string] $BinDir,
    [string] $InstallerPath,
    [string] $FileListPath,

    [Parameter(Mandatory)]
    [string] $AzureTenantId,

    [Parameter(Mandatory)]
    [string] $KeyVaultUrl,

    [Parameter(Mandatory)]
    [string] $ClientId,

    [Parameter(Mandatory)]
    [string] $ClientSecret,

    [Parameter(Mandatory)]
    [string] $CertificateName,

    [string] $TimestampServer = "http://timestamp.digicert.com",

    [switch] $Install
)

$ErrorActionPreference = 'Stop'

# --- Install tools if requested ---
if ($Install) {
    Write-Host "Installing code-signing tools..." -ForegroundColor Cyan
    dotnet tool install --global AzureSignTool 2>$null
    Install-Module -Name Devolutions.Authenticode -Force -Scope CurrentUser 2>$null

    # Trust test code-signing CA
    $TestCertsUrl = "https://raw.githubusercontent.com/Devolutions/devolutions-authenticode/master/data/certs"
    $CaCertPath = Join-Path $env:TEMP "authenticode-test-ca.crt"
    Invoke-WebRequest -Uri "$TestCertsUrl/authenticode-test-ca.crt" -OutFile $CaCertPath
    Import-Certificate -FilePath $CaCertPath -CertStoreLocation "cert:\LocalMachine\Root" | Out-Null
    Remove-Item $CaCertPath -ErrorAction SilentlyContinue
    Write-Host "Code-signing tools installed."
}

$SignParams = @(
    'sign',
    '-kvt', $AzureTenantId,
    '-kvu', $KeyVaultUrl,
    '-kvi', $ClientId,
    '-kvs', $ClientSecret,
    '-kvc', $CertificateName,
    '-tr', $TimestampServer,
    '-v'
)

function Invoke-BatchSign {
    param(
        [string[]] $Files
    )

    $Files = $Files | Where-Object { $_ -and (Test-Path $_) }
    if (-not $Files -or $Files.Count -eq 0) {
        Write-Warning "No files to sign."
        return
    }

    Write-Host "Signing $($Files.Count) files..."

    # Pass file paths via -ifl so we don't blow past the Windows 32K command-line limit.
    $tempListPath = [System.IO.Path]::GetTempFileName()
    try {
        Set-Content -Path $tempListPath -Value $Files -Encoding utf8
        AzureSignTool @SignParams -ifl $tempListPath
        if ($LASTEXITCODE -ne 0) {
            throw "AzureSignTool failed with exit code $LASTEXITCODE"
        }
    } finally {
        Remove-Item -Path $tempListPath -ErrorAction SilentlyContinue
    }
}

function Update-IntegrityTree {
    param(
        [string] $RootPath
    )

    if (-not $RootPath -or -not (Test-Path $RootPath -PathType Container)) {
        return
    }

    $TreeRefreshScriptPath = Join-Path $PSScriptRoot "refresh-integrity-tree.ps1"
    if (-not (Test-Path $TreeRefreshScriptPath -PathType Leaf)) {
        Write-Warning "Integrity tree refresh script not found at $TreeRefreshScriptPath"
        return
    }

    & $TreeRefreshScriptPath -Path $RootPath -FailOnUnexpectedFiles
    if ($LASTEXITCODE -ne 0) {
        throw "refresh-integrity-tree.ps1 failed with exit code $LASTEXITCODE"
    }
}

# --- Sign binaries in BinDir ---
if ($FileListPath -and (Test-Path $FileListPath)) {
    Write-Host "`n=== Signing binaries from list: $FileListPath ===" -ForegroundColor Cyan
    $filesToSign = Get-Content $FileListPath | Where-Object { $_ -and ($_ -notmatch '^\s*$') }
    Invoke-BatchSign -Files $filesToSign
} elseif ($BinDir -and (Test-Path $BinDir)) {
    Write-Host "`n=== Signing binaries in $BinDir ===" -ForegroundColor Cyan
    $filesToSign = Get-ChildItem -Path $BinDir -Include @("*.exe", "*.dll") -Recurse
    if ($filesToSign.Count -eq 0) {
        Write-Warning "No .exe or .dll files found in $BinDir"
    } else {
        Invoke-BatchSign -Files ($filesToSign | ForEach-Object { $_.FullName })
        Update-IntegrityTree -RootPath $BinDir
        Write-Host "Binary signing complete."
    }
}

# --- Sign installer ---
if ($InstallerPath -and (Test-Path $InstallerPath)) {
    Write-Host "`n=== Signing installer: $InstallerPath ===" -ForegroundColor Cyan
    AzureSignTool @SignParams $InstallerPath
    if ($LASTEXITCODE -ne 0) {
        throw "AzureSignTool failed for installer with exit code $LASTEXITCODE"
    }
    Write-Host "Installer signing complete."
}
