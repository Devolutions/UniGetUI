using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Pages;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.Pages.SettingsPages.GeneralPages;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Pages.SettingsPages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class SettingsBasePage : Page, IEnterLeaveListener, IInnerNavigationPage
    {
        private readonly bool IsManagers;

        public SettingsBasePage(bool isManagers)
        {
            IsManagers = isManagers;
            this.InitializeComponent();
            BackButton.Click += (_, _) =>
            {
                if (MainNavigationFrame.Content is ManagersHomepage or SettingsHomepage)
                    MainApp.Instance.MainWindow.GoBack();
                else if (MainNavigationFrame.CanGoBack)
                    MainNavigationFrame.GoBack();
                else
                    MainNavigationFrame.Navigate(
                        isManagers ? typeof(ManagersHomepage) : typeof(SettingsHomepage),
                        null,
                        new DrillInNavigationTransitionInfo()
                    );
            };
            MainNavigationFrame.Navigated += MainNavigationFrame_Navigated;
            MainNavigationFrame.Navigating += MainNavigationFrame_Navigating;
            MainNavigationFrame.Navigate(
                isManagers ? typeof(ManagersHomepage) : typeof(SettingsHomepage),
                null,
                new DrillInNavigationTransitionInfo()
            );

            RestartRequired.Message = CoreTools.Translate(
                "Restart UniGetUI to fully apply changes"
            );
            var RestartButton = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Content = CoreTools.Translate("Restart UniGetUI"),
            };
            RestartButton.Click += (_, _) => MainApp.Instance.KillAndRestart();
            RestartRequired.ActionButton = RestartButton;
        }

        private void MainNavigationFrame_Navigating(object sender, NavigatingCancelEventArgs e)
        {
            if (MainNavigationFrame.Content is null)
                return;
            var page = MainNavigationFrame.Content as ISettingsPage;
            if (page is null)
                throw new InvalidCastException("Settings page does not inherit from ISettingsPage");

            page.NavigationRequested -= Page_NavigationRequested;
            page.RestartRequired -= Page_RestartRequired;
            if (page is PackageManagerPage pmpage)
                pmpage.ReapplyProperties -= SettingsBasePage_ReapplyProperties;
        }

        private void MainNavigationFrame_Navigated(object sender, NavigationEventArgs e)
        {
            var page = e.Content as ISettingsPage;
            if (page is null)
                throw new InvalidCastException("Settings page does not inherit from ISettingsPage");

            BackButton.Visibility = Visibility.Visible;
            SettingsTitle.Text = page.ShortTitle;
            page.NavigationRequested += Page_NavigationRequested;
            page.RestartRequired += Page_RestartRequired;
            if (page is PackageManagerPage pmpage)
                pmpage.ReapplyProperties += SettingsBasePage_ReapplyProperties;
        }

        private void SettingsBasePage_ReapplyProperties(object? sender, EventArgs e)
        {
            BackButton.Visibility =
                ((MainNavigationFrame.Content as ISettingsPage)?.CanGoBack ?? true)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            SettingsTitle.Text =
                (MainNavigationFrame.Content as ISettingsPage)?.ShortTitle
                ?? "INVALID CONTENT PAGE!";
        }

        private void Page_RestartRequired(object? sender, EventArgs e)
        {
            RestartRequired.IsOpen = true;
        }

        private void Page_NavigationRequested(object? sender, Type e)
        {
            if (e == typeof(ManagersHomepage))
            {
                MainApp.Instance.MainWindow.NavigationPage.NavigateTo(Interface.PageType.Managers);
            }
            if (e.IsSubclassOf(typeof(PackageManager)))
            {
                MainNavigationFrame.Navigate(
                    typeof(PackageManagerPage),
                    e,
                    new SlideNavigationTransitionInfo()
                    {
                        Effect = SlideNavigationTransitionEffect.FromRight,
                    }
                );
            }
            else
            {
                MainNavigationFrame.Navigate(
                    e,
                    null,
                    new SlideNavigationTransitionInfo()
                    {
                        Effect = SlideNavigationTransitionEffect.FromRight,
                    }
                );
            }
        }

        public void NavigateTo(IPackageManager manager)
        {
            Page_NavigationRequested(this, manager.GetType());
        }

        public void NavigateTo(Type e)
        {
            MainNavigationFrame.Navigate(e, null, new DrillInNavigationTransitionInfo());
        }

        public void OnEnter() =>
            MainNavigationFrame.Navigate(
                IsManagers ? typeof(ManagersHomepage) : typeof(SettingsHomepage),
                null,
                new DrillInNavigationTransitionInfo()
            );

        public void OnLeave() { }

        public bool CanGoBack() =>
            MainNavigationFrame.CanGoBack
            && MainNavigationFrame.Content is not SettingsHomepage
            && MainNavigationFrame.Content is not ManagersHomepage;

        public void GoBack()
        {
            if (CanGoBack())
                MainNavigationFrame.GoBack();
            else
                MainApp.Instance.MainWindow.GoBack();
        }
    }
}
