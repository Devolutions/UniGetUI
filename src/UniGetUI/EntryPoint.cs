using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface;
using UniGetUI.Shared;

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
                if (ShouldPrepareCliConsole(args))
                {
                    WindowsConsoleHost.PrepareCliIO();
                }

                if (SharedPreUiCommandDispatcher.TryHandle(args, SharedPreUiCommandDispatcher.WinUiExitCodes) is { } preUiExitCode)
                {
                    Environment.ExitCode = preUiExitCode;
                    return;
                }
                else if (args.Contains(CLIHandler.MIGRATE_WINGETUI_TO_UNIGETUI))
                {
                    Environment.ExitCode = CLIHandler.WingetUIToUniGetUIMigrator();
                    return;
                }
                else if (
                    args.Contains(CLIHandler.UNINSTALL_UNIGETUI)
                    || args.Contains(CLIHandler.UNINSTALL_WINGETUI)
                )
                {
                    Environment.ExitCode = CLIHandler.UninstallUniGetUI();
                    return;
                }
                else if (IpcCliSyntax.IsIpcCommand(args))
                {
                    Environment.ExitCode = CLIHandler.Automation(args);
                    return;
                }
                else if (args.Contains(CLIHandler.HEADLESS))
                {
                    Environment.ExitCode = WinUiHeadlessHost.RunAsync(args).GetAwaiter().GetResult();
                    return;
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

        private static bool ShouldPrepareCliConsole(IReadOnlyList<string> args)
        {
            return IpcCliSyntax.HasVerbCommand(args);
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
                Logger.ImportantInfo("UI Framework: WinUI 3");
                Logger.ImportantInfo($"Data directory {CoreData.UniGetUIDataDirectory}");
                Logger.ImportantInfo($"Encoding Code Page set to {CoreData.CODE_PAGE}");
                Logger.ImportantInfo($"OS: {RuntimeInformation.OSDescription}");
                Logger.ImportantInfo($"Process arch: {RuntimeInformation.ProcessArchitecture} (OS: {RuntimeInformation.OSArchitecture})");
                Logger.ImportantInfo($"Runtime: {RuntimeInformation.FrameworkDescription}");
                Logger.ImportantInfo($"Elevated: {CoreTools.IsAdministrator()}");
                Logger.ImportantInfo($"Packaged (MSIX): {CoreTools.IsPackagedApp()}");
                string[] cmdArgs = Environment.GetCommandLineArgs();
                Logger.ImportantInfo($"Args: {(cmdArgs.Length > 1 ? string.Join(" ", cmdArgs.Skip(1)) : "(none)")}");

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
