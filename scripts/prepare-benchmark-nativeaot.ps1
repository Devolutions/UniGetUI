param(
    [Parameter(Mandatory)]
    [ValidateSet('Package', 'Consumer')]
    [string]$Mode
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root '.benchmark-tooling\AotAnywhere'
$version = '0.0.0-benchmark.7661138aebfa'

if ($Mode -eq 'Consumer') {
    $packages = Join-Path $root '.benchmark-tooling\packages'
    $config = Join-Path $root 'NuGet.Config'
    if (Test-Path $config) { throw 'Refusing to replace an existing repository NuGet.Config.' }
    # The SDK resolver needs the local fork package before restore starts.
    # This configuration exists only in the disposable CI checkout.
    dotnet new nugetconfig --output $root --force
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the benchmark NuGet configuration.' }
    dotnet nuget add source $packages --name benchmark-native-toolchain --configfile $config
    if ($LASTEXITCODE -ne 0) { throw 'Could not register the local AotAnywhere package source.' }
    if ($IsWindows) { return }
}

$hostRid = if ($IsWindows) { 'win-x64' } else { 'linux-x64' }
$manifest = Get-Content (Join-Path $source 'eng\toolchain-artifacts.json') -Raw | ConvertFrom-Json
$toolchain = @($manifest.clangToolsets | Where-Object hostRuntimeIdentifier -eq $hostRid)
if ($toolchain.Count -ne 1) { throw "No unique LLVM toolchain found for $hostRid." }
$toolchain = $toolchain[0]
$llvm = Join-Path $env:RUNNER_TEMP 'benchmark-llvm'
$archive = Join-Path $env:RUNNER_TEMP 'benchmark-llvm.tar.xz'
Invoke-WebRequest $toolchain.url -OutFile $archive
if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $toolchain.sha256) {
    throw 'The LLVM archive does not match the pinned AotAnywhere checksum.'
}
New-Item $llvm -ItemType Directory -Force | Out-Null
tar -xf $archive -C $llvm --strip-components=1
if ($LASTEXITCODE -ne 0) { throw 'Could not extract the pinned LLVM toolchain.' }
Remove-Item -LiteralPath $archive
$llvmBin = Join-Path $llvm 'bin'
$env:PATH = "$llvmBin$([IO.Path]::PathSeparator)$env:PATH"

if ($Mode -eq 'Package') {
    if (-not $IsWindows) { throw 'CRT stub and symbol-import preparation requires Windows.' }
    & (Join-Path $PSScriptRoot 'enter-benchmark-vsdevshell.ps1')
    $payload = Join-Path $env:RUNNER_TEMP 'benchmark-native-payload'
    $crt = Join-Path $payload 'aot-crt-stub'
    $imports = Join-Path $payload 'windows-sdk-imports'
    & (Join-Path $source 'eng\build-aot-crt-stub.ps1') -OutputDirectory $crt
    & (Join-Path $source 'eng\build-windows-sdk-imports.ps1') `
        -DllRoot (Join-Path $env:SystemRoot 'System32') `
        -OutputDirectory $imports -LlvmBinDirectory $llvmBin -Architecture x64
    Push-Location $source
    try {
        dotnet build -t:Pack 'src\AotAnywhere.nuproj' "-p:Version=$version" -p:Platform=AnyCPU `
            "-p:AotAnywhereAotCrtStubPayloadDir=$crt" `
            "-p:AotAnywhereWindowsSdkImportsPayloadDir=$imports" --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw 'Could not pack the pinned AotAnywhere fork.' }
    }
    finally {
        Pop-Location
    }
    $packages = Join-Path $env:RUNNER_TEMP 'benchmark-native-packages'
    New-Item $packages -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $source "src\bin\Debug\StuDev.AotAnywhere.$version.nupkg") $packages
}
else {
    "BENCHMARK_LLVM=$llvm" >> $env:GITHUB_ENV
}
