using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
#if AVALONIA_DIAGNOSTICS_ENABLED
using Avalonia.Diagnostics;
#endif
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia;

public partial class App : Application
{
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Platform theme dictionaries are Avalonia resources included in the app package; only the resource URI is selected dynamically.")]
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if AVALONIA_DIAGNOSTICS_ENABLED
        this.AttachDeveloperTools();
#endif

        string platform = OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsMacOS() ? "macOS"
            : "Linux";

        Styles.Add(new StyleInclude(new Uri("avares://UniGetUI.Avalonia/"))
        {
            Source = new Uri($"avares://UniGetUI.Avalonia/Assets/Styles/Styles.{platform}.axaml")
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (OperatingSystem.IsWindows())
        {
            // Redirect WebView2 user-data folder to a writable temp location.
            // Without this, WebView2 tries to write next to the executable in
            // C:\Program Files\, which is read-only for non-admin users and
            // causes UnauthorizedAccessException (E_ACCESSDENIED) on startup.
            SetUpWebViewUserDataFolder();

            // Safety net for NativeWebView (WebView2) initialization failures thrown
            // asynchronously on the dispatcher. Without this the app crashes; with it
            // the Help page shows a fallback "Open in browser" button.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                if (e.Exception is InvalidOperationException { Message: var msg }
                    && msg.Contains("child window for native control host"))
                {
                    e.Handled = true;
                }
            };
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (OperatingSystem.IsMacOS())
            {
                ProcessEnvironmentConfigurator.PrepareForCurrentPlatform();
                using var stream = AssetLoader.Open(new Uri("avares://UniGetUI.Avalonia/Assets/icon.png"));
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                MacOsNotificationBridge.SetDockIcon(ms.ToArray());
            }
            else
            {
                ProcessEnvironmentConfigurator.ApplyProxySettingsToProcess();
            }
            PEInterface.LoadLoaders();
            ApplyTheme(CoreSettings.GetValue(CoreSettings.K.PreferredTheme));
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            AvaloniaAppHost.SecondaryInstanceArgsReceived += args =>
                HandleSecondaryInstanceArgs(mainWindow, args);

            if (CoreData.WasDaemon)
            {
                // Start silently: hide the window on first open only.
                // Opened fires on every Show() in Avalonia, so we must unsubscribe
                // immediately or every ShowFromTray() call would hide the window again.
                void HideOnce(object? s, EventArgs e)
                {
                    mainWindow.Opened -= HideOnce;
                    mainWindow.Hide();
                }
                mainWindow.Opened += HideOnce;
            }

            _ = StartupAsync(mainWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartupAsync(MainWindow mainWindow)
    {
        // Show crash report from the previous session and wait for the user
        // to dismiss it before continuing with normal startup.
        if (File.Exists(CrashHandler.PendingCrashFile))
        {
            try
            {
                string report = File.ReadAllText(CrashHandler.PendingCrashFile);
                File.Delete(CrashHandler.PendingCrashFile);
                // Yield once so the main window has time to open before
                // ShowDialog tries to attach to it as owner.
                await Task.Yield();

                // ShowDialog requires a visible owner. In daemon mode the main window
                // is hidden, so temporarily show it and re-hide after the dialog closes.
                bool reshide = CoreData.WasDaemon;
                if (reshide) mainWindow.Show();
                await new CrashReportWindow(report).ShowDialog(mainWindow);
                if (reshide) mainWindow.Hide();
            }
            catch { /* must not prevent normal startup */ }
        }

        await AvaloniaBootstrapper.InitializeAsync();
    }

    private static void HandleSecondaryInstanceArgs(MainWindow mainWindow, string[] args)
    {
        bool isDaemonLaunch = args.Contains(AvaloniaCliHandler.DAEMON);
        CoreData.IsDaemon = isDaemonLaunch;

        if (isDaemonLaunch)
            return;

        if (!mainWindow.IsVisible)
            mainWindow.Show();

        mainWindow.Activate();
    }

    public static void ApplyTheme(string value)
    {
        Current!.RequestedThemeVariant = value switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    public static string WebViewUserDataFolder { get; } =
        Path.Join(Path.GetTempPath(), "UniGetUI", "WebView");

    private static void SetUpWebViewUserDataFolder()
    {
        try
        {
            if (!Directory.Exists(WebViewUserDataFolder))
                Directory.CreateDirectory(WebViewUserDataFolder);

            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", WebViewUserDataFolder);
        }
        catch (Exception e)
        {
            Logger.Warn("Could not set up data folder for WebView2");
            Logger.Warn(e);
        }
    }

}
