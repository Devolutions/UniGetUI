using UniGetUI.Shared;

namespace UniGetUI.Tests;

public sealed class WingetUIShortcutMigrationTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(WingetUIShortcutMigrationTests),
        Guid.NewGuid().ToString("N")
    );

    private readonly string _desktop;
    private readonly string _startMenu;
    private readonly string _programs;

    public WingetUIShortcutMigrationTests()
    {
        _desktop = Path.Combine(_testRoot, "Desktop");
        _startMenu = Path.Combine(_testRoot, "Start Menu");
        _programs = Path.Combine(_startMenu, "Programs");
        Directory.CreateDirectory(_desktop);
        Directory.CreateDirectory(_programs);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private int Migrate() =>
        SharedPreUiCommandDispatcher.MigrateWingetUIShortcuts(
            [(_desktop, _desktop), (_startMenu, _programs)]
        );

    [Fact]
    public void StartMenuShortcutMovesIntoPrograms()
    {
        File.WriteAllText(Path.Combine(_startMenu, "WingetUI.lnk"), "legacy");

        Assert.Equal(0, Migrate());

        Assert.False(File.Exists(Path.Combine(_startMenu, "WingetUI.lnk")));
        Assert.False(File.Exists(Path.Combine(_startMenu, "UniGetUI.lnk")));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(_programs, "UniGetUI.lnk")));
    }

    [Fact]
    public void StartMenuShortcutIsDroppedWhenProgramsAlreadyHasOne()
    {
        File.WriteAllText(Path.Combine(_startMenu, "UniGetUI (formerly WingetUI).lnk"), "legacy");
        File.WriteAllText(Path.Combine(_programs, "UniGetUI.lnk"), "current");

        Assert.Equal(0, Migrate());

        Assert.False(File.Exists(Path.Combine(_startMenu, "UniGetUI (formerly WingetUI).lnk")));
        Assert.False(File.Exists(Path.Combine(_startMenu, "UniGetUI.lnk")));
        Assert.Equal("current", File.ReadAllText(Path.Combine(_programs, "UniGetUI.lnk")));
    }

    [Fact]
    public void DesktopShortcutIsRenamedInPlace()
    {
        File.WriteAllText(Path.Combine(_desktop, "WingetUI .lnk"), "legacy");

        Assert.Equal(0, Migrate());

        Assert.False(File.Exists(Path.Combine(_desktop, "WingetUI .lnk")));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(_desktop, "UniGetUI.lnk")));
    }
}
