using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using UniGetUI.Avalonia.Views;
using UniGetUI.PackageEngine;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            if (OperatingSystem.IsMacOS())
                ExpandMacOSPath();
            PEInterface.LoadLoaders();
            ApplyTheme(CoreSettings.GetValue(CoreSettings.K.PreferredTheme));
            var mainWindow = new MainWindow();
#if DEBUG
            mainWindow.AttachDevTools();
#endif
            desktop.MainWindow = mainWindow;
            _ = Task.Run(PEInterface.LoadManagers);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// macOS GUI apps start with a minimal PATH (/usr/bin:/bin:/usr/sbin:/sbin).
    /// Ask the user's login shell for its full PATH so package managers (npm, pip,
    /// cargo, brew-installed tools, …) can be found.
    /// </summary>
    private static void ExpandMacOSPath()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("zsh", ["-l", "-c", "printenv PATH"])
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            string shellPath = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            if (!string.IsNullOrEmpty(shellPath))
                Environment.SetEnvironmentVariable("PATH", shellPath);
        }
        catch { /* keep the existing PATH if the shell can't be launched */ }
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

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
