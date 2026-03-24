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

    private void ShowRestartBanner(object? sender, EventArgs e) => RestartRequired?.Invoke(this, e);

    public Interface_P()
    {
        DataContext = new Interface_PViewModel();
        InitializeComponent();

        if (OperatingSystem.IsMacOS())
            SystemTraySection.IsVisible = false;

        VM.RestartRequired += ShowRestartBanner;
        VM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VM.IconCacheSizeText))
                ResetIconCache.Header = VM.IconCacheSizeText;
        };
        _ = VM.LoadIconCacheSize();

        if (CoreSettings.GetValue(CoreSettings.K.PreferredTheme) == "")
            CoreSettings.SetValue(CoreSettings.K.PreferredTheme, "auto");

        ThemeSelector.AddItem(CoreTools.AutoTranslated("Light"), "light");
        ThemeSelector.AddItem(CoreTools.AutoTranslated("Dark"), "dark");
        ThemeSelector.AddItem(CoreTools.AutoTranslated("Follow system color scheme"), "auto");
        ThemeSelector.SettingName = CoreSettings.K.PreferredTheme;
        ThemeSelector.Text = "Application theme:";
        ThemeSelector.ShowAddedItems();
        ThemeSelector.ValueChanged += (_, _) => App.ApplyTheme(CoreSettings.GetValue(CoreSettings.K.PreferredTheme));

        StartupPageSelector.AddItem(CoreTools.AutoTranslated("Default"), "default");
        StartupPageSelector.AddItem(CoreTools.AutoTranslated("Discover Packages"), "discover");
        StartupPageSelector.AddItem(CoreTools.AutoTranslated("Software Updates"), "updates");
        StartupPageSelector.AddItem(CoreTools.AutoTranslated("Installed Packages"), "installed");
        StartupPageSelector.AddItem(CoreTools.AutoTranslated("Package Bundles"), "bundles");
        StartupPageSelector.AddItem(CoreTools.AutoTranslated("Settings"), "settings");
        StartupPageSelector.SettingName = CoreSettings.K.StartupPage;
        StartupPageSelector.Text = "UniGetUI startup page:";
        StartupPageSelector.ShowAddedItems();

        DisableSystemTray.SettingName = CoreSettings.K.DisableSystemTray;
        DisableSystemTray.Text = "Close UniGetUI to the system tray";

        DisableIconsOnPackageLists.SettingName = CoreSettings.K.DisableIconsOnPackageLists;
        DisableIconsOnPackageLists.Text = "Show package icons on package lists";
        DisableIconsOnPackageLists.StateChanged += ShowRestartBanner;

        DisableSelectingUpdatesByDefault.SettingName = CoreSettings.K.DisableSelectingUpdatesByDefault;
        DisableSelectingUpdatesByDefault.Text = "Select upgradable packages by default";
        DisableSelectingUpdatesByDefault.StateChanged += ShowRestartBanner;
    }
}
