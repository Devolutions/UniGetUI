using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.Interface;
using UniGetUI.Shared;

namespace UniGetUI;

public static class CLIHandler
{
    public const string HELP = "--help";
    public const string DAEMON = "--daemon";
    public const string MIGRATE_WINGETUI_TO_UNIGETUI = "--migrate-wingetui-to-unigetui";
    public const string UNINSTALL_WINGETUI = "--uninstall-wingetui";
    public const string UNINSTALL_UNIGETUI = "--uninstall-unigetui";
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
    public const string HEADLESS = "--headless";

    private enum HRESULT
    {
        SUCCESS = 0,
        STATUS_FAILED = -1,
        STATUS_INVALID_PARAMETER = -1073741811,
        STATUS_NO_SUCH_FILE = -1073741809,
        STATUS_UNKNOWN__SETTINGS_KEY = -2,
        STATUS_BACKGROUND_API_UNAVAILABLE = -3,
        STATUS_UNKNOWN_AUTOMATION_COMMAND = -4,
    }

    public static int Help()
    {
        return SharedPreUiCommandDispatcher.Help();
    }

    public static int ImportSettings()
    {
        return ImportSettings(Environment.GetCommandLineArgs());
    }

    internal static int ImportSettings(IReadOnlyList<string> args)
    {
        return SharedPreUiCommandDispatcher.ImportSettings(
            args,
            SharedPreUiCommandDispatcher.WinUiExitCodes
        );
    }

    public static int ExportSettings()
    {
        return ExportSettings(Environment.GetCommandLineArgs());
    }

    internal static int ExportSettings(IReadOnlyList<string> args)
    {
        return SharedPreUiCommandDispatcher.ExportSettings(
            args,
            SharedPreUiCommandDispatcher.WinUiExitCodes
        );
    }

    public static int EnableSetting()
    {
        return EnableSetting(Environment.GetCommandLineArgs());
    }

    internal static int EnableSetting(IReadOnlyList<string> args)
    {
        return SharedPreUiCommandDispatcher.EnableSetting(
            args,
            SharedPreUiCommandDispatcher.WinUiExitCodes
        );
    }

    public static int DisableSetting()
    {
        return DisableSetting(Environment.GetCommandLineArgs());
    }

    internal static int DisableSetting(IReadOnlyList<string> args)
    {
        return SharedPreUiCommandDispatcher.DisableSetting(
            args,
            SharedPreUiCommandDispatcher.WinUiExitCodes
        );
    }

    public static int SetSettingsValue()
    {
        return SetSettingsValue(Environment.GetCommandLineArgs());
    }

    internal static int SetSettingsValue(IReadOnlyList<string> args)
    {
        return SharedPreUiCommandDispatcher.SetSettingValue(
            args,
            SharedPreUiCommandDispatcher.WinUiExitCodes
        );
    }

    public static int WingetUIToUniGetUIMigrator()
    {
        try
        {
            string[] BasePaths =
            [
                // User desktop icon
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                // User start menu icon
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                // Common desktop icon
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                // User start menu icon
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            ];

            foreach (string path in BasePaths)
            {
                foreach (
                    string old_wingetui_icon in new[]
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
                        string old_file = Path.Join(path, old_wingetui_icon);
                        string new_file = Path.Join(path, "UniGetUI.lnk");
                        if (!File.Exists(old_file))
                        {
                            continue;
                        }

                        if (File.Exists(old_file) && File.Exists(new_file))
                        {
                            Logger.Info(
                                "Deleting shortcut "
                                    + old_file
                                    + " since new shortcut already exists"
                            );
                            File.Delete(old_file);
                        }
                        else if (File.Exists(old_file) && !File.Exists(new_file))
                        {
                            Logger.Info("Moving shortcut to " + new_file);
                            File.Move(old_file, new_file);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(
                            $"An error occurred while migrating the shortcut {Path.Join(path, old_wingetui_icon)}"
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

    public static int UninstallUniGetUI()
    {
        // There is currently no uninstall logic. However, this needs to be maintained, or otherwhise UniGetUI will launch on uninstall
        return 0;
    }

    public static int EnableSecureSetting()
    {
        return EnableSecureSetting(Environment.GetCommandLineArgs());
    }

    internal static int EnableSecureSetting(IReadOnlyList<string> args)
    {
        return SharedPreUiCommandDispatcher.EnableSecureSetting(
            args,
            SharedPreUiCommandDispatcher.WinUiExitCodes
        );
    }

    public static int DisableSecureSetting()
    {
        return DisableSecureSetting(Environment.GetCommandLineArgs());
    }

    internal static int DisableSecureSetting(IReadOnlyList<string> args)
    {
        return SharedPreUiCommandDispatcher.DisableSecureSetting(
            args,
            SharedPreUiCommandDispatcher.WinUiExitCodes
        );
    }

    public static int EnableSecureSettingForUser()
    {
        return EnableSecureSettingForUser(Environment.GetCommandLineArgs());
    }

    internal static int EnableSecureSettingForUser(IReadOnlyList<string> args)
    {
        return SharedPreUiCommandDispatcher.EnableSecureSettingForUser(
            args,
            SharedPreUiCommandDispatcher.WinUiExitCodes
        );
    }

    public static int DisableSecureSettingForUser()
    {
        return DisableSecureSettingForUser(Environment.GetCommandLineArgs());
    }

    internal static int DisableSecureSettingForUser(IReadOnlyList<string> args)
    {
        return SharedPreUiCommandDispatcher.DisableSecureSettingForUser(
            args,
            SharedPreUiCommandDispatcher.WinUiExitCodes
        );
    }

    public static int Automation()
    {
        return Automation(Environment.GetCommandLineArgs());
    }

    internal static int Automation(IReadOnlyList<string> args)
    {
        return IpcCliCommandRunner.RunAsync(args, Console.Out, Console.Error)
            .GetAwaiter()
            .GetResult();
    }
}
