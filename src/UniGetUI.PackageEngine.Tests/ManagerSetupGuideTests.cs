using System.Reflection;
using System.Text.RegularExpressions;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Managers.AptManager;
using UniGetUI.PackageEngine.Managers.BunManager;
using UniGetUI.PackageEngine.Managers.CargoManager;
using UniGetUI.PackageEngine.Managers.DnfManager;
using UniGetUI.PackageEngine.Managers.DotNetManager;
using UniGetUI.PackageEngine.Managers.FlatpakManager;
using UniGetUI.PackageEngine.Managers.HomebrewManager;
using UniGetUI.PackageEngine.Managers.MacPkgManager;
using UniGetUI.PackageEngine.Managers.NpmManager;
using UniGetUI.PackageEngine.Managers.PacmanManager;
using UniGetUI.PackageEngine.Managers.PipManager;
using UniGetUI.PackageEngine.Managers.SnapManager;
using UniGetUI.PackageEngine.Managers.VcpkgManager;
#if WINDOWS
using UniGetUI.PackageEngine.Managers.ChocolateyManager;
using UniGetUI.PackageEngine.Managers.PowerShell7Manager;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.Managers.ScoopManager;
using UniGetUI.PackageEngine.Managers.WingetManager;
using UniGetUI.PackageEngine.Managers.WinInstallerManager;
#endif

namespace UniGetUI.PackageEngine.Tests;

public class ManagerSetupGuideTests
{
    private const string GuideFileName = "PACKAGE_MANAGERS.md";

    private static readonly Lazy<PackageManager[]> Managers = new(() =>
    [
        new Npm(), new Bun(), new Pip(), new Cargo(), new Vcpkg(), new DotNet(),
        new Homebrew(), new MacPkg(), new Apt(), new Dnf(), new Pacman(), new Snap(), new Flatpak(),
#if WINDOWS
        new WinGet(), new Scoop(), new Chocolatey(), new WinInstaller(), new PowerShell(), new PowerShell7(),
#endif
    ]);

    public static TheoryData<string> ManagerNames()
    {
        TheoryData<string> data = [];
        foreach (PackageManager manager in Managers.Value)
            data.Add(manager.Name);
        return data;
    }

    private static PackageManager Manager(string name) => Managers.Value.Single(m => m.Name == name);

    private static string DocsDirectory()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Join(dir.FullName, "docs");
            if (File.Exists(Path.Join(candidate, GuideFileName)))
                return candidate;
        }
        throw new DirectoryNotFoundException("docs/" + GuideFileName);
    }

    private static HashSet<string> HeadingSlugs(string file)
    {
        HashSet<string> slugs = [];
        bool inFence = false;
        foreach (string line in File.ReadLines(file))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            Match heading = Regex.Match(line, @"^#{1,6} (.+)$");
            if (inFence || !heading.Success)
                continue;
            string kept = Regex.Replace(heading.Groups[1].Value.Trim().ToLowerInvariant(), @"[^\p{L}\p{Nd}\s_-]", "");
            slugs.Add(Regex.Replace(kept, @"\s", "-"));
        }
        return slugs;
    }

    [Fact]
    public void TheListCoversEveryManagerInTheManagerAssemblies()
    {
        HashSet<Type> found = [];
        foreach (string dll in Directory.GetFiles(AppContext.BaseDirectory, "UniGetUI.PackageEngine.Managers.*.dll"))
        {
            Type?[] types;
            try
            {
                types = Assembly.LoadFrom(dll).GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }
            foreach (Type? type in types)
            {
                if (type is { IsAbstract: false } && type.IsSubclassOf(typeof(PackageManager)))
                    found.Add(type);
            }
        }

        Assert.NotEmpty(found);
        Assert.Equal(
            found.Select(t => t.FullName).Order(),
            Managers.Value.Select(m => m.GetType().FullName).Order());
    }

    [Theory]
    [MemberData(nameof(ManagerNames))]
    public void EveryManagerLinksToAnExistingSectionOfTheDocs(string name)
    {
        string guide = Manager(name).Properties.SetupGuide;
        Assert.False(string.IsNullOrEmpty(guide), $"{name} declares no SetupGuide");

        string[] parts = guide.Split('#', 2);
        string file = Path.Join(DocsDirectory(), parts[0]);
        Assert.True(File.Exists(file), $"{name}: docs/{parts[0]} does not exist");
        if (parts.Length == 2)
            Assert.True(HeadingSlugs(file).Contains(parts[1]), $"{name}: no heading in docs/{parts[0]} produces #{parts[1]}");
    }

    [Theory]
    [MemberData(nameof(ManagerNames))]
    public void EveryInstallCommandIsTheOneTheGuideShows(string name)
    {
        string guide = File.ReadAllText(Path.Join(DocsDirectory(), GuideFileName));
        ManagerInstallCommands commands = Manager(name).Properties.InstallCommands;
        foreach (string? command in new[] { commands.Windows, commands.MacOS, commands.Linux })
        {
            if (command is not null)
                Assert.Contains(command, guide);
        }
    }

    [Theory]
    [InlineData("Homebrew")]
    [InlineData("Pkg")]
    [InlineData("Apt")]
    [InlineData("Dnf")]
    [InlineData("Pacman")]
    [InlineData("Snap")]
    [InlineData("Flatpak")]
    public void ManagersThatDoNotRunOnWindowsOfferNoWindowsCommand(string name)
    {
        Assert.Null(Manager(name).Properties.InstallCommands.Windows);
    }

#if WINDOWS
    [Theory]
    [InlineData("Winget")]
    [InlineData("Scoop")]
    [InlineData("Chocolatey")]
    [InlineData("Installer")]
    [InlineData("PowerShell")]
    public void WindowsOnlyManagersOfferNoMacOrLinuxCommand(string name)
    {
        ManagerInstallCommands commands = Manager(name).Properties.InstallCommands;
        Assert.Null(commands.MacOS);
        Assert.Null(commands.Linux);
    }

    [Fact]
    public void ScoopOffersNoCommandBecauseItsInstallerMustRunInAnUnelevatedPowerShell()
    {
        Assert.Equal(default, Manager("Scoop").Properties.InstallCommands);
    }
#endif

    [Fact]
    public void TheHeadingScanIgnoresCommentsInsideCodeBlocks()
    {
        string file = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(file, "## Real heading\n\n```shell\n# winget\n```\n");
        try
        {
            HashSet<string> slugs = HeadingSlugs(file);
            Assert.Contains("real-heading", slugs);
            Assert.DoesNotContain("winget", slugs);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
