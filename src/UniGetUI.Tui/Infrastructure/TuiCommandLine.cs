using UniGetUI.Shared;
using UniGetUI.Tui.FakeData;
using UniGetUI.Tui.Theme;

namespace UniGetUI.Tui.Infrastructure;

/// <summary>Parsed TUI command line. See <see cref="HelpText"/> for the grammar.</summary>
internal sealed class TuiCommandLine
{
    public const string PageFlag = "--page";
    public const string UpdateAppsFlag = "--updateapps";
    public const string ThemeFlag = "--theme";

    private static readonly string[] BundleExtensions = [".ubundle", ".json", ".yaml", ".xml"];

    private static readonly string[] SettingsCommands =
    [
        SharedPreUiCommandDispatcher.ImportSettingsArgument,
        SharedPreUiCommandDispatcher.ExportSettingsArgument,
        SharedPreUiCommandDispatcher.EnableSettingArgument,
        SharedPreUiCommandDispatcher.DisableSettingArgument,
        SharedPreUiCommandDispatcher.SetSettingValueArgument,
        SharedPreUiCommandDispatcher.EnableSecureSettingArgument,
        SharedPreUiCommandDispatcher.DisableSecureSettingArgument,
        UniGetUI.Core.SettingsEngine.SecureSettings.SecureSettings.Args.ENABLE_FOR_USER,
        UniGetUI.Core.SettingsEngine.SecureSettings.SecureSettings.Args.DISABLE_FOR_USER,
    ];

    public static readonly string HelpText = """
        UniGetUI Terminal UI

        Usage: unigetui tui [options] [bundle-file]
               uniget tui [options] [bundle-file]

          --help                          Show this help and exit.
          --fake-data                     Run against a built-in fake data set. No real package
                                          manager is called and nothing is installed; all settings
                                          and state live in a throwaway sandbox directory.
          --fake-data-dir <dir>           Like --fake-data, but keep the sandbox in <dir> so state
                                          survives restarts.
          --page <id>                     Open on a page: discover, updates, installed, bundles,
                                          operations, managers, settings, logs, history, help, about.
          --updateapps                    Update every upgradable package once the list has loaded.
          --theme <id>                    Use a colour theme for this session (the saved theme is
                                          chosen in Settings or View > Theme). Themes are listed below.
          <file.ubundle|.json|.yaml|.xml> Open a package bundle on start.

        Settings commands (run without opening the UI, then exit):
          --import-settings <file>        --export-settings <file>
          --enable-setting <Key>          --disable-setting <Key>
          --set-setting-value <Key> <Value>
          --enable-secure-setting <Key>   --disable-secure-setting <Key>
        """;

    private readonly string[] _args;

    private TuiCommandLine(string[] args) => _args = args;

    public bool ShowHelp { get; private init; }
    public bool HasHeadlessCommand => _args.Length > 0;
    public bool FakeData { get; private init; }
    public string? FakeDataDirectory { get; private init; }
    public string? StartupPage { get; private init; }
    public bool UpdateAppsOnStart { get; private init; }
    public TuiTheme? Theme { get; private init; }
    public IReadOnlyList<string> BundleFiles { get; private init; } = [];

    /// <summary>Arguments forwarded to the console lifetime (none today).</summary>
    public string[] RemainingArgs => [];

    public static TuiCommandLine Parse(string[] args)
    {
        string? page = null;
        TuiTheme? theme = null;
        bool showHelp = false;
        bool fakeData = FakeDataEnvironment.IsRequested([]);
        string? fakeDataDirectory = null;
        bool updateApps = false;
        string[] settingsArgs = [];
        int settingsPriority = int.MaxValue;
        List<string> bundles = [];
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == PageFlag)
            {
                if (i + 1 >= args.Length) throw new ArgumentException("--page needs a page id.");
                page = args[++i].ToLowerInvariant();
                if (!TuiPageIds.All.Contains(page))
                    throw new ArgumentException($"Unknown page \"{page}\". Valid pages: {string.Join(", ", TuiPageIds.All)}.");
            }
            else if (arg == ThemeFlag)
            {
                if (i + 1 >= args.Length) throw new ArgumentException("--theme needs a theme id.");
                string id = args[++i];
                theme = TuiThemes.Find(id) ?? throw new ArgumentException(
                    $"Unknown theme \"{id}\". Valid themes: {string.Join(", ", TuiThemes.All.Select(t => t.Id))}.");
            }
            else if (ValueCount(arg) is > 0 and int count)
            {
                if (i + count >= args.Length)
                    throw new ArgumentException($"{arg} needs {count} value(s).");
                if (arg == FakeDataEnvironment.DirectoryFlag)
                {
                    string directory = args[i + 1];
                    if (string.IsNullOrWhiteSpace(directory) || directory.StartsWith('-'))
                        throw new ArgumentException("--fake-data-dir needs a directory path (use ./ or .\\ for a path starting with '-').");
                    fakeData = true;
                    fakeDataDirectory ??= directory;
                }
                else
                {
                    // Preserve desktop command precedence without treating values as commands.
                    int priority = Array.IndexOf(SettingsCommands, arg);
                    if (priority < settingsPriority)
                    {
                        settingsPriority = priority;
                        settingsArgs = args[i..(i + count + 1)];
                    }
                }
                i += count;
            }
            else if (arg is "--help" or "-h")
            {
                showHelp = true;
            }
            else if (arg == FakeDataEnvironment.Flag)
            {
                fakeData = true;
            }
            else if (arg == UpdateAppsFlag)
            {
                updateApps = true;
            }
            else if (!arg.StartsWith('-') && BundleExtensions.Contains(Path.GetExtension(arg), StringComparer.OrdinalIgnoreCase))
            {
                string full = Path.GetFullPath(arg);
                if (!File.Exists(full)) throw new ArgumentException($"Bundle file not found: {full}");
                bundles.Add(full);
            }
            else
            {
                throw new ArgumentException($"Unknown terminal argument \"{arg}\".");
            }
        }

        return new TuiCommandLine(settingsArgs)
        {
            ShowHelp = showHelp,
            FakeData = fakeData,
            FakeDataDirectory = fakeDataDirectory,
            StartupPage = page,
            UpdateAppsOnStart = updateApps,
            Theme = theme,
            BundleFiles = bundles,
        };
    }

    /// <summary>How many values follow a flag (so a value such as "settings.json" is not read as a bundle).</summary>
    private static int ValueCount(string flag) => flag switch
    {
        FakeDataEnvironment.DirectoryFlag => 1,
        SharedPreUiCommandDispatcher.ImportSettingsArgument or SharedPreUiCommandDispatcher.ExportSettingsArgument => 1,
        SharedPreUiCommandDispatcher.EnableSettingArgument or SharedPreUiCommandDispatcher.DisableSettingArgument => 1,
        SharedPreUiCommandDispatcher.EnableSecureSettingArgument or SharedPreUiCommandDispatcher.DisableSecureSettingArgument => 1,
        SharedPreUiCommandDispatcher.SetSettingValueArgument => 2,
        UniGetUI.Core.SettingsEngine.SecureSettings.SecureSettings.Args.ENABLE_FOR_USER
            or UniGetUI.Core.SettingsEngine.SecureSettings.SecureSettings.Args.DISABLE_FOR_USER => 2,
        _ => 0,
    };

    /// <summary>Runs a settings-only command, if one was given. Returns false to continue into the UI.</summary>
    public bool TryRunHeadlessCommand(out int exitCode)
    {
        exitCode = 0;
        if (!HasHeadlessCommand) return false;
        var codes = SharedPreUiCommandDispatcher.PortableCliExitCodes;
        exitCode = _args[0] switch
        {
            SharedPreUiCommandDispatcher.ImportSettingsArgument => SharedPreUiCommandDispatcher.ImportSettings(_args, codes),
            SharedPreUiCommandDispatcher.ExportSettingsArgument => SharedPreUiCommandDispatcher.ExportSettings(_args, codes),
            SharedPreUiCommandDispatcher.EnableSettingArgument => SharedPreUiCommandDispatcher.EnableSetting(_args, codes),
            SharedPreUiCommandDispatcher.DisableSettingArgument => SharedPreUiCommandDispatcher.DisableSetting(_args, codes),
            SharedPreUiCommandDispatcher.SetSettingValueArgument => SharedPreUiCommandDispatcher.SetSettingValue(_args, codes),
            SharedPreUiCommandDispatcher.EnableSecureSettingArgument => SharedPreUiCommandDispatcher.EnableSecureSetting(_args, codes),
            SharedPreUiCommandDispatcher.DisableSecureSettingArgument => SharedPreUiCommandDispatcher.DisableSecureSetting(_args, codes),
            UniGetUI.Core.SettingsEngine.SecureSettings.SecureSettings.Args.ENABLE_FOR_USER =>
                SharedPreUiCommandDispatcher.EnableSecureSettingForUser(_args, codes),
            UniGetUI.Core.SettingsEngine.SecureSettings.SecureSettings.Args.DISABLE_FOR_USER =>
                SharedPreUiCommandDispatcher.DisableSecureSettingForUser(_args, codes),
            _ => throw new InvalidOperationException("Unrecognized parsed settings command."),
        };
        Console.WriteLine(exitCode == 0 ? "Done." : $"Failed with exit code {exitCode}.");
        return true;
    }
}

/// <summary>What the process was started with, for the UI to act on once it is up.</summary>
internal static class TuiStartup
{
    public static TuiCommandLine? Current { get; set; }
}
