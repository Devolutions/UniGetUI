using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using UniGetUI.PackageEngine.Managers.CargoManager;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class CargoInteroperabilityTests
{
    private static string Target =>
        (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64-" : "x86_64-")
        + (OperatingSystem.IsWindows() ? "pc-windows-msvc"
            : OperatingSystem.IsMacOS() ? "apple-darwin" : "unknown-linux-gnu");
    private static string Bin(string name) => name + (OperatingSystem.IsWindows() ? ".exe" : "");

    private static Task NativeInstall(CargoTestSandbox sandbox) => CargoInstallTracker.InstallAsync(
        sandbox.PathFor("root"), CargoTestData.Plan(target: Target, binaries: ["fixture-one", "fixture-two"]),
        new Dictionary<string, byte[]>
        {
            [Bin("fixture-one")] = CargoTestData.Executable(Target),
            [Bin("fixture-two")] = CargoTestData.Executable(Target),
        }, default);

    private static Process StartCargo(CargoTestSandbox sandbox, params string[] arguments)
    {
        ProcessStartInfo info = new("cargo")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = sandbox.Root,
        };
        info.Environment["CARGO_HOME"] = sandbox.PathFor("home");
        info.Environment["CARGO_INSTALL_ROOT"] = sandbox.PathFor("root");
        info.Environment.Remove("CARGO_BUILD_TARGET");
        info.Environment["CARGO_NET_OFFLINE"] = "true";
        info.Environment["CARGO_TERM_COLOR"] = "never";
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        var process = new Process { StartInfo = info };
        Assert.True(process.Start(), "Cargo did not start.");
        return process;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCargo(
        CargoTestSandbox sandbox, params string[] arguments)
    {
        using Process process = StartCargo(sandbox, arguments);
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task RealCargoListsNativeMultiBinaryInstallAndUninstallsFromIsolatedRoot()
    {
        using var sandbox = new CargoTestSandbox();
        await NativeInstall(sandbox);
        var listed = await RunCargo(sandbox, "install", "--list", "--root", sandbox.PathFor("root"), "--offline");
        Assert.True(listed.ExitCode == 0, $"cargo install --list failed: {listed.Error}");
        var entry = Assert.Single(Cargo.ParseInstallList(listed.Output.Replace("\r\n", "\n").Split('\n')));
        Assert.Equal(CargoTestData.Id, entry.Id);
        Assert.Equal(CargoTestData.Version, entry.InstalledVersion);
        Assert.True(entry.IsCratesIo);
        Assert.Contains("    " + Bin("fixture-one"), listed.Output);
        Assert.Contains("    " + Bin("fixture-two"), listed.Output);
        var removed = await RunCargo(sandbox, "uninstall", "--root", sandbox.PathFor("root"), CargoTestData.Id, "--offline");
        Assert.True(removed.ExitCode == 0, $"cargo uninstall failed: {removed.Error}");
        Assert.False(File.Exists(sandbox.PathFor("root/bin/" + Bin("fixture-one"))));
        Assert.False(File.Exists(sandbox.PathFor("root/bin/" + Bin("fixture-two"))));
        var v1 = CargoConfiguration.Parse(File.ReadAllText(sandbox.PathFor("root/.crates.toml")));
        Assert.Empty(CargoConfiguration.Table(v1, "v1")!);
        Assert.Empty(JsonNode.Parse(File.ReadAllText(sandbox.PathFor("root/.crates2.json")))!
            ["installs"]!.AsObject());
        var empty = await RunCargo(sandbox, "install", "--list", "--root", sandbox.PathFor("root"), "--offline");
        Assert.True(empty.ExitCode == 0, empty.Error);
        Assert.Empty(Cargo.ParseInstallList(empty.Output.Split('\n')));
    }

    [Fact]
    public async Task SeparateCargoProcessWaitsForNativeTrackingLockAndSucceedsAfterRelease()
    {
        using var sandbox = new CargoTestSandbox();
        await NativeInstall(sandbox);
        CargoTrackingLock? held = await CargoTrackingLock.AcquireAsync(sandbox.PathFor("root"), default);
        using Process process = StartCargo(sandbox, "uninstall", "--root", sandbox.PathFor("root"),
            CargoTestData.Id, "--offline");
        var blocking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new StringBuilder();
        async Task ReadError()
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                error.AppendLine(line);
                if (line.Contains("Blocking", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("lock", StringComparison.OrdinalIgnoreCase))
                    blocking.TrySetResult();
            }
        }
        Task stderr = ReadError();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task exited = process.WaitForExitAsync();
        try
        {
            Task first = await Task.WhenAny(blocking.Task, exited).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(first == blocking.Task, "Cargo did not contend on the native lock: " + error);
            Assert.False(process.HasExited);
            Assert.True(File.Exists(sandbox.PathFor("root/bin/" + Bin("fixture-one"))));
            held.Dispose();
            held = null;
            await exited.WaitAsync(TimeSpan.FromSeconds(30));
            await stderr;
            await stdout;
            Assert.True(process.ExitCode == 0, "Cargo failed after lock release: " + error);
            Assert.False(File.Exists(sandbox.PathFor("root/bin/" + Bin("fixture-one"))));
            Assert.False(File.Exists(sandbox.PathFor("root/bin/" + Bin("fixture-two"))));
        }
        finally
        {
            held?.Dispose();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await stderr;
            await stdout;
        }
    }

    [Fact]
    public async Task CanceledContenderDoesNotLeakTrackingLocks()
    {
        using var sandbox = new CargoTestSandbox();
        string root = sandbox.PathFor("root");
        using (var held = await CargoTrackingLock.AcquireAsync(root, default))
        {
            using var cancellation = new CancellationTokenSource();
            Task<CargoTrackingLock> pending = CargoTrackingLock.AcquireAsync(root, cancellation.Token);
            Assert.False(pending.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        }
        using var acquired = await CargoTrackingLock.AcquireAsync(root, default);
        Assert.Equal(Path.Join(root, ".crates.toml"), acquired.V1.Name);
        Assert.Equal(Path.Join(root, ".crates2.json"), acquired.V2.Name);
        Assert.True(acquired.V1.CanWrite);
        Assert.True(acquired.V2.CanWrite);
    }
}
