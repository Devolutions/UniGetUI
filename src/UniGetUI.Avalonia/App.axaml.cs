using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
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
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Windows 11 Mica look is opt-in per environment: only merge the translucent
        // surface overrides when Mica is actually usable (Win11 + transparency on).
        // macOS, Linux, Windows 10, and transparency-off all keep the solid Styles.Common look.
        if (MicaWindowHelper.IsMicaEnabled())
            ApplyWindowsMicaStyling();
#if AVALONIA_DIAGNOSTICS_ENABLED
        this.AttachDeveloperTools();
#endif
    }

    // ResourceInclude is flagged with RequiresUnreferencedCode because, in general, it can load
    // resources from other assemblies that trimming might remove. Styles.WindowsMica.axaml is an
    // avares resource embedded in THIS assembly, so it is never trimmed — the warning is safe to
    // suppress here. (It can't be declared in XAML because the merge is conditional at runtime.)
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Styles.WindowsMica.axaml is an avares resource in this assembly and is not trimmed.")]
    private void ApplyWindowsMicaStyling()
    {
        Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null)
        {
            Source = new Uri("avares://UniGetUI/Assets/Styles/Styles.WindowsMica.axaml")
        });
        // Give flyouts/menus/tooltips a native acrylic backdrop (DWM) so they blur + tint
        // from behind and adapt to the theme.
        MicaWindowHelper.EnableAcrylicPopups();
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
            // Apply the saved theme before any window is shown so the splash
            // appears in the user's preferred light/dark variant from the start.
            ApplyTheme(CoreSettings.GetValue(CoreSettings.K.PreferredTheme));

            // Show the splash before any heavy initialization. Skipped in daemon
            // mode since the app isn't supposed to be visible at all.
            SplashWindow? splash = null;
            if (!CoreData.WasDaemon)
            {
                splash = new SplashWindow();
                splash.Show();
            }

            // Defer the rest of startup so the splash gets a chance to paint
            // before we block the UI thread loading package managers and the
            // main window XAML. Without this the splash window appears empty
            // until init completes, defeating its purpose.
            Dispatcher.UIThread.Post(() => StartMainWindow(desktop, splash), DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void StartMainWindow(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow? splash)
    {
        if (OperatingSystem.IsMacOS())
        {
            // The Dock icon (incl. Default/Dark/Tinted/Clear styling) is provided by the .app bundle's
            // AppIcon (scripts/macos/AppIcon.icon → Assets.car, via CFBundleIconName) and rendered by
            // the system — for packaged releases and for Debug builds, which also build into a .app
            // (see UniGetUI.Avalonia.csproj). There is nothing to do at runtime.
            ProcessEnvironmentConfigurator.PrepareForCurrentPlatform();
        }
        else
        {
            ProcessEnvironmentConfigurator.ApplyProxySettingsToProcess();
        }
        PEInterface.LoadLoaders();
        var mainWindow = new MainWindow();
        desktop.MainWindow = mainWindow;
        AvaloniaAppHost.SecondaryInstanceArgsReceived += args =>
            HandleSecondaryInstanceArgs(mainWindow, args);

        desktop.ShutdownRequested += (_, e) =>
        {
            if (mainWindow.IsQuitting)
                return;

            e.Cancel = true;
            mainWindow.QuitApplication();
        };

        if (Current?.TryGetFeature<IActivatableLifetime>() is { } activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen)
                    mainWindow.ShowFromTray();
            };
        }

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

        if (splash is not null)
        {
            var splashRef = splash;
            void CloseSplashOnce(object? s, EventArgs e)
            {
                mainWindow.Opened -= CloseSplashOnce;
                splashRef.Close();
            }
            mainWindow.Opened += CloseSplashOnce;
        }

        // Framework auto-show already passed (we deferred via Dispatcher.Post),
        // so we have to open the window ourselves.
        mainWindow.Show();

        _ = StartupAsync(mainWindow);
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
