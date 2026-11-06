using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.Extensions.AgentSkills;

/// <summary>The machine layout an agent CLI is looked up in. Tests replace it with a temporary tree.</summary>
internal sealed record AgentHostProbe
{
    public required string Home { get; init; }
    public string AppData { get; init; } = "";
    public string LocalAppData { get; init; } = "";
    public bool IsWindows { get; init; }
    public bool IsMacOS { get; init; }
    public bool IsArm64 { get; init; }
    public IReadOnlyList<string> SearchPath { get; init; } = [];
    public IReadOnlyList<string> UnixPrefixes { get; init; } = [];
    public IReadOnlyList<string> ApplicationFolders { get; init; } = [];
    public Func<string, string?> Which { get; init; } = _ => null;
    public Func<string, string?> GetEnvironment { get; init; } = _ => null;
    public Func<IReadOnlyList<string>> CodexAppPackageRoots { get; init; } = () => [];

    public static AgentHostProbe CreateCurrent()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new()
        {
            Home = home,
            AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            IsWindows = OperatingSystem.IsWindows(),
            IsMacOS = OperatingSystem.IsMacOS(),
            IsArm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64,
            SearchPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            UnixPrefixes = ["/opt/homebrew", "/usr/local"],
            ApplicationFolders = ["/Applications", Path.Combine(home, "Applications")],
            Which = name => CoreTools.Which(name) is { Item1: true } found ? found.Item2 : null,
            GetEnvironment = Environment.GetEnvironmentVariable,
            CodexAppPackageRoots = OperatingSystem.IsWindows() ? AgentHostLocator.ReadCodexPackageRoots : () => [],
        };
    }
}

/// <summary>
/// Finds an agent CLI. The Claude and Codex desktop apps each ship their own copy of the CLI and share plugin
/// state with the standalone one (~/.claude and CODEX_HOME), so a desktop-only install is a valid host too.
/// A standalone install wins; the bundled copy is re-resolved every time because app updates move it.
/// </summary>
internal static class AgentHostLocator
{
    private const string AppxRepositoryKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    private const string CodexPackageName = "OpenAI.Codex";
    private const string CodexPublisherId = "2p2nqsd0c76g0";
    private const string ClaudePackageFamily = "Claude_pzs8sxrjxfjjc";

    public static string? Find(string tool) => Find(tool, AgentHostProbe.CreateCurrent());

    internal static string? Find(string tool, AgentHostProbe probe)
    {
        string name = probe.IsWindows ? tool + ".exe" : tool;
        if (tool == "codex" && probe.GetEnvironment("CODEX_CLI_PATH") is { Length: > 0 } custom && File.Exists(custom))
            return custom;
        if (probe.Which(name) is { Length: > 0 } onPath) return onPath;
        if (StandaloneCandidates(tool, name, probe).FirstOrDefault(File.Exists) is { } standalone) return standalone;
        string? bundled = tool switch
        {
            "claude" => FindClaudeDesktopCli(probe),
            "codex" => FindCodexAppCli(probe),
            _ => null,
        };
        if (bundled is not null) Logger.Debug($"Using the {tool} CLI bundled with its desktop app: {bundled}");
        return bundled;
    }

    private static IEnumerable<string> StandaloneCandidates(string tool, string name, AgentHostProbe probe)
    {
        yield return Path.Combine(probe.Home, ".local", "bin", name);
        if (!probe.IsWindows)
        {
            foreach (string prefix in probe.UnixPrefixes) yield return Path.Combine(prefix, "bin", name);
            yield break;
        }
        if (probe.LocalAppData.Length > 0)
            yield return Path.Combine(probe.LocalAppData, "Microsoft", "WinGet", "Links", name);
        // npm puts only .cmd/.ps1 shims on PATH; their target is the native binary inside the package.
        string arch = probe.IsArm64 ? "arm64" : "x64";
        string triple = probe.IsArm64 ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
        IEnumerable<string> prefixes = probe.SearchPath;
        if (probe.AppData.Length > 0) prefixes = prefixes.Append(Path.Combine(probe.AppData, "npm"));
        foreach (string prefix in prefixes)
        {
            string modules = Path.Combine(prefix.Trim('"'), "node_modules");
            if (tool == "claude")
            {
                yield return Path.Combine(modules, "@anthropic-ai", "claude-code", "bin", name);
            }
            else if (tool == "codex")
            {
                string package = Path.Combine(modules, "@openai", "codex");
                foreach (string vendor in new[]
                         {
                             Path.Combine(package, "node_modules", "@openai", "codex-win32-" + arch, "vendor"),
                             Path.Combine(package, "vendor"),
                         })
                {
                    yield return Path.Combine(vendor, triple, "bin", name);
                    yield return Path.Combine(vendor, triple, "codex", name);
                }
            }
        }
    }

    /// <summary>
    /// Claude Desktop downloads the CLI used by its Code tab to claude-code/&lt;version&gt;/&lt;hash&gt;/. A hash folder
    /// is only used once the app has marked it verified, so an interrupted download is never picked.
    /// </summary>
    internal static string? FindClaudeDesktopCli(AgentHostProbe probe)
    {
        string[] binaries = probe.IsWindows
            ? ["claude.exe"]
            : [Path.Combine("claude.app", "Contents", "MacOS", "claude"), "claude"];
        try
        {
            return ClaudeDesktopRoots(probe)
                .SelectMany(VersionFolders)
                .OrderByDescending(folder => folder.Version)
                .Select(folder => VerifiedBinary(folder.Path, binaries))
                .FirstOrDefault(path => path is not null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Debug($"Could not inspect the Claude Desktop CLI folders: {ex.Message}");
            return null;
        }
    }

    private static IEnumerable<string> ClaudeDesktopRoots(AgentHostProbe probe)
    {
        if (probe.IsWindows)
        {
            if (probe.AppData.Length > 0) yield return Path.Combine(probe.AppData, "Claude", "claude-code");
            if (probe.LocalAppData.Length == 0) yield break;
            yield return Path.Combine(probe.LocalAppData, "Packages", ClaudePackageFamily, "LocalCache", "Roaming", "Claude", "claude-code");
            // Newer MSIX builds moved their data to %LOCALAPPDATA%\Claude-Data; its inner layout is undocumented.
            foreach (string folder in FindFolders(Path.Combine(probe.LocalAppData, "Claude-Data"), "claude-code", 3))
                yield return folder;
        }
        else if (probe.IsMacOS)
        {
            yield return Path.Combine(probe.Home, "Library", "Application Support", "Claude", "claude-code");
        }
    }

    private static IEnumerable<string> FindFolders(string root, string name, int depth)
    {
        if (depth < 0 || !Directory.Exists(root)) yield break;
        foreach (string folder in SafeDirectories(root))
        {
            if (string.Equals(Path.GetFileName(folder), name, StringComparison.OrdinalIgnoreCase))
                yield return folder;
            else if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0)
                foreach (string match in FindFolders(folder, name, depth - 1))
                    yield return match;
        }
    }

    private static IEnumerable<(Version Version, string Path)> VersionFolders(string root)
    {
        foreach (string folder in SafeDirectories(root))
        {
            string name = Path.GetFileName(folder);
            if (Version.TryParse(name.Split('-', '+')[0], out Version? version))
                yield return (version, folder);
        }
    }

    private static string? VerifiedBinary(string versionFolder, string[] binaries)
    {
        foreach (string binary in binaries)
        {
            string direct = Path.Combine(versionFolder, binary);
            if (File.Exists(direct)) return direct;
        }
        foreach (string hashFolder in SafeDirectories(versionFolder).OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            foreach (string binary in binaries)
            {
                string candidate = Path.Combine(hashFolder, binary);
                if (File.Exists(candidate) && IsVerified(hashFolder, candidate)) return candidate;
            }
        }
        return null;
    }

    internal static bool IsVerified(string hashFolder, string binary)
    {
        string payload = Path.Combine(hashFolder, ".payload");
        if (!File.Exists(payload)) return true;
        string verified = Path.Combine(hashFolder, ".verified");
        if (!File.Exists(verified) || new FileInfo(verified).Length > 1024 || new FileInfo(payload).Length > 4096)
            return false;
        string digest = File.ReadAllText(verified).Trim();
        string folder = Path.GetFileName(hashFolder);
        if (folder.Length == 0 || !digest.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var expected = JsonSerializer.Deserialize(File.ReadAllText(payload), AgentHostJsonContext.Default.ClaudeBundlePayload);
            return expected?.Size is not { } size || size == new FileInfo(binary).Length;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string? FindCodexAppCli(AgentHostProbe probe)
    {
        if (probe.IsWindows)
            return probe.CodexAppPackageRoots()
                .Select(root => Path.Combine(root, "app", "resources", "codex.exe"))
                .FirstOrDefault(File.Exists);
        if (probe.IsMacOS)
            return probe.ApplicationFolders
                .SelectMany(folder => new[] { "Codex.app", "ChatGPT.app" }
                    .Select(app => Path.Combine(folder, app, "Contents", "Resources", "codex")))
                .FirstOrDefault(File.Exists);
        return null;
    }

    [SupportedOSPlatform("windows")]
    internal static IReadOnlyList<string> ReadCodexPackageRoots()
    {
        List<(Version Version, string Folder)> matches = [];
        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(AppxRepositoryKey);
            if (root is null) return [];
            foreach (string packageFullName in root.GetSubKeyNames().Where(IsCodexPackage))
            {
                using RegistryKey? entry = root.OpenSubKey(packageFullName);
                if (entry?.GetValue("PackageRootFolder") is string folder && !string.IsNullOrWhiteSpace(folder))
                    matches.Add((PackageVersion(packageFullName), folder));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Logger.Debug($"Could not read the Codex app install location from the registry: {ex.Message}");
            return [];
        }
        return matches.OrderByDescending(match => match.Version).Select(match => match.Folder).ToArray();
    }

    internal static bool IsCodexPackage(string packageFullName)
    {
        string[] pieces = packageFullName.Split('_');
        return pieces.Length >= 4
            && pieces[0].Equals(CodexPackageName, StringComparison.OrdinalIgnoreCase)
            && pieces[^1].Equals(CodexPublisherId, StringComparison.OrdinalIgnoreCase);
    }

    internal static Version PackageVersion(string packageFullName)
    {
        string[] pieces = packageFullName.Split('_');
        return pieces.Length >= 2 && Version.TryParse(pieces[1], out Version? version) ? version : new Version(0, 0);
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

internal sealed class ClaudeBundlePayload
{
    public long? Size { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClaudeBundlePayload))]
internal sealed partial class AgentHostJsonContext : JsonSerializerContext;
