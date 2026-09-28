using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Tui.FakeData;

/// <summary>
/// Everything <c>--fake-data</c> changes about the process. Activating it redirects every UniGetUI
/// data directory (settings, secure settings, bundles, history, caches) into a throwaway sandbox,
/// builds the fake managers, and points the elevator at the fake one, so a fake-data session can
/// neither read nor modify the user's real UniGetUI state or installed software.
/// </summary>
internal sealed class FakeDataEnvironment
{
    public const string Flag = "--fake-data";
    public const string DirectoryFlag = "--fake-data-dir";
    public const string EnvironmentVariable = "UNIGETUI_TUI_FAKE_DATA";

    private FakeDataEnvironment(string sandbox, string cliPath, string[] cliPrefix)
    {
        SandboxDirectory = sandbox;
        StatePath = Path.Join(sandbox, "fake-system-state.json");
        FakeCliPath = cliPath;
        FakeCliPrefixArgs = cliPrefix;
    }

    /// <summary>The active fake-data environment, or null when running against real managers.</summary>
    public static FakeDataEnvironment? Current { get; private set; }

    public static bool IsActive => Current is not null;

    public string SandboxDirectory { get; }
    public string StatePath { get; }
    public string FakeCliPath { get; }
    public string[] FakeCliPrefixArgs { get; }
    public IReadOnlyList<IPackageManager> Managers { get; private set; } = [];

    /// <summary>True when the command line or environment asks for fake data.</summary>
    public static bool IsRequested(IReadOnlyList<string> args)
        => args.Contains(Flag) || args.Contains(DirectoryFlag)
           || Environment.GetEnvironmentVariable(EnvironmentVariable) is "1" or "true";

    /// <summary>Reads <c>--fake-data-dir &lt;path&gt;</c>, if present.</summary>
    public static string? RequestedDirectory(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count - 1; i++)
            if (args[i] == DirectoryFlag) return args[i + 1];
        return null;
    }

    /// <summary>
    /// Creates the sandbox and redirects all UniGetUI state into it. Must run before anything reads
    /// settings. <paramref name="sandboxDirectory"/> defaults to a fresh temp directory per run, so
    /// every session starts from the same seed; pass a directory to keep state between runs.
    /// </summary>
    public static FakeDataEnvironment Activate(string? sandboxDirectory = null)
    {
        if (Current is not null) return Current;

        string sandbox = Path.GetFullPath(sandboxDirectory
            ?? Path.Join(Path.GetTempPath(), "UniGetUI-TUI-FakeData", $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}"));
        Directory.CreateDirectory(sandbox);

        string data = Path.Join(sandbox, "UniGetUIData");
        Directory.CreateDirectory(data);
        CoreData.TEST_DataDirectoryOverride = data;
        CoreData.TEST_PerUserDataDirectoryOverride = data;
        string downloads = Path.Join(sandbox, "Downloads");
        Directory.CreateDirectory(downloads);
        CoreData.TEST_DownloadsDirectoryOverride = downloads;
        SecureSettings.TEST_SecureSettingsRootOverride = Path.Join(sandbox, "SecureSettings");

        (string cliPath, string[] cliPrefix) = ResolveFakeCli();
        var env = new FakeDataEnvironment(sandbox, cliPath, cliPrefix);
        FakeStateStore.EnsureSeeded(env.StatePath);

        // Children spawned by fake operations inherit this marker (see FakePackageManagerProcess).
        Environment.SetEnvironmentVariable(FakePackageManagerProcess.ChildMarkerVariable, "1");

        // Elevation goes to the fake elevator: same binary, "--fake-elevate" prefix. Nothing is elevated.
        CoreData.ElevatorPath = cliPath;
        CoreData.ElevatorArgs = FakePackageManagerProcess.ElevateFlag;

        // The Devolutions Agent broker is a real out-of-process service; never route fake operations there.
        Settings.Set(Settings.K.UseAgentBroker, false);

        env.Managers = FakeCatalog.Managers.Select(p => (IPackageManager)new FakePackageManager(p, env)).ToArray();
        Current = env;
        return env;
    }

    /// <summary>Resets the fake system to its seed state (used by tests between scenarios).</summary>
    public void ResetState()
    {
        if (File.Exists(StatePath)) File.Delete(StatePath);
        FakeStateStore.EnsureSeeded(StatePath);
    }

    private static (string Path, string[] Prefix) ResolveFakeCli()
    {
        string exeName = OperatingSystem.IsWindows() ? "UniGetUI.Tui.exe" : "UniGetUI.Tui";
        string? processPath = Environment.ProcessPath;
        if (processPath is not null
            && Path.GetFileName(processPath).Equals(exeName, StringComparison.OrdinalIgnoreCase))
            return (processPath, []);

        // Under a test host the process is testhost/dotnet; the TUI apphost sits next to our assembly.
        string candidate = Path.Join(AppContext.BaseDirectory, exeName);
        if (File.Exists(candidate)) return (candidate, []);

        string dll = Path.Join(AppContext.BaseDirectory, "UniGetUI.Tui.dll");
        return ("dotnet", [dll]);
    }
}
