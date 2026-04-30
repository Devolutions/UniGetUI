using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Win32;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.Infrastructure;

[SupportedOSPlatform("windows")]
internal sealed class WindowsTrayService : IDisposable
{
    private readonly TrayIcon _trayIcon;
    private string _lastIconUri = "";

    public WindowsTrayService(MainWindow owner)
    {
        _trayIcon = new TrayIcon
        {
            ToolTipText = "UniGetUI",
            Menu = BuildMenu(owner),
        };

        _trayIcon.Clicked += (_, _) => Dispatcher.UIThread.Post(() => owner.ShowFromTray());

        var app = Application.Current!;
        var icons = TrayIcon.GetIcons(app) ?? new TrayIcons();
        icons.Add(_trayIcon);
        TrayIcon.SetIcons(app, icons);
    }

    public void UpdateStatus()
    {
        try
        {
            _trayIcon.IsVisible = !Settings.Get(Settings.K.DisableSystemTray);

            string modifier;
            string tooltip;

            bool anyRunning = AvaloniaOperationRegistry.Operations.Any(
                o => o.Status is OperationStatus.Running or OperationStatus.InQueue);

            int updatesCount = UpgradablePackagesLoader.Instance?.Count() ?? 0;

            if (anyRunning)
            {
                modifier = "_blue";
                tooltip = CoreTools.Translate("Operation in progress");
            }
            else if (AvaloniaOperationRegistry.ErrorsOccurred > 0)
            {
                modifier = "_orange";
                tooltip = CoreTools.Translate("Attention required");
            }
            else if (AvaloniaOperationRegistry.RestartRequired)
            {
                modifier = "_turquoise";
                tooltip = CoreTools.Translate("Restart required");
            }
            else if (updatesCount > 0)
            {
                modifier = "_green";
                tooltip = updatesCount == 1
                    ? CoreTools.Translate("1 update is available")
                    : CoreTools.Translate("{0} updates are available", updatesCount);
            }
            else
            {
                modifier = "_empty";
                tooltip = CoreTools.Translate("Everything is up to date");
            }

            _trayIcon.ToolTipText = tooltip + " - UniGetUI";

            modifier += IsTaskbarLight() ? "_black" : "_white";

            string uri = $"avares://UniGetUI.Avalonia/Assets/tray{modifier}.ico";
            if (_lastIconUri == uri) return;
            _lastIconUri = uri;

            using var stream = AssetLoader.Open(new Uri(uri));
            _trayIcon.Icon = new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to update tray icon status:");
            Logger.Error(ex);
        }
    }

    // Windows reads SystemUsesLightTheme to pick dark vs light taskbar icons.
    private static bool IsTaskbarLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v > 0;
        }
        catch
        {
            return false;
        }
    }

    private static NativeMenu BuildMenu(MainWindow owner)
    {
        var menu = new NativeMenu();

        var discover = new NativeMenuItem(CoreTools.Translate("Discover Packages"));
        discover.Click += (_, _) => Dispatcher.UIThread.Post(() => { owner.Navigate(PageType.Discover); owner.ShowFromTray(); });

        var updates = new NativeMenuItem(CoreTools.Translate("Available Updates"));
        updates.Click += (_, _) => Dispatcher.UIThread.Post(() => { owner.Navigate(PageType.Updates); owner.ShowFromTray(); });

        var installed = new NativeMenuItem(CoreTools.Translate("Installed Packages"));
        installed.Click += (_, _) => Dispatcher.UIThread.Post(() => { owner.Navigate(PageType.Installed); owner.ShowFromTray(); });

        menu.Add(discover);
        menu.Add(updates);
        menu.Add(installed);
        menu.Add(new NativeMenuItemSeparator());

        menu.Add(new NativeMenuItem(
            CoreTools.Translate("UniGetUI Version {0} by Devolutions", CoreData.VersionName))
        {
            IsEnabled = false,
        });
        menu.Add(new NativeMenuItemSeparator());

        var show = new NativeMenuItem(CoreTools.Translate("Show UniGetUI"));
        show.Click += (_, _) => Dispatcher.UIThread.Post(() => owner.ShowFromTray());
        menu.Add(show);

        var quit = new NativeMenuItem(CoreTools.Translate("Quit"));
        quit.Click += (_, _) => Dispatcher.UIThread.Post(() => owner.QuitApplication());
        menu.Add(quit);

        return menu;
    }

    public void Dispose()
    {
        var app = Application.Current;
        if (app is not null)
            TrayIcon.GetIcons(app)?.Remove(_trayIcon);
        _trayIcon.Dispose();
    }
}
