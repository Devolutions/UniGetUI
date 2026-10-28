using System.Diagnostics;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageOperations;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class KillProcessOperationTests
{
    [Fact]
    public async Task FailsWhenAProcessRefusesToCloseAndMayNotBeKilled()
    {
        Settings.Set(Settings.K.KillProcessesThatRefuseToDie, false);
        using var target = WindowlessProcess.Start();
        using var operation = new KillProcessOperation(target.Name);

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.False(target.Process.HasExited);
    }

    [Fact]
    public async Task SucceedsWhenAProcessThatRefusesToCloseIsKilled()
    {
        Settings.Set(Settings.K.KillProcessesThatRefuseToDie, false);
        using var target = WindowlessProcess.Start();
        using var operation = new KillProcessOperation(target.Name, forceKill: true);

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        Assert.True(target.Process.HasExited);
    }

    [Fact]
    public async Task SucceedsWhenNoProcessHasTheName()
    {
        using var operation = new KillProcessOperation($"unigetui-absent-{Guid.NewGuid():N}");

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Succeeded, operation.Status);
    }

    private sealed class WindowlessProcess : IDisposable
    {
        private readonly string _directory;

        public string Name { get; }
        public Process Process { get; }

        private WindowlessProcess(string directory, string name, Process process)
        {
            _directory = directory;
            Name = name;
            Process = process;
        }

        public static WindowlessProcess Start()
        {
            string directory = Path.Combine(Path.GetTempPath(), $"unigetui-killtest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            string name = $"unigetui-killtest-{Guid.NewGuid():N}";

            string source;
            string arguments;
            if (OperatingSystem.IsWindows())
            {
                source = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                arguments = "/d /c ping -n 120 127.0.0.1 >nul";
                name += ".exe";
            }
            else
            {
                source = File.Exists("/bin/sleep") ? "/bin/sleep" : "/usr/bin/sleep";
                arguments = "120";
            }

            string executable = Path.Combine(directory, name);
            File.Copy(source, executable);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(executable, File.GetUnixFileMode(source));

            var process = Process.Start(
                new ProcessStartInfo(executable, arguments)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                }
            )!;
            return new WindowlessProcess(directory, name, process);
        }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    Process.WaitForExit(10000);
                }
            }
            catch (InvalidOperationException) { }

            Process.Dispose();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
