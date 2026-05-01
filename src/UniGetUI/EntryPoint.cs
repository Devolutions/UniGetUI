using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI
{
    public static class EntryPoint
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // Having an async main method breaks WebView2
            try
            {
                if (args.Contains(CLIHandler.HELP))
                {
                    CLIHandler.Help();
                    Environment.Exit(0);
                }
                else if (args.Contains(CLIHandler.MIGRATE_WINGETUI_TO_UNIGETUI))
                {
                    int ret = CLIHandler.WingetUIToUniGetUIMigrator();
                    Environment.Exit(ret);
                }
                else if (
                    args.Contains(CLIHandler.UNINSTALL_UNIGETUI)
                    || args.Contains(CLIHandler.UNINSTALL_WINGETUI)
                )
                {
                    int ret = CLIHandler.UninstallUniGetUI();
                    Environment.Exit(ret);
                }
                else if (args.Contains(CLIHandler.IMPORT_SETTINGS))
                {
                    int ret = CLIHandler.ImportSettings();
                    Environment.Exit(ret);
                }
                else if (args.Contains(CLIHandler.EXPORT_SETTINGS))
                {
                    int ret = CLIHandler.ExportSettings();
                    Environment.Exit(ret);
                }
                else if (args.Contains(CLIHandler.ENABLE_SETTING))
                {
                    int ret = CLIHandler.EnableSetting();
                    Environment.Exit(ret);
                }
                else if (args.Contains(CLIHandler.DISABLE_SETTING))
                {
                    int ret = CLIHandler.DisableSetting();
                    Environment.Exit(ret);
                }
                else if (args.Contains(CLIHandler.SET_SETTING_VAL))
                {
                    int ret = CLIHandler.SetSettingsValue();
                    Environment.Exit(ret);
                }
                else if (args.Contains(CLIHandler.ENABLE_SECURE_SETTING))
                {
                    int ret = CLIHandler.EnableSecureSetting();
                    Environment.Exit(ret);
                }
                else if (args.Contains(CLIHandler.DISABLE_SECURE_SETTING))
                {
                    int ret = CLIHandler.DisableSecureSetting();
                    Environment.Exit(ret);
                }
                else if (args.Contains(CLIHandler.ENABLE_SECURE_SETTING_FOR_USER))
                {
                    int ret = CLIHandler.EnableSecureSettingForUser();
                    Environment.Exit(ret);
                }
                else if (args.Contains(CLIHandler.DISABLE_SECURE_SETTING_FOR_USER))
                {
                    int ret = CLIHandler.DisableSecureSettingForUser();
                    Environment.Exit(ret);
                }
                else if (!ModernAppLauncher.IsClassicModeEnabled())
                {
                    ModernAppLauncher.Launch(args);
                }
                else
                {
                    CoreData.WasDaemon = CoreData.IsDaemon = args.Contains(CLIHandler.DAEMON);
                    _ = AsyncMain();
                }
            }
            catch (Exception e)
            {
                CrashHandler.ReportFatalException(e);
            }
        }

        /// <summary>
        /// UniGetUI app main entry point
        /// </summary>
        private static async Task AsyncMain()
        {
            try
            {
                string textart = $"""
                       __  __      _ ______     __  __  ______
                      / / / /___  (_) ____/__  / /_/ / / /  _/
                     / / / / __ \/ / / __/ _ \/ __/ / / // /
                    / /_/ / / / / / /_/ /  __/ /_/ /_/ // /
                    \____/_/ /_/_/\____/\___/\__/\____/___/
                        Welcome to UniGetUI Version {CoreData.VersionName}
                    """;

                Logger.ImportantInfo(textart);
                Logger.ImportantInfo("  ");
                Logger.ImportantInfo($"Build {CoreData.BuildNumber}");
                Logger.ImportantInfo($"Data directory {CoreData.UniGetUIDataDirectory}");
                Logger.ImportantInfo($"Encoding Code Page set to {CoreData.CODE_PAGE}");

                // WinRT single-instance fancy stuff
                WinRT.ComWrappersSupport.InitializeComWrappers();
                bool isRedirect = await DecideRedirection();

                // If this is the main instance, start the app
                if (!isRedirect)
                {
                    Application.Start(
                        (_) =>
                        {
                            DispatcherQueueSynchronizationContext context = new(
                                DispatcherQueue.GetForCurrentThread()
                            );
                            SynchronizationContext.SetSynchronizationContext(context);
                            var app = new MainApp();
                        }
                    );
                }
            }
            catch (Exception e)
            {
                CrashHandler.ReportFatalException(e);
            }
        }

        /// <summary>
        /// Default WinUI Redirector
        /// </summary>
        private static async Task<bool> DecideRedirection()
        {
            try
            {
                // IDK how does this work, I copied it from the MS Docs
                // example on single-instance apps using unpackaged AppSdk + WinUI3
                bool isRedirect = false;

                var keyInstance = AppInstance.FindOrRegisterForKey(CoreData.MainWindowIdentifier);
                if (keyInstance.IsCurrent)
                {
                    keyInstance.Activated += async (_, e) =>
                    {
                        if (Application.Current is MainApp baseInstance)
                        {
                            await baseInstance.ShowMainWindowFromRedirectAsync(e);
                        }
                    };
                }
                else
                {
                    isRedirect = true;
                    AppActivationArguments args = AppInstance.GetCurrent().GetActivatedEventArgs();
                    await keyInstance.RedirectActivationToAsync(args);
                }
                return isRedirect;
            }
            catch (Exception e)
            {
                Logger.Warn(e);
                return false;
            }
        }
    }
}
