param(
    [Parameter(Mandatory)]
    [ValidateSet('Restore', 'Build')]
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
# The app otherwise selects its framework from the host OS, not the target RID.
$framework = (dotnet msbuild $project @properties -nologo -getProperty:WindowsTargetFramework | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the Windows target framework.' }
$framework = $framework.Trim()
if ($framework -notmatch '^net\d+\.\d+-windows\d+\.\d+\.\d+\.\d+$') {
    throw "Unexpected Windows target framework: '$framework'."
}
$properties += "-p:TargetFramework=$framework"
$sdk = (dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the .NET SDK version.' }

$arguments = @($Phase.ToLowerInvariant(), $project) + $properties + @('--nologo', '--verbosity', 'minimal')
if ($Phase -eq 'Build') {
    dotnet build-server shutdown
    if ($LASTEXITCODE -ne 0) { throw 'Could not shut down build servers before the build measurement.' }
    $arguments += @('--no-restore', '-maxcpucount:4', '-nodeReuse:false', '-p:UseSharedCompilation=false')
}
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
$duration = [math]::Round($stopwatch.Elapsed.TotalSeconds, 2)
$timing = [ordered]@{
    phase = $Phase
    runner = $env:BENCHMARK_RUNNER
    os = $env:RUNNER_OS
    vcpus = $vcpus
    sdk = $sdk
    framework = $framework
    configuration = 'Release'
    runtimeIdentifier = 'win-x64'
    commit = $env:GITHUB_SHA
    captureBinlog = $captureBinlog
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
