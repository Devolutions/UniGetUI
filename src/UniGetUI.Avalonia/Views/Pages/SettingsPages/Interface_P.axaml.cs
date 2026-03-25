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

        if (OperatingSystem.IsMacOS())
            SystemTraySection.IsVisible = false;

        VM.RestartRequired += (s, e) => RestartRequired?.Invoke(s, e);
        _ = VM.LoadIconCacheSize();

        if (CoreSettings.GetValue(CoreSettings.K.PreferredTheme) == "")
            CoreSettings.SetValue(CoreSettings.K.PreferredTheme, "auto");

        ThemeSelector.AddItem(CoreTools.Translate("Light"), "light");
        ThemeSelector.AddItem(CoreTools.Translate("Dark"), "dark");
        ThemeSelector.AddItem(CoreTools.Translate("Follow system color scheme"), "auto");
        ThemeSelector.SettingName = CoreSettings.K.PreferredTheme;
        ThemeSelector.Text = CoreTools.Translate("Application theme:");
        ThemeSelector.ShowAddedItems();
        ThemeSelector.ValueChanged += (_, _) => App.ApplyTheme(CoreSettings.GetValue(CoreSettings.K.PreferredTheme));

        StartupPageSelector.AddItem(CoreTools.Translate("Default"), "default");
        StartupPageSelector.AddItem(CoreTools.Translate("Discover Packages"), "discover");
        StartupPageSelector.AddItem(CoreTools.Translate("Software Updates"), "updates");
        StartupPageSelector.AddItem(CoreTools.Translate("Installed Packages"), "installed");
        StartupPageSelector.AddItem(CoreTools.Translate("Package Bundles"), "bundles");
        StartupPageSelector.AddItem(CoreTools.Translate("Settings"), "settings");
        StartupPageSelector.SettingName = CoreSettings.K.StartupPage;
        StartupPageSelector.Text = CoreTools.Translate("UniGetUI startup page:");
        StartupPageSelector.ShowAddedItems();
    }
}
