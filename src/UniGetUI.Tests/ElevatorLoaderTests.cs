using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public sealed class ElevatorLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(ElevatorLoaderTests), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string CreateElevator(string directory)
    {
        string path = Path.Join(directory, "Assets", "Utilities", "UniGetUI Elevator.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void PrefersTheElevatorNextToTheExecutable()
    {
        string executableDirectory = Path.Join(_root, "app");
        string expected = CreateElevator(executableDirectory);
        CreateElevator(_root);

        Assert.Equal(expected, ElevatorLoader.ResolveBundledElevatorPath(executableDirectory));
    }

    [Fact]
    public void FallsBackToTheElevatorInTheParentDirectory()
    {
        string executableDirectory = Path.Join(_root, "app");
        Directory.CreateDirectory(executableDirectory);
        string expected = CreateElevator(_root);

        Assert.Equal(expected, ElevatorLoader.ResolveBundledElevatorPath(executableDirectory));
    }

    [Fact]
    public void ReturnsNullWhenNoElevatorIsBundled()
    {
        string executableDirectory = Path.Join(_root, "app");
        Directory.CreateDirectory(executableDirectory);

        Assert.Null(ElevatorLoader.ResolveBundledElevatorPath(executableDirectory));
    }

    [Fact]
    public async Task UsesTheBundledElevatorWhenPresent()
    {
        string executableDirectory = Path.Join(_root, "app");
        string expected = CreateElevator(executableDirectory);

        Assert.Equal(expected, await ElevatorLoader.ResolveWindowsElevatorPathAsync(executableDirectory, forceUserGSudo: false, useSystemGSudo: false));
    }

    [Fact]
    public async Task OnlyUsesGSudoFromPathWhenForcedByTheUser()
    {
        if (!OperatingSystem.IsWindows()) return;
        string executableDirectory = Path.Join(_root, "app");
        Directory.CreateDirectory(executableDirectory);
        string gsudoDirectory = Path.Join(_root, "gsudo");
        Directory.CreateDirectory(gsudoDirectory);
        File.WriteAllText(Path.Join(gsudoDirectory, "gsudo.exe"), "");
        string? oldPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);

        try
        {
            Environment.SetEnvironmentVariable("PATH", gsudoDirectory + Path.PathSeparator + oldPath, EnvironmentVariableTarget.Process);

            Assert.Equal(string.Empty, await ElevatorLoader.ResolveWindowsElevatorPathAsync(executableDirectory, forceUserGSudo: false, useSystemGSudo: false));
            Assert.EndsWith("gsudo.exe", await ElevatorLoader.ResolveWindowsElevatorPathAsync(executableDirectory, forceUserGSudo: true, useSystemGSudo: false), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath, EnvironmentVariableTarget.Process);
        }
    }
}
