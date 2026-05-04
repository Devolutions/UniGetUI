using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia;

/// <summary>
/// Pre-UI CLI argument handler. Mirrors WinUI's CLIHandler.
/// Methods that return a non-null exit code should cause the process to exit
/// without launching the Avalonia app.
/// </summary>
internal static class AvaloniaCliHandler
{
    public const string HELP = "--help";
    public const string DAEMON = "--daemon";
    public const string NO_CORRUPT_DIALOG = "--no-corrupt-dialog";

    public const string IMPORT_SETTINGS = "--import-settings";
    public const string EXPORT_SETTINGS = "--export-settings";

    public const string ENABLE_SETTING = "--enable-setting";
    public const string DISABLE_SETTING = "--disable-setting";
    public const string SET_SETTING_VAL = "--set-setting-value";

    public const string ENABLE_SECURE_SETTING = "--enable-secure-setting";
    public const string DISABLE_SECURE_SETTING = "--disable-secure-setting";
    public const string ENABLE_SECURE_SETTING_FOR_USER = SecureSettings.Args.ENABLE_FOR_USER;
    public const string DISABLE_SECURE_SETTING_FOR_USER = SecureSettings.Args.DISABLE_FOR_USER;

    private enum ExitCode
    {
        Success = 0,
        Failed = 1,
        InvalidParameter = 2,
        NoSuchFile = 3,
        UnknownSettingsKey = 4,
    }

    /// <summary>
    /// Inspect <paramref name="args"/> and, for recognised pre-UI arguments,
    /// execute the requested action and return the desired process exit code.
    /// Returns null when no pre-UI argument was found and the app should start normally.
    /// </summary>
    public static int? HandlePreUiArgs(string[] args)
    {
        if (args.Contains(HELP))
        {
            CoreTools.Launch("https://github.com/Devolutions/UniGetUI/blob/main/docs/CLI.md#unigetui-command-line-interface");
            return (int)ExitCode.Success;
        }

        if (args.Contains(IMPORT_SETTINGS))
            return ImportSettings(args);

        if (args.Contains(EXPORT_SETTINGS))
            return ExportSettings(args);

        if (args.Contains(ENABLE_SETTING))
            return EnableSetting(args);

        if (args.Contains(DISABLE_SETTING))
            return DisableSetting(args);

        if (args.Contains(SET_SETTING_VAL))
            return SetSettingsValue(args);

        if (args.Contains(ENABLE_SECURE_SETTING))
            return EnableSecureSetting(args);

        if (args.Contains(DISABLE_SECURE_SETTING))
            return DisableSecureSetting(args);

        if (args.Contains(ENABLE_SECURE_SETTING_FOR_USER))
            return EnableSecureSettingForUser(args);

        if (args.Contains(DISABLE_SECURE_SETTING_FOR_USER))
            return DisableSecureSettingForUser(args);

        return null;
    }

    private static int ImportSettings(string[] args)
    {
        int idx = Array.IndexOf(args, IMPORT_SETTINGS);
        if (idx < 0 || idx + 1 >= args.Length)
            return (int)ExitCode.InvalidParameter;

        var file = args[idx + 1].Trim('"').Trim('\'');
        if (!File.Exists(file))
            return (int)ExitCode.NoSuchFile;

        try
        {
            Settings.ImportFromFile_JSON(file);
            return (int)ExitCode.Success;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return ex.HResult;
        }
    }

    private static int ExportSettings(string[] args)
    {
        int idx = Array.IndexOf(args, EXPORT_SETTINGS);
        if (idx < 0 || idx + 1 >= args.Length)
            return (int)ExitCode.InvalidParameter;

        var file = args[idx + 1].Trim('"').Trim('\'');
        try
        {
            Settings.ExportToFile_JSON(file);
            return (int)ExitCode.Success;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return ex.HResult;
        }
    }

    private static int EnableSetting(string[] args)
    {
        int idx = Array.IndexOf(args, ENABLE_SETTING);
        if (idx < 0 || idx + 1 >= args.Length)
            return (int)ExitCode.InvalidParameter;

        if (!Enum.TryParse(args[idx + 1].Trim('"').Trim('\''), out Settings.K key))
            return (int)ExitCode.UnknownSettingsKey;

        try { Settings.Set(key, true); return (int)ExitCode.Success; }
        catch (Exception ex) { return ex.HResult; }
    }

    private static int DisableSetting(string[] args)
    {
        int idx = Array.IndexOf(args, DISABLE_SETTING);
        if (idx < 0 || idx + 1 >= args.Length)
            return (int)ExitCode.InvalidParameter;

        if (!Enum.TryParse(args[idx + 1].Trim('"').Trim('\''), out Settings.K key))
            return (int)ExitCode.UnknownSettingsKey;

        try { Settings.Set(key, false); return (int)ExitCode.Success; }
        catch (Exception ex) { return ex.HResult; }
    }

    private static int SetSettingsValue(string[] args)
    {
        int idx = Array.IndexOf(args, SET_SETTING_VAL);
        if (idx < 0 || idx + 2 >= args.Length)
            return (int)ExitCode.InvalidParameter;

        if (!Enum.TryParse(args[idx + 1].Trim('"').Trim('\''), out Settings.K key))
            return (int)ExitCode.UnknownSettingsKey;

        try { Settings.SetValue(key, args[idx + 2]); return (int)ExitCode.Success; }
        catch (Exception ex) { return ex.HResult; }
    }

    private static int EnableSecureSetting(string[] args)
    {
        int idx = Array.IndexOf(args, ENABLE_SECURE_SETTING);
        if (idx < 0 || idx + 1 >= args.Length)
            return (int)ExitCode.InvalidParameter;

        if (!Enum.TryParse(args[idx + 1].Trim('"').Trim('\''), out SecureSettings.K key))
            return (int)ExitCode.UnknownSettingsKey;

        try
        {
            bool ok = SecureSettings.TrySet(key, true).GetAwaiter().GetResult();
            return ok ? (int)ExitCode.Success : (int)ExitCode.Failed;
        }
        catch (Exception ex) { return ex.HResult; }
    }

    private static int DisableSecureSetting(string[] args)
    {
        int idx = Array.IndexOf(args, DISABLE_SECURE_SETTING);
        if (idx < 0 || idx + 1 >= args.Length)
            return (int)ExitCode.InvalidParameter;

        if (!Enum.TryParse(args[idx + 1].Trim('"').Trim('\''), out SecureSettings.K key))
            return (int)ExitCode.UnknownSettingsKey;

        try
        {
            bool ok = SecureSettings.TrySet(key, false).GetAwaiter().GetResult();
            return ok ? (int)ExitCode.Success : (int)ExitCode.Failed;
        }
        catch (Exception ex) { return ex.HResult; }
    }

    private static int EnableSecureSettingForUser(string[] args)
    {
        int idx = Array.IndexOf(args, ENABLE_SECURE_SETTING_FOR_USER);
        if (idx < 0 || idx + 2 >= args.Length)
            return (int)ExitCode.InvalidParameter;

        var user = args[idx + 1].Trim('"').Trim('\'');
        var setting = args[idx + 2].Trim('"').Trim('\'');
        try { return SecureSettings.ApplyForUser(user, setting, true); }
        catch (Exception ex) { return ex.HResult; }
    }

    private static int DisableSecureSettingForUser(string[] args)
    {
        int idx = Array.IndexOf(args, DISABLE_SECURE_SETTING_FOR_USER);
        if (idx < 0 || idx + 2 >= args.Length)
            return (int)ExitCode.InvalidParameter;

        var user = args[idx + 1].Trim('"').Trim('\'');
        var setting = args[idx + 2].Trim('"').Trim('\'');
        try { return SecureSettings.ApplyForUser(user, setting, false); }
        catch (Exception ex) { return ex.HResult; }
    }
}
