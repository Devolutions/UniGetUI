using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Pages.SettingsPages.GeneralPages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class Interface_P : Page, ISettingsPage
    {
        public Interface_P()
        {
            this.InitializeComponent();

            if (Settings.GetValue(Settings.K.PreferredTheme) == "")
            {
                Settings.SetValue(Settings.K.PreferredTheme, "auto");
            }

            ThemeSelector.AddItem(CoreTools.AutoTranslated("Light"), "light");
            ThemeSelector.AddItem(CoreTools.AutoTranslated("Dark"), "dark");
            ThemeSelector.AddItem(CoreTools.AutoTranslated("Follow system color scheme"), "auto");
            ThemeSelector.ShowAddedItems();

            StartupPageSelector.AddItem(CoreTools.AutoTranslated("Default"), "default");
            StartupPageSelector.AddItem(CoreTools.AutoTranslated("Discover Packages"), "discover");
            StartupPageSelector.AddItem(CoreTools.AutoTranslated("Software Updates"), "updates");
            StartupPageSelector.AddItem(
                CoreTools.AutoTranslated("Installed Packages"),
                "installed"
            );
            StartupPageSelector.AddItem(CoreTools.AutoTranslated("Package Bundles"), "bundles");
            StartupPageSelector.AddItem(CoreTools.AutoTranslated("Settings"), "settings");
            StartupPageSelector.ShowAddedItems();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _ = LoadIconCacheSize();
        }

        public bool CanGoBack => true;

        public string ShortTitle => CoreTools.Translate("User interface preferences");

        public event EventHandler? RestartRequired;
        public event EventHandler<Type>? NavigationRequested
        {
            add { }
            remove { }
        }

        public void ShowRestartBanner(object sender, EventArgs e) =>
            RestartRequired?.Invoke(this, e);

        private void ResetIconCache_OnClick(object sender, EventArgs e)
        {
            try
            {
                Directory.Delete(CoreData.UniGetUICacheDirectory_Icons, true);
            }
            catch (Exception ex)
            {
                Logger.Error("An error occurred while deleting icon cache");
                Logger.Error(ex);
            }
            ShowRestartBanner(this, new());
            // PackageWrapper.ResetIconCache();
            // Package.ResetIconCache();
            _ = LoadIconCacheSize();
        }

        private async Task LoadIconCacheSize()
        {
            double realSize =
                (
                    await Task.Run(() =>
                    {
                        return Directory
                            .GetFiles(
                                CoreData.UniGetUICacheDirectory_Icons,
                                "*",
                                SearchOption.AllDirectories
                            )
                            .Sum(file => new FileInfo(file).Length);
                    })
                ) / 1048576d;
            double roundedSize = ((int)(realSize * 100)) / 100d;
            ResetIconCache.Header = CoreTools.Translate(
                "The local icon cache currently takes {0} MB",
                roundedSize
            );
        }

        private void DisableSystemTray_StateChanged(object sender, EventArgs e) =>
            MainApp.Instance.MainWindow.UpdateSystemTrayStatus();

        private void ThemeSelector_ValueChanged(object sender, EventArgs e) =>
            MainApp.Instance.MainWindow.ApplyTheme();

        private void EditAutostartSettings_Click(object sender, EventArgs e) =>
            CoreTools.Launch("ms-settings:startupapps");
    }
}
