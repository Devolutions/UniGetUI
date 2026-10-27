using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.Tools;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class Interface_P : UserControl, ISettingsPage
{
    private Interface_PViewModel VM => (Interface_PViewModel)DataContext!;

    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("User interface preferences");

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested { add { } remove { } }

    public Interface_P()
    {
        DataContext = new Interface_PViewModel();
        InitializeComponent();

        VM.RestartRequired += (s, e) => RestartRequired?.Invoke(s, e);
        _ = VM.LoadIconCacheSize();

        if (CoreSettings.GetValue(CoreSettings.K.PreferredTheme) == "")
            CoreSettings.SetValue(CoreSettings.K.PreferredTheme, "auto");

        if (!OperatingSystem.IsWindows())
            UseSystemUIFontCard.Text = CoreTools.Translate(
                "Use the font configured in the system instead of the default interface font");

        ThemeSelector.AddItem(CoreTools.Translate("Light"), "light");
        ThemeSelector.AddItem(CoreTools.Translate("Dark"), "dark");
        ThemeSelector.AddItem(CoreTools.Translate("Follow system color scheme"), "auto");
        ThemeSelector.SettingName = CoreSettings.K.PreferredTheme;
        ThemeSelector.Text = CoreTools.Translate("Application theme:");
        ThemeSelector.ShowAddedItems();
        ThemeSelector.ValueChanged += (_, _) => App.ApplyTheme(CoreSettings.GetValue(CoreSettings.K.PreferredTheme));

        bool catalogEnabled = CoreSettings.Get(CoreSettings.K.EnableSoftwareCatalog);
        StartupPageSelector.AddItem(CoreTools.Translate("Default"), "default");
        if (catalogEnabled)
            StartupPageSelector.AddItem(CoreTools.Translate("Software Catalog"), "catalog");
        StartupPageSelector.AddItem(CoreTools.Translate("Discover Packages"), "discover");
        StartupPageSelector.AddItem(CoreTools.Translate("Software Updates"), "updates");
        StartupPageSelector.AddItem(CoreTools.Translate("Installed Packages"), "installed");
        StartupPageSelector.AddItem(CoreTools.Translate("Package Bundles"), "bundles");
        if (catalogEnabled && CoreSettings.Get(CoreSettings.K.EnableCatalogEditor))
            StartupPageSelector.AddItem(CoreTools.Translate("Catalog Editor"), "catalog-editor");
        StartupPageSelector.AddItem(CoreTools.Translate("Settings"), "settings");
        StartupPageSelector.SettingName = CoreSettings.K.StartupPage;
        StartupPageSelector.Text = CoreTools.Translate("UniGetUI startup page:");
        StartupPageSelector.ShowAddedItems();

        NavMenuModeSelector.AddItem(CoreTools.Translate("Automatic"), "auto");
        NavMenuModeSelector.AddItem(CoreTools.Translate("Docked open"), "docked");
        NavMenuModeSelector.AddItem(CoreTools.Translate("Sliding overlay"), "overlay");
        NavMenuModeSelector.SettingName = CoreSettings.K.NavMenuMode;
        NavMenuModeSelector.Text = CoreTools.Translate("Navigation menu:");
        NavMenuModeSelector.ShowAddedItems();
        NavMenuModeSelector.ValueChanged += (_, _) => MainWindow.Instance?.RefreshNavigationMode();

        WindowCloseBehaviorSelector.AddItem(
            CoreTools.Translate("Keep running in the background, with a system tray icon"),
            nameof(CoreSettings.WindowCloseBehavior.KeepRunningWithTrayIcon));
        WindowCloseBehaviorSelector.AddItem(
            CoreTools.Translate("Keep running in the background, without a system tray icon"),
            nameof(CoreSettings.WindowCloseBehavior.KeepRunningWithoutTrayIcon));
        // macOS always keeps the app running when its window closes, so quitting is not a choice there.
        if (!OperatingSystem.IsMacOS())
            WindowCloseBehaviorSelector.AddItem(
                CoreTools.Translate("Quit UniGetUI"),
                nameof(CoreSettings.WindowCloseBehavior.Quit));
        WindowCloseBehaviorSelector.Text = CoreTools.Translate("When the main window is closed:");
        WindowCloseBehaviorSelector.ShowAddedItems(
            () => GetShownWindowCloseBehavior().ToString(),
            value => CoreSettings.SetWindowCloseBehavior(Enum.Parse<CoreSettings.WindowCloseBehavior>(value)));
        WindowCloseBehaviorSelector.ValueChanged += (_, _) => UpdateWindowCloseBehaviorDescription();
        UpdateWindowCloseBehaviorDescription();

        NavigationOrderCard.Description = CoreTools.Translate(
            "Drag the pages in the navigation menu, or press {0} on a focused page, to reorder them. Keyboard shortcuts follow the same order.",
            OperatingSystem.IsMacOS() ? "Option+Up/Down" : "Alt+Up/Down");
        NavigationOrderCard.Click += (_, _) => MainWindow.Instance?.ResetNavigationOrder();
    }

    // A "Quit" saved on macOS (by an older version's tray checkbox) already behaves as a hidden icon there.
    private static CoreSettings.WindowCloseBehavior GetShownWindowCloseBehavior()
    {
        var behavior = CoreSettings.GetWindowCloseBehavior();
        return OperatingSystem.IsMacOS() && behavior is CoreSettings.WindowCloseBehavior.Quit
            ? CoreSettings.WindowCloseBehavior.KeepRunningWithoutTrayIcon
            : behavior;
    }

    private void UpdateWindowCloseBehaviorDescription()
    {
        WindowCloseBehaviorSelector.Description = GetShownWindowCloseBehavior() switch
        {
            CoreSettings.WindowCloseBehavior.KeepRunningWithoutTrayIcon when OperatingSystem.IsLinux() =>
                CoreTools.Translate("UniGetUI keeps checking for updates. To reopen it, launch UniGetUI again. To exit, use More > Quit UniGetUI in the navigation menu.")
                + " " + CoreTools.Translate("Notifications are not available on Linux, so you will not be told when updates are found."),
            CoreSettings.WindowCloseBehavior.KeepRunningWithoutTrayIcon =>
                CoreTools.Translate("UniGetUI keeps checking for updates. To reopen it, launch UniGetUI again. To exit, use More > Quit UniGetUI in the navigation menu."),
            CoreSettings.WindowCloseBehavior.Quit =>
                CoreTools.Translate("UniGetUI stops checking for updates and sending notifications until you open it again."),
            _ => null,
        };
    }
}
