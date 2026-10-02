using UniGetUI.Tui.FakeData;
using UniGetUI.Tui.Infrastructure;

namespace UniGetUI.Tui.Tests;

public class TuiCommandLineTests
{
    [Fact]
    public void ParsesFakeDataPageAndUpdateApps()
    {
        var cl = TuiCommandLine.Parse(["--fake-data", "--page", "Updates", "--updateapps"]);
        Assert.True(cl.FakeData);
        Assert.Equal("updates", cl.StartupPage);
        Assert.True(cl.UpdateAppsOnStart);
        Assert.Empty(cl.BundleFiles);
    }

    [Fact]
    public void FakeDataDirImpliesFakeData()
    {
        var cl = TuiCommandLine.Parse(["--fake-data-dir", "some dir"]);
        Assert.True(cl.FakeData);
        Assert.Equal("some dir", cl.FakeDataDirectory);
    }

    [Fact]
    public void RejectsUnknownPage()
        => Assert.Throws<ArgumentException>(() => TuiCommandLine.Parse(["--page", "nope"]));

    [Fact]
    public void ParsesThemeByIdOrName()
    {
        Assert.Equal("dracula", TuiCommandLine.Parse(["--theme", "dracula"]).Theme?.Id);
        Assert.Equal("devolutions-dark-blue", TuiCommandLine.Parse(["--theme", "Devolutions - Dark Blue"]).Theme?.Id);
        Assert.Null(TuiCommandLine.Parse(["--fake-data"]).Theme);
    }

    [Fact]
    public void RejectsUnknownThemeAndListsTheValidOnes()
    {
        var ex = Assert.Throws<ArgumentException>(() => TuiCommandLine.Parse(["--theme", "neon"]));
        Assert.Contains("devolutions-graphite", ex.Message);
        Assert.Throws<ArgumentException>(() => TuiCommandLine.Parse(["--theme"]));
    }

    [Fact]
    public void RecognisesAnExistingBundleFile()
    {
        string file = Path.Join(Path.GetTempPath(), $"tui-cl-{Guid.NewGuid():N}.ubundle");
        File.WriteAllText(file, "{}");
        try
        {
            var cl = TuiCommandLine.Parse([file]);
            Assert.Equal([Path.GetFullPath(file)], cl.BundleFiles);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void SettingsCommandValuesAreNotBundles()
    {
        // "settings.json" must be the value of --export-settings, not a (missing) bundle file.
        var cl = TuiCommandLine.Parse(["--export-settings", "settings.json"]);
        Assert.Empty(cl.BundleFiles);
    }

    [Fact]
    public void HelpIsRecognised()
    {
        Assert.True(TuiCommandLine.Parse(["--help"]).ShowHelp);
        Assert.Contains("--fake-data", TuiCommandLine.HelpText);
    }
}

public class FakePackageManagerProcessTests : IDisposable
{
    private readonly string _state = Path.Join(Path.GetTempPath(), $"tui-fake-state-{Guid.NewGuid():N}.json");

    public FakePackageManagerProcessTests()
    {
        Environment.SetEnvironmentVariable(FakePackageManagerProcess.StepDelayVariable, "0");
        FakeStateStore.EnsureSeeded(_state);
    }

    public void Dispose()
    {
        if (File.Exists(_state)) File.Delete(_state);
    }

    private int Run(params string[] args)
    {
        Assert.True(FakePackageManagerProcess.TryRun([FakePackageManagerProcess.PmFlag, "--state", _state, .. args], out int code));
        return code;
    }

    private string? Version(string manager, string id)
        => FakeStateStore.Read(_state).Installed.FirstOrDefault(p => p.Manager == manager && p.Id == id)?.Version;

    [Fact]
    public void InstallUpdateUninstall_MutateOnlyTheStateFile()
    {
        Assert.Null(Version("Winget", "Contoso.PhotoStudio"));
        Assert.Equal(0, Run("Winget", "install", "--id", "Contoso.PhotoStudio", "--version", "2024.2"));
        Assert.Equal("2024.2", Version("Winget", "Contoso.PhotoStudio"));
        Assert.Equal(0, Run("Winget", "update", "--id", "Contoso.PhotoStudio"));
        Assert.Equal("2025.1", Version("Winget", "Contoso.PhotoStudio"));
        Assert.Equal(0, Run("Winget", "uninstall", "--id", "Contoso.PhotoStudio"));
        Assert.Null(Version("Winget", "Contoso.PhotoStudio"));
    }

    [Fact]
    public void FailingPackagesFail()
    {
        Assert.NotEqual(0, Run("Winget", "update", "--id", "Fabrikam.FailingUpdater"));
        Assert.Equal("1.0.0", Version("Winget", "Fabrikam.FailingUpdater"));
    }

    [Fact]
    public void UnknownPackageAndVersionFail()
    {
        Assert.NotEqual(0, Run("Winget", "install", "--id", "Does.Not.Exist"));
        Assert.NotEqual(0, Run("Winget", "install", "--id", "Contoso.Editor", "--version", "99.0"));
        Assert.NotEqual(0, Run("Winget", "uninstall", "--id", "Contoso.PhotoStudio"));
    }

    [Fact]
    public void SourcesCanBeAddedAndRemoved()
    {
        Assert.Equal(0, Run("Scoop", "source-add", "--name", "versions", "--url", "https://fake.unigetui.invalid/scoop/versions"));
        Assert.Contains(FakeStateStore.Read(_state).Sources, s => s.Manager == "Scoop" && s.Name == "versions");
        Assert.NotEqual(0, Run("Scoop", "source-add", "--name", "versions", "--url", "https://x.invalid"));
        Assert.Equal(0, Run("Scoop", "source-remove", "--name", "versions"));
        Assert.DoesNotContain(FakeStateStore.Read(_state).Sources, s => s.Name == "versions");
    }

    [Fact]
    public void FakeElevatorDelegatesWithoutElevating()
    {
        Assert.True(FakePackageManagerProcess.TryRun(
            [FakePackageManagerProcess.ElevateFlag, "UniGetUI.Tui.exe", FakePackageManagerProcess.PmFlag, "--state", _state, "Npm", "install", "--id", "tailspin-bundler"],
            out int code));
        Assert.Equal(0, code);
        Assert.Equal("5.4.10", Version("Npm", "tailspin-bundler"));
    }

    [Fact]
    public void OrdinaryArgumentsAreNotHandled()
        => Assert.False(FakePackageManagerProcess.TryRun(["--page", "updates"], out _));
}

public class FakeCatalogTests
{
    [Fact]
    public void EverySeededPackageExistsInTheCatalog()
    {
        foreach (var (manager, id, version, _) in FakeCatalog.InitiallyInstalled)
        {
            var package = FakeCatalog.Find(manager, id);
            Assert.NotNull(package);
            Assert.Contains(version, package!.Versions);
        }
    }

    [Fact]
    public void CatalogUsesOnlyReservedDomains()
    {
        Assert.All(FakeCatalog.Packages, p => Assert.EndsWith(".invalid", new Uri(p.Homepage).Host));
        Assert.All(FakeCatalog.DefaultSources, s => Assert.EndsWith(".invalid", new Uri(s.Url).Host));
    }
}
