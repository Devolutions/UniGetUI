namespace UniGetUI.Extensions.AgentSkills.Tests;

public sealed class AgentHostLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "UniGetUI-AgentHosts-" + Guid.NewGuid().ToString("N"));

    public AgentHostLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Home => Path.Combine(_root, "home");
    private string AppData => Path.Combine(_root, "roaming");
    private string LocalAppData => Path.Combine(_root, "local");

    private AgentHostProbe Windows(Func<string, string?>? which = null, IReadOnlyList<string>? searchPath = null,
        Func<string, string?>? environment = null, IReadOnlyList<string>? codexRoots = null) => new()
        {
            Home = Home,
            AppData = AppData,
            LocalAppData = LocalAppData,
            IsWindows = true,
            SearchPath = searchPath ?? [],
            Which = which ?? (_ => null),
            GetEnvironment = environment ?? (_ => null),
            CodexAppPackageRoots = () => codexRoots ?? [],
        };

    private AgentHostProbe MacOS() => new()
    {
        Home = Home,
        IsMacOS = true,
        UnixPrefixes = [Path.Combine(_root, "opt", "homebrew")],
        ApplicationFolders = [Path.Combine(_root, "Applications"), Path.Combine(Home, "Applications")],
    };

    private static string Touch(string path, int size = 4)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private string ClaudeBundle(string version, string hash, int size = 16, bool payload = true, bool verified = true,
        int? payloadSize = null, string? digest = null)
    {
        string folder = Path.Combine(AppData, "Claude", "claude-code", version, hash);
        string binary = Touch(Path.Combine(folder, "claude.exe"), size);
        if (payload)
            File.WriteAllText(Path.Combine(folder, ".payload"), $$"""{"sha256":"{{new string('b', 64)}}","size":{{payloadSize ?? size}}}""");
        if (verified)
            File.WriteAllText(Path.Combine(folder, ".verified"), (digest ?? hash + new string('a', 52)) + "\n");
        return binary;
    }

    [Fact]
    public void StandaloneCliOnPathWinsOverDesktopBundle()
    {
        ClaudeBundle("2.1.317", "a33aa5518497");
        string onPath = Touch(Path.Combine(_root, "bin", "claude.exe"));

        Assert.Equal(onPath, AgentHostLocator.Find("claude", Windows(name => name == "claude.exe" ? onPath : null)));
    }

    [Fact]
    public void UserLocalAndWinGetLinksWinOverDesktopBundle()
    {
        ClaudeBundle("2.1.317", "a33aa5518497");
        string winget = Touch(Path.Combine(LocalAppData, "Microsoft", "WinGet", "Links", "claude.exe"));
        Assert.Equal(winget, AgentHostLocator.Find("claude", Windows()));

        string local = Touch(Path.Combine(Home, ".local", "bin", "claude.exe"));
        Assert.Equal(local, AgentHostLocator.Find("claude", Windows()));
    }

    [Fact]
    public void NpmClaudeIsFoundBehindItsCmdShim()
    {
        string prefix = Path.Combine(_root, "node prefix");
        string binary = Touch(Path.Combine(prefix, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"));
        Assert.Equal(binary, AgentHostLocator.Find("claude", Windows(searchPath: ["\"" + prefix + "\""])));

        File.Delete(binary);
        string roaming = Touch(Path.Combine(AppData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"));
        Assert.Equal(roaming, AgentHostLocator.Find("claude", Windows()));
    }

    [Fact]
    public void ClaudeDesktopOnly_UsesNewestVerifiedBundle()
    {
        ClaudeBundle("2.1.309", "9078b9c1d466");
        string newest = ClaudeBundle("2.1.317", "a33aa5518497");
        Directory.CreateDirectory(Path.Combine(AppData, "Claude", "claude-code", "not-a-version"));

        Assert.Equal(newest, AgentHostLocator.Find("claude", Windows()));
    }

    [Fact]
    public void ClaudeDesktopBundle_RejectsIncompleteOrMismatchedDownloads()
    {
        string older = ClaudeBundle("2.1.309", "9078b9c1d466");
        string folder = Path.GetDirectoryName(ClaudeBundle("2.1.317", "a33aa5518497", payloadSize: 999))!;
        Assert.Equal(older, AgentHostLocator.Find("claude", Windows()));

        Directory.Delete(folder, recursive: true);
        ClaudeBundle("2.1.317", "a33aa5518497", verified: false);
        Assert.Equal(older, AgentHostLocator.Find("claude", Windows()));

        Directory.Delete(folder, recursive: true);
        ClaudeBundle("2.1.317", "a33aa5518497", digest: new string('f', 64));
        Assert.Equal(older, AgentHostLocator.Find("claude", Windows()));

        Directory.Delete(folder, recursive: true);
        string legacy = ClaudeBundle("2.1.317", "a33aa5518497", payload: false, verified: false);
        Assert.Equal(legacy, AgentHostLocator.Find("claude", Windows()));
    }

    [Fact]
    public void ClaudeDesktopBundle_RejectsOversizedOrMalformedMarkers()
    {
        string older = ClaudeBundle("2.1.309", "9078b9c1d466");
        string folder = Path.GetDirectoryName(ClaudeBundle("2.1.317", "a33aa5518497"))!;
        File.WriteAllText(Path.Combine(folder, ".payload"), "{" + new string(' ', 5000) + "}");
        Assert.Equal(older, AgentHostLocator.Find("claude", Windows()));

        File.WriteAllText(Path.Combine(folder, ".payload"), "not json");
        Assert.Equal(older, AgentHostLocator.Find("claude", Windows()));
    }

    [Fact]
    public void ClaudeDesktopMsixLayouts_AreFound()
    {
        string packaged = Touch(Path.Combine(LocalAppData, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming",
            "Claude", "claude-code", "2.1.300", "claude.exe"));
        Assert.Equal(packaged, AgentHostLocator.Find("claude", Windows()));

        string relocated = Touch(Path.Combine(LocalAppData, "Claude-Data", "profile", "Roaming", "Claude", "claude-code",
            "2.1.400", "claude.exe"));
        Assert.Equal(relocated, AgentHostLocator.Find("claude", Windows()));
    }

    [Fact]
    public void CodexCliPathOverrideWinsWhenItExists()
    {
        string custom = Touch(Path.Combine(_root, "custom", "codex.exe"));
        string onPath = Touch(Path.Combine(_root, "bin", "codex.exe"));

        Assert.Equal(custom, AgentHostLocator.Find("codex",
            Windows(_ => onPath, environment: name => name == "CODEX_CLI_PATH" ? custom : null)));
        Assert.Equal(onPath, AgentHostLocator.Find("codex",
            Windows(_ => onPath, environment: name => name == "CODEX_CLI_PATH" ? custom + ".missing" : null)));
        Assert.Equal(onPath, AgentHostLocator.Find("claude",
            Windows(_ => onPath, environment: _ => custom)));
    }

    [Fact]
    public void NpmCodexVendorBinaryIsFound()
    {
        string prefix = Path.Combine(_root, "npm");
        string binary = Touch(Path.Combine(prefix, "node_modules", "@openai", "codex", "node_modules", "@openai",
            "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe"));

        Assert.Equal(binary, AgentHostLocator.Find("codex", Windows(searchPath: [prefix])));
    }

    [Fact]
    public void CodexAppOnly_UsesTheBundledCliOfTheFirstInstalledPackage()
    {
        string missing = Path.Combine(_root, "WindowsApps", "OpenAI.Codex_1.0.0.0_x64__2p2nqsd0c76g0");
        string package = Path.Combine(_root, "WindowsApps", "OpenAI.Codex_26.1027.6604.0_x64__2p2nqsd0c76g0");
        string binary = Touch(Path.Combine(package, "app", "resources", "codex.exe"));

        Assert.Equal(binary, AgentHostLocator.Find("codex", Windows(codexRoots: [missing, package])));
        Assert.Null(AgentHostLocator.Find("git", Windows(codexRoots: [package])));
    }

    [Fact]
    public void CodexPackageNamesAreMatchedByNameAndPublisher()
    {
        Assert.True(AgentHostLocator.IsCodexPackage("OpenAI.Codex_26.1027.6604.0_x64__2p2nqsd0c76g0"));
        Assert.True(AgentHostLocator.IsCodexPackage("openai.codex_26.1.0.0_arm64__2P2NQSD0C76G0"));
        Assert.False(AgentHostLocator.IsCodexPackage("OpenAI.Codex_26.1027.6604.0_x64__otherpublisher"));
        Assert.False(AgentHostLocator.IsCodexPackage("OpenAI.CodexBeta_26.1027.6604.0_x64__2p2nqsd0c76g0"));
        Assert.False(AgentHostLocator.IsCodexPackage("OpenAI.Codex_2p2nqsd0c76g0"));
        Assert.Equal(new Version(26, 1027, 6604, 0), AgentHostLocator.PackageVersion("OpenAI.Codex_26.1027.6604.0_x64__2p2nqsd0c76g0"));
        Assert.Equal(new Version(0, 0), AgentHostLocator.PackageVersion("OpenAI.Codex_latest_x64__2p2nqsd0c76g0"));
    }

    [Fact]
    public void MacOS_FindsHomebrewThenDesktopAppBundles()
    {
        string claudeApp = Touch(Path.Combine(Home, "Library", "Application Support", "Claude", "claude-code", "2.1.5",
            "abcdef123456", "claude.app", "Contents", "MacOS", "claude"));
        string codexApp = Touch(Path.Combine(Home, "Applications", "Codex.app", "Contents", "Resources", "codex"));

        Assert.Equal(claudeApp, AgentHostLocator.Find("claude", MacOS()));
        Assert.Equal(codexApp, AgentHostLocator.Find("codex", MacOS()));

        string chatGpt = Touch(Path.Combine(_root, "Applications", "ChatGPT.app", "Contents", "Resources", "codex"));
        Assert.Equal(chatGpt, AgentHostLocator.Find("codex", MacOS()));

        string brew = Touch(Path.Combine(_root, "opt", "homebrew", "bin", "claude"));
        Assert.Equal(brew, AgentHostLocator.Find("claude", MacOS()));
    }

    [Fact]
    public void Linux_HasNoDesktopAppFallback()
    {
        Touch(Path.Combine(Home, "Library", "Application Support", "Claude", "claude-code", "2.1.5", "claude"));
        var linux = MacOS() with { IsMacOS = false };

        Assert.Null(AgentHostLocator.Find("claude", linux));
        Assert.Null(AgentHostLocator.Find("codex", linux));
    }

    [Fact]
    public void CurrentProbe_DescribesThisMachine()
    {
        AgentHostProbe probe = AgentHostProbe.CreateCurrent();

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), probe.Home);
        Assert.Equal(OperatingSystem.IsWindows(), probe.IsWindows);
        Assert.Equal(OperatingSystem.IsMacOS(), probe.IsMacOS);
        Assert.NotNull(probe.CodexAppPackageRoots());
    }
}
