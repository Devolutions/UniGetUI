param(
    [Parameter(Mandatory)]
    [ValidateSet('Restore', 'Build', 'NativeRestore', 'NativePublish')]
    [string]$Phase
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$vcpus = [Environment]::ProcessorCount
if ($vcpus -ne 4) {
    throw "This benchmark requires 4 vCPUs; the runner exposes $vcpus."
}
if ([string]::IsNullOrWhiteSpace($env:BENCHMARK_RUNNER)) {
    throw 'BENCHMARK_RUNNER must identify the runner being measured.'
}
$captureBinlog = [bool]::Parse($env:BENCHMARK_BINLOG)
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\UniGetUI.Avalonia\UniGetUI.Avalonia.csproj'
$projection = Join-Path $env:RUNNER_TEMP 'windows-projection'
$timingDir = Join-Path $env:RUNNER_TEMP 'build-timing'
New-Item $timingDir -ItemType Directory -Force | Out-Null
if (@(Get-ChildItem $projection -Filter '*.cs').Count -eq 0) {
    throw 'The common Windows projection sources are missing.'
}

$properties = @(
    '-p:Configuration=Release',
    '-p:Platform=x64',
    '-p:RuntimeIdentifier=win-x64',
    '-p:SelfContained=false',
    '-p:EnableWindowsTargeting=true',
    '-p:PublishAot=false',
    '-p:PublishReadyToRun=false',
    '-p:CsWinRTGenerateProjection=false',
    "-p:CsWinRTGeneratedFilesDir=$projection$([IO.Path]::DirectorySeparatorChar)",
    '-p:SkipBundledPingetCli=true'
)
$native = $Phase.StartsWith('Native')
if ($Phase -eq 'NativePublish' -and $IsWindows) {
    & (Join-Path $PSScriptRoot 'enter-benchmark-vsdevshell.ps1')
}
if ($native) {
    $properties = @($properties | Where-Object { $_ -notin @('-p:SelfContained=false', '-p:PublishAot=false') })
    $properties += @(
        '-p:SelfContained=true',
        '-p:PublishAot=true',
        '-p:BenchmarkNativeAot=true',
        "-p:CustomAfterMicrosoftCommonProps=$(Join-Path $PSScriptRoot 'benchmark-nativeaot.props')",
        '-p:UseExternalClang=true',
        "-p:AotAnywhereClangPath=$env:BENCHMARK_LLVM",
        '-p:SkipElevatedPolicyHelper=true',
        '-p:PublishTrimmed=true',
        '-p:TrimMode=full',
        '-p:TrimmerSingleWarn=false',
        '-p:NativeDebugSymbols=false'
    )
    if (-not $IsWindows) { $properties += '-p:UseAotCrtStub=true' }
}
# The app otherwise selects its framework from the host OS, not the target RID.
$framework = (dotnet msbuild $project @properties -nologo -getProperty:WindowsTargetFramework | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the Windows target framework.' }
$framework = $framework.Trim()
if ($framework -notmatch '^net\d+\.\d+-windows\d+\.\d+\.\d+\.\d+$') {
    throw "Unexpected Windows target framework: '$framework'."
}
$properties += @("-p:TargetFramework=$framework", "-p:SharedTargetFrameworks=$framework")
$windowsSdk = (dotnet msbuild $project @properties -nologo -getProperty:WindowsSdkPackageVersion | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($windowsSdk)) {
    throw 'Could not resolve the Windows SDK reference package version.'
}
# Restore removes TargetFramework from project references; keep their SDK pack
# version consistent with the explicitly Windows-targeted build.
$properties += "-p:WindowsSdkPackageVersion=$($windowsSdk.Trim())"
$sdk = (dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the .NET SDK version.' }

$command = switch ($Phase) {
    'Restore' { 'restore' }
    'Build' { 'build' }
    'NativeRestore' { 'restore' }
    'NativePublish' { 'publish' }
}
$arguments = @($command, $project) + $properties + @('--nologo', '--verbosity', 'minimal')
if ($Phase -in @('Build', 'NativePublish')) {
    dotnet build-server shutdown
    if ($LASTEXITCODE -ne 0) { throw 'Could not shut down build servers before the build measurement.' }
    $arguments += @('--no-restore', '-maxcpucount:1', '-nodeReuse:false', '-p:UseSharedCompilation=false')
}
$publishDir = Join-Path $env:RUNNER_TEMP 'benchmark-native-publish'
if ($Phase -eq 'NativePublish') { $arguments += @('--output', $publishDir) }
if ($captureBinlog) {
    $binlog = Join-Path $timingDir "$($Phase.ToLowerInvariant()).binlog"
    $arguments += "-bl:$binlog;ProjectImports=None"
}

Write-Host "Measuring $Phase on $env:BENCHMARK_RUNNER ($vcpus vCPUs, SDK $sdk, $framework, win-x64)"
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
try {
    & dotnet @arguments
    $exitCode = $LASTEXITCODE
}
finally {
    $stopwatch.Stop()
}

if ($exitCode -eq 0 -and $Phase -eq 'Build') {
    $output = Join-Path $root "src\UniGetUI.Avalonia\bin\x64\Release\$framework\win-x64\UniGetUI.dll"
    if (-not (Test-Path $output)) {
        Write-Host "::error::The managed Windows app assembly was not produced at $output."
        $exitCode = 1
    }
}
$nativeBytes = $null
if ($exitCode -eq 0 -and $Phase -eq 'NativePublish') {
    $executable = Join-Path $publishDir 'UniGetUI.exe'
    if (-not (Test-Path $executable)) {
        Write-Host "::error::NativeAOT did not produce $executable."
        $exitCode = 1
    }
    else {
        Add-Type -AssemblyName System.Reflection.Metadata
        $stream = [IO.File]::OpenRead($executable)
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            if ($pe.PEHeaders.CoffHeader.Machine -ne [System.Reflection.PortableExecutable.Machine]::Amd64 -or
                $null -ne $pe.PEHeaders.CorHeader) {
                Write-Host '::error::The published executable is not a native x64 Windows PE image.'
                $exitCode = 1
            }
            $nativeBytes = $stream.Length
        }
        finally {
            $pe.Dispose()
            $stream.Dispose()
        }
    }
}
$duration = [math]::Round($stopwatch.Elapsed.TotalSeconds, 2)
$timing = [ordered]@{
    phase = $Phase
    runner = $env:BENCHMARK_RUNNER
    os = $env:RUNNER_OS
    vcpus = $vcpus
    msbuildWorkers = 1
    sdk = $sdk
    framework = $framework
    configuration = 'Release'
    runtimeIdentifier = 'win-x64'
    commit = $env:GITHUB_SHA
    captureBinlog = $captureBinlog
    nativeDebugSymbols = if ($native) { $false } else { $null }
    nativeExecutableBytes = $nativeBytes
    durationSeconds = $duration
    exitCode = $exitCode
    succeeded = ($exitCode -eq 0)
}
$timing | ConvertTo-Json | Set-Content (Join-Path $timingDir "$env:BENCHMARK_RUNNER-$Phase.json") -Encoding utf8
$result = if ($exitCode -eq 0) { 'success' } else { 'failure' }
@(
    "## $Phase timing ($env:BENCHMARK_RUNNER)",
    '',
    "- Target: $framework / win-x64 / Release",
    "- SDK: $sdk; vCPUs: $vcpus; binlog: $captureBinlog",
    "- Duration: ${duration}s; result: $result",
    ''
) | Out-File $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
exit $exitCode
