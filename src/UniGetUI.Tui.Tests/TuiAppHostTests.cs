using System.Diagnostics;
using UniGetUI.Tui.FakeData;
using UniGetUI.Tui.Infrastructure;

namespace UniGetUI.Tui.Tests;

public class TuiAppHostTests
{
    [Theory]
    [InlineData("tui")]
    [InlineData("TUI")]
    public void RecognizesTerminalCommandBeforeItsOptions(string command)
        => Assert.True(TuiAppHost.IsCommand([command, "--fake-data", "--page", "updates"]));

    [Fact]
    public void DoesNotRouteDesktopOrAutomationArgumentsToTerminal()
    {
        Assert.False(TuiAppHost.IsCommand([]));
        Assert.False(TuiAppHost.IsCommand(["--daemon"]));
        Assert.False(TuiAppHost.IsCommand(["app", "status"]));
        Assert.False(TuiAppHost.IsCommand(["--set-setting-value", "key", "tui"]));
        Assert.False(TuiAppHost.IsCommand(["tui.ubundle"]));
    }

    [Fact]
    public async Task MainExecutablePrintsTerminalHelpWithoutStartingDesktop()
    {
        var (code, output, error) = await RunHost("--help");
        Assert.Equal(0, code);
        Assert.Contains("Usage: unigetui tui", output);
        Assert.Contains("uniget tui", output);
        Assert.Contains("--fake-data", output);
        Assert.DoesNotContain("Initializing UniGetUI engine", output);
        Assert.Equal("", error);
    }

    [Fact]
    public async Task MainExecutableReturnsInvalidParameterForBadTerminalPage()
    {
        var (code, output, error) = await RunHost("--page", "not-a-page");
        Assert.Equal(2, code);
        Assert.Contains("Unknown page", error);
        Assert.Contains("tui --help", error);
        Assert.DoesNotContain("Initializing UniGetUI engine", output);
    }

    [Theory]
    [InlineData("--unknown-option")]
    [InlineData("--fake-data-dir")]
    [InlineData("--set-setting-value", "PreferredLanguage")]
    [InlineData("--fake-data-dir", "--page")]
    public async Task MainExecutableRejectsMalformedArgumentsBeforeInitialization(params string[] args)
    {
        var (code, output, error) = await RunHost(args);
        Assert.Equal(2, code);
        Assert.Contains(args[0], error);
        Assert.Contains("tui --help", error);
        Assert.DoesNotContain("Initializing UniGetUI engine", output);
        Assert.DoesNotContain("FAKE DATA MODE", output);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--fake-data")]
    [InlineData("--fake-data-dir")]
    [InlineData("--updateapps")]
    [InlineData("--enable-setting")]
    [InlineData("--export-settings")]
    public async Task MainExecutableStoresOptionLikeValuesWithoutReinterpretingThem(string value)
    {
        string sandbox = Path.Join(Path.GetTempPath(), $"tui-setting-value-{Guid.NewGuid():N}");
        try
        {
            var (code, output, error) = await RunHost(
                "--fake-data-dir", sandbox, "--set-setting-value", "PreferredLanguage", value);
            Assert.Equal(0, code);
            Assert.Contains("Done.", output);
            Assert.Equal("", error);
            Assert.DoesNotContain("Usage:", output);
            Assert.DoesNotContain("Initializing UniGetUI engine", output);
            Assert.Equal(value, File.ReadAllText(
                Path.Join(sandbox, "UniGetUIData", "Configuration", "PreferredLanguage")));
        }
        finally
        {
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public async Task HeadlessSettingsCommandKeepsDesktopPrecedence()
    {
        string sandbox = Path.Join(Path.GetTempPath(), $"tui-setting-priority-{Guid.NewGuid():N}");
        try
        {
            var (code, output, error) = await RunHost(
                "--fake-data-dir", sandbox, "--disable-setting", "DisableTelemetry",
                "--enable-setting", "DisableTelemetry");
            Assert.Equal(0, code);
            Assert.Contains("Done.", output);
            Assert.Equal("", error);
            Assert.True(File.Exists(
                Path.Join(sandbox, "UniGetUIData", "Configuration", "DisableTelemetry")));
        }
        finally
        {
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public async Task MainExecutableRunsFakeManagerWithoutStartingEitherUi()
    {
        string sandbox = Path.Join(Path.GetTempPath(), $"tui-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandbox);
        string statePath = Path.Join(sandbox, "fake-system-state.json");
        try
        {
            var (code, output, error) = await RunHost(
                "--fake-pm", "--state", statePath, "Winget", "install",
                "--id", "Proseware.Archiver");
            Assert.Equal(0, code);
            Assert.Contains("Successfully installed", output);
            Assert.Equal("", error);
            Assert.Contains(FakeStateStore.Read(statePath).Installed,
                p => p.Id == "Proseware.Archiver" && p.Version == "24.08");
            Assert.False(Directory.Exists(Path.Join(sandbox, "UniGetUIData")));
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public async Task RedirectedInteractiveStartupFailsBeforeActivatingFakeData()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (code, output, error) = await RunHost("--fake-data");
        Assert.Equal(1, code);
        Assert.Contains("requires console input and output", error);
        Assert.DoesNotContain("Initializing UniGetUI engine", output);
        Assert.DoesNotContain("FAKE DATA MODE", output);
    }

    [Fact]
    public void SubprocessUsesMainExecutableAndTerminalPrefix()
    {
        string exe = Path.Join(Path.GetTempPath(), "host with spaces", "UniGetUI.exe");
        var (path, prefix) = TuiProcessHost.Resolve(exe, Path.GetTempPath(), windows: true);
        Assert.Equal(exe, path);
        Assert.Equal(["tui"], prefix);
    }

    [Fact]
    public async Task MainExecutableRunsFakeElevatorWithTerminalHostPrefix()
    {
        string sandbox = Path.Join(Path.GetTempPath(), $"tui-elevated-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandbox);
        string statePath = Path.Join(sandbox, "fake-system-state.json");
        try
        {
            var (host, prefix) = TuiProcessHost.Resolve();
            var (code, output, error) = await RunHost(
                ["--fake-elevate", host, .. prefix, "--fake-pm", "--state", statePath,
                    "Winget", "install", "--id", "Proseware.Archiver"]);
            Assert.Equal(0, code);
            Assert.Contains("Successfully installed", output);
            Assert.Equal("", error);
            Assert.Contains(FakeStateStore.Read(statePath).Installed,
                p => p.Id == "Proseware.Archiver" && p.Version == "24.08");
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    public async Task InheritedFakeCacheCommandDoesNotStartDesktop(string mode)
    {
        var (code, output, error) = await RunMainHost(true, "cache", mode, "--pid", "1");
        Assert.Equal(0, code);
        Assert.Contains("Pretending to cache administrator rights", output);
        Assert.DoesNotContain("Initializing UniGetUI engine", output);
        Assert.Equal("", error);
    }

    [Fact]
    public void TestHostUsesMainApphostBesideItsAssembly()
    {
        string directory = Path.Join(Path.GetTempPath(), $"tui managed host {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string exe = Path.Join(directory, "UniGetUI.exe");
        File.WriteAllText(exe, "");
        try
        {
            var (path, prefix) = TuiProcessHost.Resolve("dotnet", directory, windows: true);
            Assert.Equal(exe, path);
            Assert.Equal(["tui"], prefix);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingMainHostFailsInsteadOfLaunchingASeparateTuiExecutable()
        => Assert.Throws<FileNotFoundException>(() =>
            TuiProcessHost.Resolve("testhost", Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N")), windows: true));

    private static async Task<(int Code, string Output, string Error)> RunHost(params string[] args)
        => await RunMainHost(false, [TuiAppHost.Command, .. args]);

    private static async Task<(int Code, string Output, string Error)> RunMainHost(bool fakeChild, params string[] args)
    {
        var (host, _) = TuiProcessHost.Resolve();
        var info = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        info.Environment[FakePackageManagerProcess.StepDelayVariable] = "0";
        info.Environment[FakePackageManagerProcess.ChildMarkerVariable] = fakeChild ? "1" : "0";
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Could not start the UniGetUI test host.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}
