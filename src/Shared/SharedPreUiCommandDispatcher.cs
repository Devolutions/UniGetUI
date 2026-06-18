using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;

namespace UniGetUI.Shared;

internal readonly record struct SharedPreUiCommandExitCodes(
    int Success,
    int Failed,
    int InvalidParameter,
    int NoSuchFile,
    int UnknownSettingsKey
);

internal static class SharedPreUiCommandDispatcher
{
    internal static readonly SharedPreUiCommandExitCodes WindowsCliExitCodes = new(
        Success: 0,
        Failed: -1,
        InvalidParameter: -1073741811,
        NoSuchFile: -1073741809,
        UnknownSettingsKey: -2
    );

    internal static readonly SharedPreUiCommandExitCodes PortableCliExitCodes = new(
        Success: 0,
        Failed: 1,
        InvalidParameter: 2,
        NoSuchFile: 3,
        UnknownSettingsKey: 4
    );

    internal const string HelpArgument = "--help";
    internal const string ImportSettingsArgument = "--import-settings";
    internal const string ExportSettingsArgument = "--export-settings";
    internal const string EnableSettingArgument = "--enable-setting";
    internal const string DisableSettingArgument = "--disable-setting";
    internal const string SetSettingValueArgument = "--set-setting-value";
    internal const string EnableSecureSettingArgument = "--enable-secure-setting";
    internal const string DisableSecureSettingArgument = "--disable-secure-setting";
    internal const string MigrateWingetUIToUniGetUIArgument = "--migrate-wingetui-to-unigetui";
    internal const string UninstallWingetUIArgument = "--uninstall-wingetui";
    internal const string UninstallUniGetUIArgument = "--uninstall-unigetui";

    private const string EnableSecureSettingForUserArgument = SecureSettings.Args.ENABLE_FOR_USER;
    private const string DisableSecureSettingForUserArgument = SecureSettings.Args.DISABLE_FOR_USER;

    public static int? TryHandle(IReadOnlyList<string> args, SharedPreUiCommandExitCodes exitCodes)
    {
        if (args.Contains(HelpArgument))
        {
            return Help();
        }

        if (args.Contains(ImportSettingsArgument))
        {
            return ImportSettings(args, exitCodes);
        }

        if (args.Contains(ExportSettingsArgument))
        {
            return ExportSettings(args, exitCodes);
        }

        if (args.Contains(EnableSettingArgument))
        {
            return EnableSetting(args, exitCodes);
        }

        if (args.Contains(DisableSettingArgument))
        {
            return DisableSetting(args, exitCodes);
        }

        if (args.Contains(SetSettingValueArgument))
        {
            return SetSettingValue(args, exitCodes);
        }

        if (args.Contains(EnableSecureSettingArgument))
        {
            return EnableSecureSetting(args, exitCodes);
        }

        if (args.Contains(DisableSecureSettingArgument))
        {
            return DisableSecureSetting(args, exitCodes);
        }

        if (args.Contains(EnableSecureSettingForUserArgument))
        {
            return EnableSecureSettingForUser(args, exitCodes);
        }

        if (args.Contains(DisableSecureSettingForUserArgument))
        {
            return DisableSecureSettingForUser(args, exitCodes);
        }

        if (args.Contains(MigrateWingetUIToUniGetUIArgument))
        {
            return MigrateWingetUIToUniGetUI();
        }

        if (args.Contains(UninstallWingetUIArgument) || args.Contains(UninstallUniGetUIArgument))
        {
            return exitCodes.Success;
        }

        return null;
    }

    public static int Help()
    {
        CoreTools.Launch(
            "https://github.com/Devolutions/UniGetUI/blob/main/docs/CLI.md#unigetui-command-line-interface"
        );
        return 0;
    }

    public static int ImportSettings(IReadOnlyList<string> args, SharedPreUiCommandExitCodes exitCodes)
    {
        if (!TryGetValueAfterArgument(args, ImportSettingsArgument, 1, out string file))
        {
            return exitCodes.InvalidParameter;
        }

        if (!File.Exists(file))
        {
            return exitCodes.NoSuchFile;
        }

        try
        {
            Settings.ImportFromFile_JSON(file);
            return exitCodes.Success;
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    public static int ExportSettings(IReadOnlyList<string> args, SharedPreUiCommandExitCodes exitCodes)
    {
        if (!TryGetValueAfterArgument(args, ExportSettingsArgument, 1, out string file))
        {
            return exitCodes.InvalidParameter;
        }

        try
        {
            Settings.ExportToFile_JSON(file);
            return exitCodes.Success;
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    public static int EnableSetting(IReadOnlyList<string> args, SharedPreUiCommandExitCodes exitCodes)
    {
        if (!TryGetEnumArgument(args, EnableSettingArgument, out Settings.K validKey))
        {
            return GetEnumArgumentErrorCode(args, EnableSettingArgument, exitCodes);
        }

        try
        {
            Settings.Set(validKey, true);
            return exitCodes.Success;
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    public static int DisableSetting(IReadOnlyList<string> args, SharedPreUiCommandExitCodes exitCodes)
    {
        if (!TryGetEnumArgument(args, DisableSettingArgument, out Settings.K validKey))
        {
            return GetEnumArgumentErrorCode(args, DisableSettingArgument, exitCodes);
        }

        try
        {
            Settings.Set(validKey, false);
            return exitCodes.Success;
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    public static int SetSettingValue(IReadOnlyList<string> args, SharedPreUiCommandExitCodes exitCodes)
    {
        if (!TryGetValueAfterArgument(args, SetSettingValueArgument, 1, out string setting))
        {
            return exitCodes.InvalidParameter;
        }

        if (!TryGetValueAfterArgument(args, SetSettingValueArgument, 2, out string value))
        {
            return exitCodes.InvalidParameter;
        }

        if (!Enum.TryParse(setting, out Settings.K validKey))
        {
            return exitCodes.UnknownSettingsKey;
        }

        try
        {
            Settings.SetValue(validKey, value);
            return exitCodes.Success;
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    public static int EnableSecureSetting(IReadOnlyList<string> args, SharedPreUiCommandExitCodes exitCodes)
    {
        if (!TryGetEnumArgument(args, EnableSecureSettingArgument, out SecureSettings.K validKey))
        {
            return GetEnumArgumentErrorCode(args, EnableSecureSettingArgument, exitCodes);
        }

        try
        {
            bool success = SecureSettings.TrySet(validKey, true).GetAwaiter().GetResult();
            return success ? exitCodes.Success : exitCodes.Failed;
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    public static int DisableSecureSetting(IReadOnlyList<string> args, SharedPreUiCommandExitCodes exitCodes)
    {
        if (!TryGetEnumArgument(args, DisableSecureSettingArgument, out SecureSettings.K validKey))
        {
            return GetEnumArgumentErrorCode(args, DisableSecureSettingArgument, exitCodes);
        }

        try
        {
            bool success = SecureSettings.TrySet(validKey, false).GetAwaiter().GetResult();
            return success ? exitCodes.Success : exitCodes.Failed;
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    public static int EnableSecureSettingForUser(IReadOnlyList<string> args, SharedPreUiCommandExitCodes exitCodes)
    {
        if (!TryGetValueAfterArgument(args, EnableSecureSettingForUserArgument, 1, out string user))
        {
            return exitCodes.InvalidParameter;
        }

        if (!TryGetValueAfterArgument(args, EnableSecureSettingForUserArgument, 2, out string setting))
        {
            return exitCodes.InvalidParameter;
        }

        try
        {
            return SecureSettings.ApplyForUser(user, setting, true);
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    public static int DisableSecureSettingForUser(IReadOnlyList<string> args, SharedPreUiCommandExitCodes exitCodes)
    {
        if (!TryGetValueAfterArgument(args, DisableSecureSettingForUserArgument, 1, out string user))
        {
            return exitCodes.InvalidParameter;
        }

        if (!TryGetValueAfterArgument(args, DisableSecureSettingForUserArgument, 2, out string setting))
        {
            return exitCodes.InvalidParameter;
        }

        try
        {
            return SecureSettings.ApplyForUser(user, setting, false);
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    public static int MigrateWingetUIToUniGetUI()
    {
        try
        {
            string[] basePaths =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            ];

            foreach (string path in basePaths)
            {
                foreach (
                    string oldWingetUIIcon in new[]
                    {
                        "WingetUI.lnk",
                        "WingetUI .lnk",
                        "UniGetUI (formerly WingetUI) .lnk",
                        "UniGetUI (formerly WingetUI).lnk",
                    }
                )
                {
                    try
                    {
                        string oldFile = Path.Join(path, oldWingetUIIcon);
                        string newFile = Path.Join(path, "UniGetUI.lnk");
                        if (!File.Exists(oldFile))
                        {
                            continue;
                        }

                        if (File.Exists(newFile))
                        {
                            Logger.Info(
                                "Deleting shortcut "
                                    + oldFile
                                    + " since new shortcut already exists"
                            );
                            File.Delete(oldFile);
                        }
                        else
                        {
                            Logger.Info("Moving shortcut to " + newFile);
                            File.Move(oldFile, newFile);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(
                            $"An error occurred while migrating the shortcut {Path.Join(path, oldWingetUIIcon)}"
                        );
                        Logger.Warn(ex);
                    }
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return ex.HResult;
        }
    }

    private static bool TryGetValueAfterArgument(
        IReadOnlyList<string> args,
        string argument,
        int offset,
        out string value
    )
    {
        int basePos = FindArgumentIndex(args, argument);
        if (basePos < 0 || basePos + offset >= args.Count)
        {
            value = string.Empty;
            return false;
        }

        value = args[basePos + offset].Trim('"').Trim('\'');
        return true;
    }

    private static bool TryGetEnumArgument<TEnum>(
        IReadOnlyList<string> args,
        string argument,
        out TEnum value
    ) where TEnum : struct
    {
        if (!TryGetValueAfterArgument(args, argument, 1, out string rawValue))
        {
            value = default;
            return false;
        }

        return Enum.TryParse(rawValue, out value);
    }

    private static int GetEnumArgumentErrorCode(
        IReadOnlyList<string> args,
        string argument,
        SharedPreUiCommandExitCodes exitCodes
    )
    {
        return TryGetValueAfterArgument(args, argument, 1, out _) ? exitCodes.UnknownSettingsKey : exitCodes.InvalidParameter;
    }

    private static int FindArgumentIndex(IReadOnlyList<string> args, string argument)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], argument, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
