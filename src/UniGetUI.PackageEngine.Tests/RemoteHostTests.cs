using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.RemoteHosts;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

public sealed class RemoteHostTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(RemoteHostTests),
        Guid.NewGuid().ToString("N")
    );

    public RemoteHostTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        WslDistroCatalog.ListOverride = static () => [];
        Settings.ResetSettings();
        RemoteHostService.Instance.ReloadFromStore();
    }

    public void Dispose()
    {
        Settings.ResetSettings();
        WslDistroCatalog.ListOverride = null;
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    [Theory]
    [InlineData("linux-box")]
    [InlineData("user@server")]
    [InlineData("user@192.168.1.10")]
    [InlineData("tailscale-host")]
    [InlineData("[::1]")]
    [InlineData("user@host:22")]
    public void ValidDestinationsAreAccepted(string destination)
    {
        Assert.True(RemoteHost.IsValidDestination(destination));
        var host = new RemoteHost(destination, "Lab");
        Assert.Equal(destination, host.Destination);
        Assert.Equal("Lab", host.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-oProxyCommand=evil")]
    [InlineData("-J jump")]
    [InlineData("user@host; rm -rf /")]
    [InlineData("user@host && id")]
    [InlineData("host with spaces")]
    [InlineData("user@host`id`")]
    public void InvalidDestinationsAreRejected(string destination)
    {
        Assert.False(RemoteHost.IsValidDestination(RemoteHost.NormalizeDestination(destination)));
        Assert.Throws<RemoteHostException>(() => new RemoteHost(destination));
    }

    [Fact]
    public void DestinationLongerThanCapIsRejected()
    {
        string destination = new('a', RemoteHost.MaxDestinationLength + 1);
        Assert.False(RemoteHost.IsValidDestination(destination));
    }

    [Fact]
    public void StoreRejectsDuplicateDestinations()
    {
        RemoteHostStore.AddOrUpdate(new RemoteHost("box-a", "A"));
        var duplicate = Assert.Throws<RemoteHostException>(
            () => RemoteHostStore.AddOrUpdate(new RemoteHost("box-a", "B"))
        );
        Assert.Equal(RemoteHostErrorKind.DuplicateDestination, duplicate.Kind);
    }

    [Fact]
    public void StoreRoundTripsHostsAsJson()
    {
        Guid id = Guid.NewGuid();
        RemoteHostStore.AddOrUpdate(new RemoteHost("user@lab", "Lab", id));
        IReadOnlyList<RemoteHost> loaded = RemoteHostStore.Load();
        RemoteHost host = Assert.Single(loaded);
        Assert.Equal(id, host.Id);
        Assert.Equal("user@lab", host.Destination);
        Assert.Equal("Lab", host.Name);
    }

    [Fact]
    public void WslHostUsesDeterministicIdAndDisplayName()
    {
        RemoteHost host = RemoteHost.ForWsl("Ubuntu-24.04");
        Assert.Equal(RemoteHostKind.Wsl, host.Kind);
        Assert.Equal("Ubuntu-24.04", host.Destination);
        Assert.Equal("Ubuntu-24.04 (WSL)", host.DisplayName);
        Assert.Equal(WslDistroCatalog.CreateHostId("Ubuntu-24.04"), host.Id);
        Assert.Equal(host.Id, RemoteHost.ForWsl("Ubuntu-24.04").Id);
    }

    [Fact]
    public void DisabledWslDistrosAreOmittedFromPicker()
    {
        WslDistroCatalog.ListOverride = static () =>
        [
            new WslDistroInfo("Ubuntu", "Running", 2, true),
            new WslDistroInfo("Debian", "Stopped", 2, false),
        ];
        WslDistroCatalog.SetEnabled("Debian", false);
        RemoteHostService.Instance.ReloadFromStore();

        IReadOnlyList<RemoteHostPickerItem> items = RemoteHostService.Instance.GetPickerItems("This PC");
        Assert.Equal(2, items.Count);
        Assert.Null(items[0].HostId);
        Assert.Equal("Ubuntu (WSL)", items[1].DisplayName);
        Assert.True(RemoteHostService.Instance.TryGetHost(items[1].HostId!.Value, out RemoteHost host));
        Assert.Equal(RemoteHostKind.Wsl, host.Kind);
    }

    [Fact]
    public void CatalogSkipsHelpersAndDisabledNames()
    {
        WslDistroCatalog.ListOverride = static () =>
        [
            new WslDistroInfo("Ubuntu", "Running", 2, true),
            new WslDistroInfo("docker-desktop", "Running", 2, false),
        ];

        IReadOnlyList<WslDistroInfo> installed = WslDistroCatalog.ListInstalled();
        Assert.Equal("Ubuntu", Assert.Single(installed).Name);

        WslDistroCatalog.ListOverride = static () => [new WslDistroInfo("Ubuntu", "Stopped", 2, true)];
        WslDistroCatalog.SetEnabled("Ubuntu", false);
        Assert.Empty(WslDistroCatalog.GetEnabledHosts());
        WslDistroCatalog.SetEnabled("Ubuntu", true);
        Assert.Equal("Ubuntu", Assert.Single(WslDistroCatalog.GetEnabledHosts()).Destination);
    }
}

public sealed class RemoteSshClientTests
{
    [Fact]
    public void BuildBaseArgumentsUsesBatchModeAndDoesNotPassShellMetacharactersAsOptions()
    {
        IReadOnlyList<string> args = RemoteSshClient.BuildBaseArguments("user@host");
        Assert.Equal("-T", args[0]);
        Assert.Contains("BatchMode=yes", args);
        Assert.Contains("StrictHostKeyChecking=yes", args);
        Assert.Contains("ConnectTimeout=10", args);
        Assert.Contains("ServerAliveInterval=5", args);
        Assert.Contains("ServerAliveCountMax=3", args);
        Assert.Equal("--", args[^2]);
        Assert.Equal("user@host", args[^1]);
        Assert.DoesNotContain(args, item => item.StartsWith('-') && item.Length > 2 && item[1] != 'o' && item != "--");
    }

    [Fact]
    public void BuildArgumentsAppendsRemoteCommandAfterDestination()
    {
        var client = new RemoteSshClient();
        var host = new RemoteHost("demo");
        IReadOnlyList<string> args = client.BuildArguments(host, "uname -s");
        Assert.Equal("demo", args[^2]);
        Assert.Equal("uname -s", args[^1]);
        Assert.Equal("--", args[^3]);
    }

    [Fact]
    public void PosixDispatchPrefersUniGetUIThenFallsBackToLinuxScript()
    {
        string command = RemoteSshClient.BuildPosixDispatchCommand(
            RemoteSshClient.AgentArguments("hello"),
            "echo linux"
        );
        Assert.Contains("command -v unigetui", command);
        Assert.Contains("'remote' '--protocol' '1' 'hello'", command);
        Assert.Contains("[ \"$(uname -s 2>/dev/null)\" = Linux ]", command);
        Assert.Contains("echo linux", command);
    }

    [Fact]
    public async Task ProbeUsesInjectedRunnerAndBatchModeFlags()
    {
        var runner = new FakeRemoteProcessRunner
        {
            Handler = (_, arguments) =>
            {
                if (arguments[^1] == "uname -s")
                    return new RemoteProcessResult(0, "Linux\n", "");
                return new RemoteProcessResult(0, LinuxAgentlessInventoryFixtures.Apt, "");
            },
        };

        var client = new RemoteSshClient(runner, "ssh");
        RemoteControlResponse response = await client.ProbeAsync(new RemoteHost("lab"));
        Assert.True(response.Ok);
        Assert.Equal(RemoteBackendKind.LinuxAgentless, response.BackendKind);
        Assert.Contains(runner.Calls, call => call.Arguments.Contains("BatchMode=yes"));
        Assert.All(runner.Calls, call => Assert.Equal("ssh", call.FileName));
    }

    [Fact]
    public void MapErrorDetectsAuthAndHostKeyFailures()
    {
        var host = new RemoteHost("lab");
        RemoteSshException auth = RemoteSshClient.MapError(
            host,
            new RemoteProcessResult(255, "", "Permission denied (publickey).")
        );
        Assert.Equal(RemoteSshErrorKind.AuthenticationFailed, auth.Kind);

        RemoteSshException untrusted = RemoteSshClient.MapError(
            host,
            new RemoteProcessResult(255, "", "Host key verification failed.")
        );
        Assert.Equal(RemoteSshErrorKind.UntrustedHost, untrusted.Kind);
    }

    private sealed class FakeRemoteProcessRunner : IRemoteProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];
        public required Func<string, IReadOnlyList<string>, RemoteProcessResult> Handler { get; init; }

        public Task<RemoteProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            Action<string>? onOutput = null,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add((fileName, arguments));
            return Task.FromResult(Handler(fileName, arguments));
        }
    }
}

public static class LinuxAgentlessInventoryFixtures
{
    public const string Apt = """
__UGUI_LINUX_V1__
__UGUI_PROFILE__
Ubuntu 24.04	x86_64	1	apt
__UGUI_APT_VERSIONS__
bash	5.2-1
coreutils	9.4-1
hidden	1.0
__UGUI_APT_FILES__
bash: /bin/bash
coreutils: /usr/bin/ls
hidden: /usr/share/doc/hidden
__UGUI_APT_UPDATES__
Inst bash [5.2-1] (5.2-2 Ubuntu:24.04/noble [amd64])
__UGUI_END__
""";

    public const string Dnf = """
__UGUI_LINUX_V1__
__UGUI_PROFILE__
Fedora Linux 41	x86_64	0	dnf
__UGUI_RPM_FILES__
bash	0:5.2.21-1.fc41.x86_64	-rwxr-xr-x	/usr/bin/bash
hidden	0:1.0-1.fc41.x86_64	-rw-r--r--	/usr/share/doc/hidden
__UGUI_SYSTEM_UPDATES__
bash	0:5.2.26-1.fc41.x86_64
__UGUI_END__
""";

    public const string Pacman = """
__UGUI_LINUX_V1__
__UGUI_PROFILE__
Arch Linux	x86_64	1	pacman
__UGUI_PACMAN_PACKAGES__
bash	5.2.026-1
hidden	1.0-1
__UGUI_PACMAN_FILES__
bash	/usr/bin/bash
hidden	/usr/share/doc/hidden
__UGUI_PACMAN_UPDATES__
bash	5.2.037-1
__UGUI_END__
""";
}

public sealed class LinuxAgentlessParserTests
{
    [Fact]
    public void ParsesAptPackagesWithBinariesAndUpdates()
    {
        Assert.True(LinuxAgentless.TryParseInventory(LinuxAgentlessInventoryFixtures.Apt, out RemoteControlResponse? response));
        Assert.NotNull(response);
        Assert.True(response.Ok);
        Assert.Equal("apt", response.SystemPackageManager);
        Assert.True(response.CanElevate);
        Assert.Equal(2, response.Packages.Count);
        RemoteInventoryPackageDto bash = Assert.Single(response.Packages, pkg => pkg.Id == "bash");
        Assert.True(bash.IsUpgradable);
        Assert.Equal("5.2-2", bash.NewVersion);
        Assert.Contains(response.Packages, pkg => pkg.Id == "coreutils" && !pkg.IsUpgradable);
        Assert.DoesNotContain(response.Packages, pkg => pkg.Id == "hidden");
    }

    [Fact]
    public void ParsesDnfExecutablePackagesAndReadOnlyElevation()
    {
        Assert.True(LinuxAgentless.TryParseInventory(LinuxAgentlessInventoryFixtures.Dnf, out RemoteControlResponse? response));
        Assert.NotNull(response);
        Assert.False(response.CanElevate);
        RemoteInventoryPackageDto bash = Assert.Single(response.Packages);
        Assert.Equal("bash", bash.Id);
        Assert.True(bash.IsUpgradable);
    }

    [Fact]
    public void ParsesPacmanPackagesWithBinaries()
    {
        Assert.True(LinuxAgentless.TryParseInventory(LinuxAgentlessInventoryFixtures.Pacman, out RemoteControlResponse? response));
        Assert.NotNull(response);
        RemoteInventoryPackageDto bash = Assert.Single(response.Packages);
        Assert.Equal("bash", bash.Id);
        Assert.Equal("5.2.037-1", bash.NewVersion);
    }
}

public sealed class RemotePackageIdentityTests
{
    [Fact]
    public void HashIncludesRemoteHostId()
    {
        Guid hostA = Guid.NewGuid();
        Guid hostB = Guid.NewGuid();
        var local = new PackageBuilder().WithId("bash").Build();
        var remoteA = new PackageBuilder().WithId("bash").WithRemoteHostId(hostA).Build();
        var remoteB = new PackageBuilder().WithId("bash").WithRemoteHostId(hostB).Build();

        Assert.NotEqual(local.GetHash(), remoteA.GetHash());
        Assert.NotEqual(remoteA.GetHash(), remoteB.GetHash());
        Assert.False(local.IsEquivalentTo(remoteA));
        Assert.True(remoteA.IsEquivalentTo(new PackageBuilder().WithId("bash").WithRemoteHostId(hostA).Build()));
    }
}

public sealed class WslListParserTests
{
    private const string VerboseUtf8 = """
  NAME              STATE           VERSION
* Ubuntu            Running         2
  Debian            Stopped         1
  docker-desktop    Running         2
  docker-desktop-data Stopped       2
  podman-data       Stopped         2
""";

    [Fact]
    public void ParseVerboseListReadsDefaultStateAndVersion()
    {
        IReadOnlyList<WslDistroInfo> distros = WslListParser.ParseVerboseList(VerboseUtf8);
        Assert.Equal(5, distros.Count);
        Assert.Equal("Ubuntu", distros[0].Name);
        Assert.True(distros[0].IsDefault);
        Assert.Equal("Running", distros[0].State);
        Assert.Equal(2, distros[0].Version);
        Assert.Equal("Debian", distros[1].Name);
        Assert.False(distros[1].IsDefault);
        Assert.Equal(1, distros[1].Version);
    }

    [Fact]
    public void HelperDistrosAreDetected()
    {
        Assert.True(WslListParser.IsHelperDistro("docker-desktop"));
        Assert.True(WslListParser.IsHelperDistro("docker-desktop-data"));
        Assert.True(WslListParser.IsHelperDistro("podman-data"));
        Assert.False(WslListParser.IsHelperDistro("Ubuntu"));
        Assert.False(WslListParser.IsHelperDistro("Debian"));
    }

    [Fact]
    public void DecodeListOutputHandlesUtf16LeWithBom()
    {
        byte[] raw = System.Text.Encoding.Unicode.GetPreamble()
            .Concat(System.Text.Encoding.Unicode.GetBytes(VerboseUtf8))
            .ToArray();
        string text = WslListParser.DecodeListOutput(raw);
        WslDistroInfo ubuntu = Assert.Single(WslListParser.ParseVerboseList(text), d => d.Name == "Ubuntu");
        Assert.True(ubuntu.IsDefault);
    }

    [Fact]
    public void DecodeListOutputHandlesUtf16LeWithoutBom()
    {
        byte[] raw = System.Text.Encoding.Unicode.GetBytes(VerboseUtf8);
        string text = WslListParser.DecodeListOutput(raw);
        Assert.Contains(WslListParser.ParseVerboseList(text), d => d.Name == "Debian");
    }

    [Fact]
    public void DecodeListOutputHandlesUtf8()
    {
        byte[] raw = System.Text.Encoding.UTF8.GetBytes(VerboseUtf8);
        string text = WslListParser.DecodeListOutput(raw);
        Assert.Equal(5, WslListParser.ParseVerboseList(text).Count);
    }
}
