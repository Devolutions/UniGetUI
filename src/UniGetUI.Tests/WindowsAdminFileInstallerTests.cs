using System.Diagnostics;
using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public sealed class WindowsAdminFileInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(WindowsAdminFileInstallerTests), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ElevatesTheSystemRobocopyWithoutAShell()
    {
        if (!OperatingSystem.IsWindows()) return;
        var startInfo = WindowsAdminFileInstaller.CreateStartInfo(@"C:\Staging Dir", @"C:\Program Data\Org & Co\SoftwareCatalog.json");
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "robocopy.exe"), startInfo.FileName);
        Assert.Equal("runas", startInfo.Verb);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
        Assert.Equal([@"C:\Staging Dir", @"C:\Program Data\Org & Co", "SoftwareCatalog.json"], startInfo.ArgumentList.Take(3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheCopyCommandCreatesTheFolderAndReplacesTheFile(bool destinationExists)
    {
        if (!OperatingSystem.IsWindows()) return;
        string staging = Path.Combine(_root, "staging dir");
        string destination = Path.Combine(_root, "dest & folder", "nested", "SoftwareCatalog.json");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "SoftwareCatalog.json"), "new");
        if (destinationExists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllTextAsync(destination, "old contents");
            File.SetLastWriteTimeUtc(destination, DateTime.UtcNow.AddDays(1));
        }

        var startInfo = WindowsAdminFileInstaller.CreateStartInfo(staging, destination);
        startInfo.Verb = "";
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync();

        Assert.InRange(process.ExitCode, 0, 7);
        Assert.Equal("new", await File.ReadAllTextAsync(destination));
    }
}
