using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

namespace UniGetUI.PackageEngine.Tests;

/// <summary>
/// A package identifier is concatenated onto the command line by every manager, so one that looks
/// like an option is smuggled in as one even where no shell is involved. Escaping does not help:
/// a quoted argument still binds as an option when it starts with a dash.
/// </summary>
public sealed class OptionInjectionTests
{
    private static TestPackageManager DirectExecManager() =>
        new PackageManagerBuilder()
            .WithName("DirectManager")
            .ConfigureOperation(helper =>
                helper.ParametersFactory = (package, options, _) =>
                    options.Version.Length > 0
                        ? ["install", package.Id, "--version", options.Version]
                        : ["install", package.Id]
            )
            .Build();

    [Theory]
    [InlineData("requests --index-url https://evil.example")]
    [InlineData("--index-url")]
    [InlineData("-Name")]
    [InlineData("/force")]
    [InlineData("pkg --allow-downgrade")]
    [InlineData("pkg\tsecond")]
    public void ADirectExecManagerRefusesAnIdentifierThatWouldBecomeAnOption(string identifier)
    {
        var manager = DirectExecManager();
        var package = new PackageBuilder().WithManager(manager).WithId(identifier).Build();

        Assert.Throws<InvalidOperationException>(
            () =>
                manager.OperationHelper.GetParameters(
                    package,
                    new InstallOptions(),
                    OperationType.Install
                )
        );
    }

    [Theory]
    [InlineData("-x")]
    [InlineData("/x")]
    public void ADirectExecManagerRefusesAVersionThatWouldBecomeAnOption(string version)
    {
        var manager = DirectExecManager();
        var package = new PackageBuilder().WithManager(manager).WithId("Contoso.Test").Build();
        var options = new InstallOptions { Version = version };

        Assert.Throws<InvalidOperationException>(
            () => manager.OperationHelper.GetParameters(package, options, OperationType.Install)
        );
    }

    [Theory]
    [InlineData("Contoso.Test")]
    [InlineData("{e46eca4f-393b-40df-9f49-076faf788d83}")]
    [InlineData(@"MSIX\Contoso.App_1.0_x64__8wekyb3d8bbwe")]
    [InlineData("@babel/core")]
    [InlineData("g++")]
    [InlineData("zlib:x64-windows")]
    public void RealIdentifiersAreStillAccepted(string identifier)
    {
        var manager = DirectExecManager();
        var package = new PackageBuilder().WithManager(manager).WithId(identifier).Build();

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions(),
            OperationType.Install
        );

        Assert.Contains(identifier, parameters);
    }

    [Fact]
    public void AVersionWithSpacesIsStillAcceptedForADirectExecManager()
    {
        var manager = DirectExecManager();
        var package = new PackageBuilder().WithManager(manager).WithId("Contoso.Test").Build();
        var options = new InstallOptions { Version = "2021 Update" };

        var parameters = manager.OperationHelper.GetParameters(
            package,
            options,
            OperationType.Install
        );

        Assert.Contains("2021 Update", parameters);
    }

    [Fact]
    public void TheDetailLookupsRefuseAnIdentifierThatWouldBecomeAnOption()
    {
        var manager = DirectExecManager();
        bool reached = false;
        manager.TestDetailsHelper.VersionsFactory = _ =>
        {
            reached = true;
            return ["1.0.0"];
        };
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("requests --index-url https://evil.example")
            .Build();

        Assert.Empty(manager.DetailsHelper.GetVersions(package));
        Assert.False(reached);
    }

    // Real identifiers taken from `winget list` on a Windows machine. WinGet quotes the
    // identifier, so these must keep working; 3 of 157 installed packages looked like this.
    [Theory]
    [InlineData(@"ARP\Machine\X86\Microsoft Copilot")]
    [InlineData(@"ARP\Machine\X86\DevExpress Components 24.2")]
    [InlineData(@"ARP\Machine\Arm64\Sophos Endpoint Agent")]
    public void AQuotingManagerKeepsIdentifiersContainingSpaces(string identifier)
    {
        Assert.True(CoreTools.IsOptionSafeIdentifier(identifier, quotedByTheSink: true));
        Assert.False(CoreTools.IsOptionSafeIdentifier(identifier, quotedByTheSink: false));
    }

    [Theory]
    [InlineData(@"ARP\Machine\X86\Microsoft Copilot --index-url https://evil.example")]
    [InlineData("-Name")]
    public void AQuotingManagerStillRefusesOptionTokens(string identifier)
    {
        // Whitespace is allowed for a quoting manager, so a leading option marker is what has to
        // be refused; the whole value reaches WinGet as one quoted argument.
        Assert.Equal(
            !identifier.StartsWith('-'),
            CoreTools.IsOptionSafeIdentifier(identifier, quotedByTheSink: true)
        );
    }

#if WINDOWS
    // The wiring, not just the predicate: WinGet is the only manager that quotes, and its
    // Add/Remove-programs identifiers are the ones that contain spaces.
    [Fact]
    public void OnlyWinGetDeclaresThatItQuotesIdentifiers()
    {
        Assert.True(
            new UniGetUI.PackageEngine.Managers.WingetManager.WinGet()
                .IdentifiersAreQuotedOnCommandLine
        );
        Assert.False(new TestPackageManager().IdentifiersAreQuotedOnCommandLine);
    }

    [Fact]
    public void WinGetRefusesAnEllipsizedIdWhoseNameWouldBecomeAnOption()
    {
        var manager = new UniGetUI.PackageEngine.Managers.WingetManager.WinGet();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("safe…")
            .WithName("--index-url")
            .Build();

        Assert.Throws<InvalidOperationException>(
            () =>
                manager.OperationHelper.GetParameters(
                    package,
                    new InstallOptions(),
                    OperationType.Install
                )
        );
    }

    [Fact]
    public void WinGetStillSelectsAnEllipsizedPackageByItsOrdinaryName()
    {
        var manager = new UniGetUI.PackageEngine.Managers.WingetManager.WinGet();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Microsoft.VisualStu…")
            .WithName("Microsoft Visual Studio Code")
            .Build();

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions(),
            OperationType.Install
        );

        Assert.Contains(
            "--name \"Microsoft Visual Studio Code\" --exact",
            string.Join(" ", parameters)
        );
    }

    [Fact]
    public void WinGetAcceptsAnAddRemoveProgramsIdentifierWithSpaces()
    {
        var manager = new UniGetUI.PackageEngine.Managers.WingetManager.WinGet();

        Assert.True(
            CoreTools.IsOptionSafeIdentifier(
                @"ARP\Machine\X86\Microsoft Copilot",
                manager.IdentifiersAreQuotedOnCommandLine
            )
        );
    }
#endif

    [Theory]
    [InlineData("Contoso.Test", true)]
    [InlineData(@"MSIX\App_1.0_x64__8wekyb3d8bbwe", true)]
    [InlineData("{e46eca4f-393b}", true)]
    [InlineData("-Name", false)]
    [InlineData("/force", false)]
    [InlineData("has space", false)]
    [InlineData("", false)]
    public void IsOptionSafeIdentifier_ClassifiesCorrectly(string identifier, bool expected)
    {
        Assert.Equal(expected, CoreTools.IsOptionSafeIdentifier(identifier));
    }

    // The exported installation script hands its command strings to cmd.exe, which expands
    // %NAME% before it parses them, inside double quotes included.
    [Theory]
    [InlineData("chrome.exe", true)]
    [InlineData("My App.exe", true)]
    [InlineData("Café.exe", true)]
    [InlineData("foo-bar (1).exe", true)]
    [InlineData("%FOO%.exe", false)]
    [InlineData("!FOO!.exe", false)]
    [InlineData("a\"b.exe", false)]
    [InlineData("a&b.exe", false)]
    [InlineData("a|b.exe", false)]
    [InlineData("a>b.exe", false)]
    [InlineData("a<b.exe", false)]
    [InlineData("a^b.exe", false)]
    [InlineData("-x.exe", false)]
    [InlineData("/x.exe", false)]
    [InlineData("ab.exe", false)]
    [InlineData("", false)]
    public void IsSafeProcessImageName_RefusesWhatCmdWouldReinterpret(string name, bool expected)
    {
        Assert.Equal(expected, CoreTools.IsSafeProcessImageName(name));
    }

    [Fact]
    public void IsSafeProcessImageName_RefusesTheQuoteAndSeparatorBreakout()
    {
        Assert.False(
            CoreTools.IsSafeProcessImageName("nonexistent.exe\" /f & echo INJECTED & rem ")
        );
    }

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("2021 Update", true)]
    [InlineData("", true)]
    [InlineData("-x", false)]
    [InlineData("/x", false)]
    public void IsOptionSafeValue_AllowsSpacesButNotOptions(string value, bool expected)
    {
        Assert.Equal(expected, CoreTools.IsOptionSafeValue(value));
    }
}
