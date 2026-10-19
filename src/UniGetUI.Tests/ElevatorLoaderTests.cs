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
}
