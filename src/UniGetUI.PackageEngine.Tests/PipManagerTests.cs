using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.Core.Tools.Tests;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.PipManager;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;
using UniGetUI.PackageEngine.Tests.Infrastructure.Assertions;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

[CollectionDefinition("Pip manager tests", DisableParallelization = true)]
public sealed class PipManagerTestCollection
{
    public const string Name = "Pip manager tests";
}

[Collection(PipManagerTestCollection.Name)]
public sealed class PipManagerTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        nameof(PipManagerTests),
        Guid.NewGuid().ToString("N")
    );

    public PipManagerTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        string secureSettingsRoot = Path.Combine(_testRoot, "SecureSettings");
        Directory.CreateDirectory(secureSettingsRoot);
        SecureSettings.TEST_SecureSettingsRootOverride = secureSettingsRoot;
        AllowCustomManagerPaths(false);
        Settings.ResetSettings();
        Settings.Set(Settings.K.EnableProxy, false);
        Settings.Set(Settings.K.EnableProxyAuth, false);
        Settings.SetValue(Settings.K.ProxyURL, "");
    }

    public void Dispose()
    {
        AllowCustomManagerPaths(false);
        SecureSettings.TEST_SecureSettingsRootOverride = null;
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [Fact]
    public void InterpretersAreClassifiedByTheEnvironmentTheyBelongTo()
    {
        string venvPython = CreateFile("app", "venv", "Scripts", "python.exe");
        CreateFile("app", "venv", "pyvenv.cfg");
        string flatVenvPython = CreateFile("flat", "python.exe");
        CreateFile("flat", "pyvenv.cfg");
        string condaEnvPython = CreateFile("miniconda3", "envs", "tools", "python.exe");
        Directory.CreateDirectory(Path.Combine(_testRoot, "miniconda3", "envs", "tools", "conda-meta"));
        string condaUnixEnvPython = CreateFile("miniforge", "envs", "ml", "bin", "python");
        Directory.CreateDirectory(Path.Combine(_testRoot, "miniforge", "envs", "ml", "conda-meta"));
        string condaBasePython = CreateFile("miniconda3", "python.exe");
        Directory.CreateDirectory(Path.Combine(_testRoot, "miniconda3", "conda-meta"));
        string embeddedPython = CreateFile("someapp", "python", "python.exe");
        CreateFile("someapp", "python", "python313._pth");
        string systemPython = CreateFile("Python314", "python.exe");

        Assert.Equal(PythonEnvironmentKind.VirtualEnvironment, Pip.ClassifyEnvironment(venvPython));
        Assert.Equal(PythonEnvironmentKind.VirtualEnvironment, Pip.ClassifyEnvironment(flatVenvPython));
        Assert.Equal(PythonEnvironmentKind.CondaEnvironment, Pip.ClassifyEnvironment(condaEnvPython));
        Assert.Equal(PythonEnvironmentKind.CondaEnvironment, Pip.ClassifyEnvironment(condaUnixEnvPython));
        Assert.Equal(PythonEnvironmentKind.System, Pip.ClassifyEnvironment(condaBasePython));
        Assert.Equal(PythonEnvironmentKind.Embedded, Pip.ClassifyEnvironment(embeddedPython));
        Assert.Equal(PythonEnvironmentKind.System, Pip.ClassifyEnvironment(systemPython));
        Assert.True(Pip.IsEnvironmentManaged(systemPython));
        Assert.False(Pip.IsEnvironmentManaged(venvPython));
        Assert.False(Pip.IsEnvironmentManaged(condaEnvPython));
        Assert.False(Pip.IsEnvironmentManaged(embeddedPython));
    }

    [Fact]
    public void EnvironmentChoicesOverrideTheDefaultAndReorderTheCandidates()
    {
        string venvPython = CreateFile("app", "venv", "Scripts", "python.exe");
        CreateFile("app", "venv", "pyvenv.cfg");
        string systemPython = CreateFile("Python314", "python.exe");

        Pip.SetEnvironmentManaged(venvPython, true);
        Assert.Null(Pip.GetEnvironmentChoice(venvPython));

        AllowCustomManagerPaths(true);
        Pip.SetEnvironmentManaged(venvPython, true);
        Pip.SetEnvironmentManaged(systemPython, false);

        Assert.True(Pip.GetEnvironmentChoice(venvPython));
        Assert.False(Pip.GetEnvironmentChoice(systemPython));
        Assert.Equal([venvPython, systemPython], Pip.OrderCandidates([systemPython, venvPython], []));

        Pip.SetEnvironmentManaged(venvPython, false);
        Pip.SetEnvironmentManaged(systemPython, true);

        Assert.False(Pip.GetEnvironmentChoice(venvPython));
        Assert.Null(Pip.GetEnvironmentChoice(systemPython));
        Assert.Equal([systemPython, venvPython], Pip.OrderCandidates([venvPython, systemPython], []));
    }

    [Fact]
    public void AnOptInStopsCountingOnceCustomPathsAreDisallowedAgain()
    {
        string venvPython = CreateFile("app", "venv", "Scripts", "python.exe");
        CreateFile("app", "venv", "pyvenv.cfg");

        AllowCustomManagerPaths(true);
        Pip.SetEnvironmentManaged(venvPython, true);
        Assert.True(Pip.IsEnvironmentManaged(venvPython));

        AllowCustomManagerPaths(false);
        Assert.False(Pip.IsEnvironmentManaged(venvPython));
    }

    [Fact]
    public void AnInterpreterThatCannotBeInspectedIsNotManaged()
    {
        Assert.Equal(PythonEnvironmentKind.Unknown, Pip.ClassifyEnvironment("bad\0path"));
        Assert.False(Pip.IsEnvironmentManaged("bad\0path"));
    }

    [Fact]
    public void AutomaticSelectionSkipsUnmanagedEnvironments()
    {
        string venvPython = CreateFile("app", "venv", "Scripts", "python.exe");
        CreateFile("app", "venv", "pyvenv.cfg");
        string systemPython = CreateFile("Python314", "python.exe");

        Assert.Equal(new(false, ""), new ScriptedPip([venvPython]).GetExecutableFile());
        Assert.Equal(new(true, systemPython), new ScriptedPip([venvPython, systemPython]).GetExecutableFile());
    }

    [Fact]
    public void ASelectedExecutableIsHonouredUnlessItsEnvironmentIsIgnored()
    {
        string venvPython = CreateFile("app", "venv", "Scripts", "python.exe");
        CreateFile("app", "venv", "pyvenv.cfg");
        string firstSystem = CreateFile("Python314", "python.exe");
        string secondSystem = CreateFile("Python313", "python.exe");
        AllowCustomManagerPaths(true);
        var pip = new ScriptedPip([firstSystem, secondSystem, venvPython]);

        Settings.SetDictionaryItem(Settings.K.ManagerPaths, pip.Name, venvPython);
        Assert.Equal(new(true, venvPython), pip.GetExecutableFile());

        Settings.SetDictionaryItem(Settings.K.ManagerPaths, pip.Name, secondSystem);
        Pip.SetEnvironmentManaged(secondSystem, false);
        Assert.Equal(new(true, firstSystem), pip.GetExecutableFile());
    }

    [Fact]
    public void APickedExecutableIsRecognisedThroughAnyInterpreterInItsEnvironment()
    {
        string venvPython = CreateFile("app", "venv", "bin", "python");
        string venvPython3 = CreateFile("app", "venv", "bin", "python3");
        CreateFile("app", "venv", "pyvenv.cfg");
        string systemPython3 = CreateFile("usr", "bin", "python3");
        AllowCustomManagerPaths(true);
        var pip = new ScriptedPip([systemPython3, venvPython3]);
        Settings.SetDictionaryItem(Settings.K.ManagerPaths, pip.Name, venvPython);

        Assert.True(pip.IsChosenExecutable(venvPython3));
        Assert.False(pip.IsChosenExecutable(systemPython3));

        Pip.SetEnvironmentManaged(venvPython3, false);

        Assert.False(pip.IsChosenExecutable(venvPython3));
        Assert.Equal(new(true, systemPython3), pip.GetExecutableFile());
    }

    [Fact]
    public void AnEnvironmentIsRepresentedByTheFirstInterpreterFoundInIt()
    {
        string python3 = CreateFile("usr", "bin", "python3");
        string python = CreateFile("usr", "bin", "python");

        Assert.Equal([python3], Pip.OrderCandidates([python3, python], []));
    }

    [Fact]
    public void AVenvOnPathDoesNotHideTheSystemPythonAndEachEnvironmentIsListedOnce()
    {
        string venvPython = CreateFile("app", "venv", "bin", "python");
        string venvPython3 = CreateFile("app", "venv", "bin", "python3");
        CreateFile("app", "venv", "pyvenv.cfg");
        string systemPython3 = CreateFile("usr", "bin", "python3");

        Assert.Equal(
            [systemPython3, venvPython],
            Pip.OrderCandidates([venvPython, venvPython3, systemPython3], [])
        );
    }

    [Fact]
    public void UncheckingThePickedExecutableExcludesIt()
    {
        string venvPython = CreateFile("app", "venv", "Scripts", "python.exe");
        CreateFile("app", "venv", "pyvenv.cfg");
        string systemPython = CreateFile("Python314", "python.exe");
        AllowCustomManagerPaths(true);
        var pip = new ScriptedPip([systemPython, venvPython]);
        Settings.SetDictionaryItem(Settings.K.ManagerPaths, pip.Name, venvPython);

        Assert.True(pip.IsChosenExecutable(venvPython));
        Assert.Equal(new(true, venvPython), pip.GetExecutableFile());

        Pip.SetEnvironmentManaged(venvPython, false);

        Assert.False(pip.IsChosenExecutable(venvPython));
        Assert.Equal(new(true, systemPython), pip.GetExecutableFile());
    }

    [Fact]
    public void InstallLocationComesFromTheSitePackagesPipReports()
    {
        string sitePackages = Path.Combine("app", "venv", "Lib", "site-packages");
        string banner = $"pip 26.0 from {Path.Combine(sitePackages, "pip")} (python 3.13)";
        string python = Path.Combine("app", "venv", "Scripts", "python.exe");

        Assert.Equal(
            Path.Combine(sitePackages, "requests"),
            Pip.ResolveInstallLocation(banner, python, "requests", d => d == Path.Combine(sitePackages, "requests"))
        );
        Assert.Equal(
            Path.Combine(sitePackages, "typing_extensions"),
            Pip.ResolveInstallLocation(banner, python, "typing-extensions", d => d == Path.Combine(sitePackages, "typing_extensions"))
        );
        Assert.Equal(sitePackages, Pip.ResolveInstallLocation(banner, python, "PyYAML", _ => false));
        Assert.Equal(
            Path.Join(Path.Combine("app", "venv", "Scripts"), "Lib", "site-packages"),
            Pip.ResolveInstallLocation("", python, "PyYAML", _ => false)
        );
    }

    [Fact]
    public void InterpretersInTheSameDirectoryShareAnEnvironment()
    {
        string python = CreateFile("env", "bin", "python3");
        string pip = CreateFile("env", "bin", "pip3");
        string other = CreateFile("other", "bin", "python3");

        Assert.True(Pip.IsSameEnvironment(pip, python));
        Assert.False(Pip.IsSameEnvironment(other, python));
        Assert.False(Pip.IsSameEnvironment("", python));
    }

    [Fact]
    public void OnlyTheStoreInstallerAliasIsTreatedAsAStub()
    {
        string stub = CreateFile("stub", "WindowsApps", "python.exe");
        string realStore = CreateFile("store", "WindowsApps", "python.exe");
        Directory.CreateDirectory(
            Path.Combine(_testRoot, "store", "WindowsApps", "PythonSoftwareFoundation.Python.3.12_qbz5n2kfra8p0")
        );

        Assert.True(Pip.IsStoreInstallerStub(stub));
        Assert.False(Pip.IsStoreInstallerStub(realStore));
    }

    private void AllowCustomManagerPaths(bool allowed) =>
        SecureSettings.ApplyForUser(
            Environment.UserName,
            SecureSettings.ResolveKey(SecureSettings.K.AllowCustomManagerPaths),
            allowed
        );

    private sealed class ScriptedPip(IReadOnlyList<string> candidates) : Pip
    {
        public override IReadOnlyList<string> FindCandidateExecutableFiles() => candidates;
    }

    [Theory]
    [InlineData(
        @"pip 26.2.1 from C:\Python313\Lib\site-packages\pip (python 3.13)",
        @"C:\Python313\Lib\site-packages"
    )]
    [InlineData(
        "pip 24.0 from /home/me/app/venv/lib/python3.12/site-packages/pip (python 3.12)",
        "/home/me/app/venv/lib/python3.12/site-packages"
    )]
    [InlineData(
        @"pip 25.1 from C:\Program Files\My Python\Lib\site-packages\pip (python 3.14)",
        @"C:\Program Files\My Python\Lib\site-packages"
    )]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseSitePackagesDirectoryReadsThePipVersionBanner(string? banner, string? expected)
    {
        Assert.Equal(expected, Pip.ParseSitePackagesDirectory(banner));
    }

    [Fact]
    public void CandidateInterpretersKeepVirtualEnvironmentsBehindSystemPythons()
    {
        string venvPython = CreateFile("app", "venv", "Scripts", "python.exe");
        CreateFile("app", "venv", "pyvenv.cfg");
        string firstSystem = CreateFile("Python314", "python.exe");
        string secondSystem = CreateFile("Python313", "python.exe");

        Assert.Equal(
            [firstSystem, secondSystem, venvPython],
            Pip.OrderCandidates([venvPython, firstSystem], [secondSystem])
        );
    }

    [Fact]
    public void InstalledPythonsOffPathAreFoundNewestFirst()
    {
        string programFiles = Path.Combine(_testRoot, "Program Files");
        string userInstalls = Path.Combine(_testRoot, "Local", "Programs", "Python");
        string installManager = Path.Combine(_testRoot, "Local", "Python");
        string python39 = CreateFile("Program Files", "Python39", "python.exe");
        string python313 = CreateFile("Local", "Programs", "Python", "Python313-arm64", "python.exe");
        string python314 = CreateFile("Local", "Python", "pythoncore-3.14-64", "python.exe");
        CreateFile("Program Files", "PythonManager", "python.exe");
        CreateFile("Program Files", "Python312", "pythonw.exe");
        CreateFile("Program Files", "Other", "python.exe");

        Assert.Equal(
            [python314, python313, python39],
            Pip.FindInstalledPythons(
                [programFiles, userInstalls, installManager, "", Path.Combine(_testRoot, "missing")]
            )
        );
    }

    [WindowsFact]
    public void CandidateInterpretersRankTheStoreStubBehindInstalledPythons()
    {
        string alias = CreateFile("Microsoft", "WindowsApps", "python.exe");
        string venvPython = CreateFile("app", "venv", "Scripts", "python.exe");
        CreateFile("app", "venv", "pyvenv.cfg");
        string installed = CreateFile("Python314", "python.exe");

        Assert.Equal(
            [installed, alias, venvPython],
            Pip.OrderCandidates([venvPython, alias, installed], [installed.ToUpperInvariant()])
        );
    }

    [WindowsFact]
    public void AnInstalledStorePythonKeepsItsPlaceOnPath()
    {
        string alias = CreateFile("Microsoft", "WindowsApps", "python.exe");
        Directory.CreateDirectory(
            Path.Combine(_testRoot, "Microsoft", "WindowsApps", "PythonSoftwareFoundation.Python.3.12_qbz5n2kfra8p0")
        );
        string installed = CreateFile("Python313", "python.exe");

        Assert.Equal([alias, installed], Pip.OrderCandidates([alias], [installed]));
    }

    private string CreateFile(params string[] segments)
    {
        string path = Path.Combine([_testRoot, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void SearchHelpersParseSimpleIndexAndRankPrefixMatchesFirst()
    {
        var names = Pip.ParseSimpleIndexProjectNames(

            PackageEngineFixtureFiles.ReadAllText(Path.Combine("Pip", "simple-index.json"))
        );

        var matches = Pip.SelectSearchMatches("req", names);

        Assert.Equal(
            ["req", "requests", "requestium", "requests-cache", "django-reqtools"],
            matches
        );
    }

    [Theory]
    [InlineData("3.13.15", "2.2.0")]
    [InlineData("3.10.2", "2.1.0")]
    [InlineData("3.7.9", "2.0.0")]
    [InlineData("3.6.0", "1.0.0")]
    public void ResolveLatestCompatibleVersionHonoursTheRunningInterpreter(
        string interpreterVersion,
        string expected
    )
    {
        Assert.True(PythonVersion.TryParse(interpreterVersion, out var interpreter));

        var resolved = Pip.ResolveLatestCompatibleVersion(
            "sample-project",
            PackageEngineFixtureFiles.ReadAllText(
                Path.Combine("Pip", "simple-sample-project.json")
            ),
            interpreter
        );

        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData("zope.interface")]
    [InlineData("zope-interface")]
    [InlineData("Zope_Interface")]
    public void ResolveLatestCompatibleVersionMatchesEscapedDistributionFilenames(
        string projectName
    )
    {
        Assert.True(PythonVersion.TryParse("3.11.4", out var interpreter));

        var resolved = Pip.ResolveLatestCompatibleVersion(
            projectName,
            PackageEngineFixtureFiles.ReadAllText(
                Path.Combine("Pip", "simple-zope-interface.json")
            ),
            interpreter
        );

        Assert.Equal("6.0", resolved);
    }

    [Fact]
    public void ResolveLatestCompatibleVersionDoesNotMatchAVersionThatIsOnlyAPrefix()
    {
        Assert.True(PythonVersion.TryParse("3.11.4", out var interpreter));
        const string payload = """
            {
              "versions": ["2.1.0"],
              "files": [{ "filename": "demo-2.1.01-py3-none-any.whl", "yanked": false }]
            }
            """;

        Assert.Null(Pip.ResolveLatestCompatibleVersion("demo", payload, interpreter));
    }

    [Fact]
    public void ResolveLatestCompatibleVersionRejectsAResponseWithoutAVersionList()
    {
        Assert.True(PythonVersion.TryParse("3.11.4", out var interpreter));

        Assert.Throws<InvalidDataException>(
            () => Pip.ResolveLatestCompatibleVersion("demo", """{ "files": [] }""", interpreter)
        );
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(">=3.8", true)]
    [InlineData(">=3.12", false)]
    [InlineData("not a specifier", false)]
    public void IsInterpreterAllowedTreatsAnAbsentConstraintAsSatisfied(
        string? requiresPython,
        bool expected
    )
    {
        Assert.True(PythonVersion.TryParse("3.11.4", out var interpreter));

        Assert.Equal(expected, Pip.IsInterpreterAllowed(requiresPython, interpreter));
    }

    [Theory]
    [InlineData(
        "pip 26.2.1 from C:\\Python313\\Lib\\site-packages\\pip (python 3.13)",
        "3.13"
    )]
    [InlineData("pip 24.0 from /usr/lib/python3/dist-packages/pip (python 3.11)", "3.11")]
    [InlineData("pip 24.0 from /usr/lib/pip", null)]
    [InlineData(null, null)]
    public void ParseInterpreterVersionReadsThePipVersionBanner(string? banner, string? expected)
    {
        Assert.Equal(expected, Pip.ParseInterpreterVersion(banner));
    }

    [Theory]
    [InlineData("global.index-url='https://mirror.example.test/simple'", true)]
    [InlineData("global.extra-index-url='https://mirror.example.test/simple'", true)]
    [InlineData("global.no-index='true'", true)]
    [InlineData("global.find-links='C:\\\\wheels'", true)]
    [InlineData("install.no-index='true'", true)]
    [InlineData(":env:.config-file='./pip.conf'", false)]
    [InlineData("global.trusted-host='mirror.example.test'", false)]
    [InlineData("global.timeout='60'", false)]
    [InlineData("", false)]
    public void IsIndexConfigurationLineDetectsEverySourceChangingSetting(
        string line,
        bool expected
    )
    {
        Assert.Equal(expected, Pip.IsIndexConfigurationLine(line));
    }

    [Theory]
    [InlineData("zope.interface", "zope-interface")]
    [InlineData("Flask_SQLAlchemy", "flask-sqlalchemy")]
    [InlineData("requests", "requests")]
    public void NormalizeProjectNameForUrlFollowsTheSimpleApiRules(string name, string expected)
    {
        Assert.Equal(expected, Pip.NormalizeProjectNameForUrl(name));
    }

    [Fact]
    public void ParseAvailableUpdatesBuildsPackagesFromFixture()
    {
        var manager = new Pip();

        var packages = Pip.ParseAvailableUpdates(
            File.ReadLines(PackageEngineFixtureFiles.GetPath(Path.Combine("Pip", "outdated-list.txt"))),
            manager.DefaultSource,
            manager
        );

        Assert.Collection(
            packages,
            package =>
            {
                PackageAssert.Matches(package, "Requests", "requests", "2.31.0", "2.32.3");
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            },
            package =>
            {
                PackageAssert.Matches(
                    package,
                    "Django Reqtools",
                    "django-reqtools",
                    "1.0.0",
                    "1.1.0"
                );
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            }
        );
    }

    [Fact]
    public void ParseInstalledPackagesBuildsPackagesFromFixture()
    {
        var manager = new Pip();

        var packages = Pip.ParseInstalledPackages(
            File.ReadLines(PackageEngineFixtureFiles.GetPath(Path.Combine("Pip", "installed-list.txt"))),
            manager.DefaultSource,
            manager
        );

        Assert.Collection(
            packages,
            package =>
            {
                PackageAssert.Matches(package, "Requests", "requests", "2.31.0");
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            },
            package =>
            {
                PackageAssert.Matches(package, "Django Reqtools", "django-reqtools", "1.0.0");
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            }
        );
    }

    [Fact]
    public void OperationHelperBuildsInstallAndUninstallParameters()
    {
        Settings.Set(Settings.K.EnableProxy, true);
        Settings.Set(Settings.K.EnableProxyAuth, false);
        Settings.SetValue(Settings.K.ProxyURL, "http://proxy.example.test:3128/");
        var manager = new Pip();
        var overridenOptions = new OverridenInstallationOptions();
        overridenOptions.Pip_BreakSystemPackages = true;
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("requests")
            .WithOptions(overridenOptions)
            .Build();
        var installOptions = new InstallOptions
        {
            Version = "2.31.0",
            InstallationScope = PackageScope.User,
            PreRelease = true,
            CustomParameters_Install = ["--quiet"],
        };
        var uninstallOptions = new InstallOptions
        {
            CustomParameters_Uninstall = ["--verbose"],
        };

        var installParameters = manager.OperationHelper.GetParameters(
            package,
            installOptions,
            OperationType.Install
        );
        var uninstallParameters = manager.OperationHelper.GetParameters(
            package,
            uninstallOptions,
            OperationType.Uninstall
        );

        Assert.Equal(
            [
                "install",
                "\"requests==2.31.0\"",
                "--no-input",
                "--no-color",
                "--no-cache",
                "--pre",
                "--user",
                "--break-system-packages",
                "--proxy http://proxy.example.test:3128/",
                "--quiet",
            ],
            installParameters
        );
        Assert.Equal(
            [
                "uninstall",
                "requests",
                "--no-input",
                "--no-color",
                "--no-cache",
                "--yes",
                "--break-system-packages",
                "--proxy http://proxy.example.test:3128/",
                "--verbose",
            ],
            uninstallParameters
        );
    }

    [Fact]
    public void OperationHelperUsesAutoRetryMutationsForKnownPipFailures()
    {
        var manager = new Pip();
        var breakSystemPackage = new PackageBuilder().WithManager(manager).WithId("requests").Build();
        var userScopedPackage = new PackageBuilder().WithManager(manager).WithId("requests").Build();

        var externallyManagedResult = manager.OperationHelper.GetResult(
            breakSystemPackage,
            OperationType.Install,
            ["error: externally-managed-environment"],
            1
        );
        var userScopeResult = manager.OperationHelper.GetResult(
            userScopedPackage,
            OperationType.Install,
            ["hint: try again with --user"],
            1
        );
        var failureResult = manager.OperationHelper.GetResult(
            new PackageBuilder().WithManager(manager).WithId("requests").Build(),
            OperationType.Install,
            ["boom"],
            1
        );

        Assert.Equal(OperationVeredict.AutoRetry, externallyManagedResult);
        Assert.True(breakSystemPackage.OverridenOptions.Pip_BreakSystemPackages);
        Assert.Equal(OperationVeredict.AutoRetry, userScopeResult);
        Assert.Equal(PackageScope.User, userScopedPackage.OverridenOptions.Scope);
        Assert.Equal(OperationVeredict.Failure, failureResult);
    }

    [Theory]
    [InlineData(OperationType.Install, OperationVeredict.AutoRetry, PackageScope.User)]
    [InlineData(OperationType.Update, OperationVeredict.Failure, null)]
    public void OperationHelperMovesOnlyNonUpdatesToTheUserSchemeOnARepeatedExternallyManagedFailure(
        OperationType operation,
        OperationVeredict expectedResult,
        string? expectedScope
    )
    {
        var manager = new Pip();
        var package = new PackageBuilder().WithManager(manager).WithId("requests").Build();
        string[] output = ["error: externally-managed-environment"];

        var firstResult = manager.OperationHelper.GetResult(package, operation, output, 1);
        var secondResult = manager.OperationHelper.GetResult(package, operation, output, 1);

        Assert.Equal(OperationVeredict.AutoRetry, firstResult);
        Assert.True(package.OverridenOptions.Pip_BreakSystemPackages);
        Assert.Equal(expectedResult, secondResult);
        Assert.Equal(expectedScope, package.OverridenOptions.Scope);
    }

    private static readonly string[] SharingViolationOutput =
    [
        "Attempting uninstall: botocore",
        "ERROR: Could not install packages due to an OSError: [WinError 32] The process cannot access the file because it is being used by another process: 'C:\\Python314\\Lib\\site-packages\\botocore\\signers.py'",
        "Consider using the `--user` option or check the permissions.",
    ];

    private static readonly string[] PermissionDeniedOutput =
    [
        "ERROR: Could not install packages due to an OSError: [WinError 5] Access is denied: 'C:\\Python314\\Lib\\site-packages\\requests'",
        "Consider using the `--user` option or check the permissions.",
    ];

    [Theory]
    [InlineData(OperationType.Install)]
    [InlineData(OperationType.Update)]
    public void OperationHelperDoesNotMoveToTheUserSchemeOnASharingViolation(
        OperationType operation
    )
    {
        var manager = new Pip();
        var package = new PackageBuilder().WithManager(manager).WithId("boto3").Build();

        var result = manager.OperationHelper.GetResult(
            package,
            operation,
            SharingViolationOutput,
            1
        );

        Assert.Equal(OperationVeredict.Failure, result);
        Assert.Null(package.OverridenOptions.Scope);
    }

    [Fact]
    public void OperationHelperDoesNotMoveAnUpdateToTheUserScheme()
    {
        var manager = new Pip();
        var package = new PackageBuilder().WithManager(manager).WithId("requests").Build();

        var result = manager.OperationHelper.GetResult(
            package,
            OperationType.Update,
            PermissionDeniedOutput,
            1
        );

        Assert.Equal(OperationVeredict.Failure, result);
        Assert.Null(package.OverridenOptions.Scope);
    }

    [Fact]
    public void OperationHelperStillRetriesAnInstallInTheUserSchemeWhenAccessIsDenied()
    {
        var manager = new Pip();
        var package = new PackageBuilder().WithManager(manager).WithId("requests").Build();

        var result = manager.OperationHelper.GetResult(
            package,
            OperationType.Install,
            PermissionDeniedOutput,
            1
        );

        Assert.Equal(OperationVeredict.AutoRetry, result);
        Assert.Equal(PackageScope.User, package.OverridenOptions.Scope);
    }

    [Fact]
    public void PipSerializesItsOperations()
    {
        Assert.True(new Pip().Capabilities.SerializesOperations);
    }

    [Fact]
    public async Task ResolveLatestVersionReportsAFailureWhenTheResolutionBudgetIsAlreadySpent()
    {
        Assert.True(PythonVersion.TryParse("3.12.0", out PythonVersion interpreter));

        using HttpClient client = new(CoreTools.GenericHttpClientParameters);
        using CancellationTokenSource budget = new();
        await budget.CancelAsync();

        Pip.VersionResolution resolution = await Pip.ResolveLatestVersionAsync(
            client,
            "requests",
            interpreter,
            budget.Token
        );

        Assert.True(resolution.Failed);
        Assert.Null(resolution.Version);
    }

    [Fact]
    public async Task ResolveLatestVersionKeepsServingLaterLookupsAfterASpentBudget()
    {
        Assert.True(PythonVersion.TryParse("3.12.0", out PythonVersion interpreter));

        using HttpClient client = new(CoreTools.GenericHttpClientParameters);

        for (int i = 0; i < 12; i++)
        {
            using CancellationTokenSource budget = new();
            await budget.CancelAsync();

            Pip.VersionResolution resolution = await Pip
                .ResolveLatestVersionAsync(client, $"package-{i}", interpreter, budget.Token)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(resolution.Failed);
        }
    }
}
