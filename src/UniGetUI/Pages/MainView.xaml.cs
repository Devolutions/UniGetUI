using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using UniGetUI.Controls;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Dialogs;
using UniGetUI.Interface.Pages;
using UniGetUI.Interface.Pages.LogPage;
using UniGetUI.Interface.SoftwarePages;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageOperations;
using UniGetUI.Pages.DialogPages;
using UniGetUI.Pages.PageInterfaces;
using UniGetUI.Pages.SettingsPages;
using Windows.System;
using Windows.UI.Core;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Interface
{
    public enum PageType
    {
        Discover,
        Updates,
        Installed,
        Bundles,
        Settings,
        Managers,
        OwnLog,
        ManagerLog,
        OperationHistory,
        Help,
        Null, // Used for initializers
    }

    public sealed partial class MainView : UserControl
    {
        private readonly DiscoverSoftwarePage DiscoverPage;
        private readonly SoftwareUpdatesPage UpdatesPage;
        private readonly InstalledPackagesPage InstalledPage;
        private readonly PackageBundlesPage BundlesPage;
        private SettingsBasePage? SettingsPage;
        private SettingsBasePage? ManagersPage;
        private UniGetUILogPage? UniGetUILogPage;
        private ManagerLogsPage? ManagerLogPage;
        private OperationHistoryPage? OperationHistoryPage;
        private HelpPage? HelpPage;

        private PageType OldPage_t = PageType.Null;
        private PageType CurrentPage_t = PageType.Null;
        private readonly List<PageType> NavigationHistory = new();

        private readonly AutoSuggestBox MainTextBlock;
        public event EventHandler<bool>? CanGoBackChanged;

        public MainView(AutoSuggestBox mainTextBlock)
        {
            InitializeComponent();
            MainTextBlock = mainTextBlock;
            OperationList.ItemContainerTransitions = null;
            OperationList.ItemsSource = MainApp.Operations._operationList;
            DiscoverPage = new DiscoverSoftwarePage();
            UpdatesPage = new SoftwareUpdatesPage();
            InstalledPage = new InstalledPackagesPage();
            BundlesPage = new PackageBundlesPage();

            MoreNavButtonMenu.Closed += (_, _) => SelectNavButtonForPage(CurrentPage_t);
            KeyDown += (s, e) =>
            {
                if (e.KeyStatus.WasKeyDown)
                {
                    // ignore repeated KeyDown events when pressing and holding a key
                    return;
                }

                bool IS_CONTROL_PRESSED = InputKeyboardSource
                    .GetKeyStateForCurrentThread(VirtualKey.Control)
                    .HasFlag(CoreVirtualKeyStates.Down);
                bool IS_SHIFT_PRESSED = InputKeyboardSource
                    .GetKeyStateForCurrentThread(VirtualKey.Shift)
                    .HasFlag(CoreVirtualKeyStates.Down);

                Page currentPage = GetPageForType(CurrentPage_t);
                if (e.Key is VirtualKey.Tab && IS_CONTROL_PRESSED)
                {
                    NavigateTo(
                        IS_SHIFT_PRESSED
                            ? GetPreviousPage(CurrentPage_t)
                            : GetNextPage(CurrentPage_t)
                    );
                }
                else if (!IS_CONTROL_PRESSED && !IS_SHIFT_PRESSED && e.Key == VirtualKey.F1)
                {
                    NavigateTo(PageType.Help);
                }
                else if ((e.Key is VirtualKey.Q or VirtualKey.W) && IS_CONTROL_PRESSED)
                {
                    MainApp.Instance.MainWindow.Close();
                }
                else if (e.Key is VirtualKey.F5 || (e.Key is VirtualKey.R && IS_CONTROL_PRESSED))
                {
                    (currentPage as IKeyboardShortcutListener)?.ReloadTriggered();
                }
                else if (e.Key is VirtualKey.F && IS_CONTROL_PRESSED)
                {
                    (currentPage as IKeyboardShortcutListener)?.SearchTriggered();
                }
                else if (e.Key is VirtualKey.A && IS_CONTROL_PRESSED)
                {
                    (currentPage as IKeyboardShortcutListener)?.SelectAllTriggered();
                }
            };

            /*
             * Connect different loaders and UI Sections to bundles page
             */
            foreach (
                var pair in new Dictionary<CustomNavViewItem, AbstractPackageLoader>
                {
                    { DiscoverNavBtn, DiscoverablePackagesLoader.Instance },
                    { UpdatesNavBtn, UpgradablePackagesLoader.Instance },
                    { InstalledNavBtn, InstalledPackagesLoader.Instance },
                }
            )
            {
                pair.Value.FinishedLoading += (_, _) =>
                    MainApp.Dispatcher.TryEnqueue(() => pair.Key.IsLoading = false);
                pair.Value.StartedLoading += (_, _) =>
                    MainApp.Dispatcher.TryEnqueue(() => pair.Key.IsLoading = true);
                pair.Key.IsLoading = pair.Value.IsLoading;
            }

            UpgradablePackagesLoader.Instance.PackagesChanged += (_, _) =>
                MainApp.Dispatcher.TryEnqueue(() =>
                {
                    UpdatesBadge.Value = UpgradablePackagesLoader.Instance.Count();
                    UpdatesBadge.Visibility =
                        UpdatesBadge.Value > 0 ? Visibility.Visible : Visibility.Collapsed;
                });
            UpdatesBadge.Value = UpgradablePackagesLoader.Instance.Count();
            UpdatesBadge.Visibility =
                UpdatesBadge.Value > 0 ? Visibility.Visible : Visibility.Collapsed;

            BundlesPage.UnsavedChangesStateChanged += (_, _) =>
                MainApp.Dispatcher.TryEnqueue(() =>
                {
                    BundlesBadge.Visibility = BundlesPage.HasUnsavedChanges
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                });
            BundlesBadge.Visibility = BundlesPage.HasUnsavedChanges
                ? Visibility.Visible
                : Visibility.Collapsed;

            /*
             * End connecting stuff together
             */

            LoadDefaultPage();

            if (CoreTools.IsAdministrator() && !Settings.Get(Settings.K.AlreadyWarnedAboutAdmin))
            {
                Settings.Set(Settings.K.AlreadyWarnedAboutAdmin, true);
                _ = DialogHelper.WarnAboutAdminRights();
            }

            UpdateOperationsLayout();
            MainApp.Operations._operationList.CollectionChanged += (_, _) =>
                UpdateOperationsLayout();

            if (!Settings.Get(Settings.K.ShownTelemetryBanner))
            {
                DialogHelper.ShowTelemetryBanner();
            }

            if (!Settings.Get(Settings.K.CollapseNavMenuOnWideScreen))
            {
                NavView.IsPaneOpen = true;
            }
        }

        public void LoadDefaultPage()
        {
            PageType type = Settings.GetValue(Settings.K.StartupPage) switch
            {
                "discover" => PageType.Discover,
                "updates" => PageType.Updates,
                "installed" => PageType.Installed,
                "bundles" => PageType.Bundles,
                "settings" => PageType.Settings,
                _ => MainApp.Tooltip.AvailableUpdates > 0 ? PageType.Updates : PageType.Discover,
            };
            NavigateTo(type);
        }

        private Page GetPageForType(PageType type) =>
            type switch
            {
                PageType.Discover => DiscoverPage,
                PageType.Updates => UpdatesPage,
                PageType.Installed => InstalledPage,
                PageType.Bundles => BundlesPage,
                PageType.Settings => SettingsPage ??= new SettingsBasePage(false),
                PageType.Managers => ManagersPage ??= new SettingsBasePage(true),
                PageType.OwnLog => UniGetUILogPage ??= new UniGetUILogPage(),
                PageType.ManagerLog => ManagerLogPage ??= new ManagerLogsPage(),
                PageType.OperationHistory => OperationHistoryPage ??= new OperationHistoryPage(),
                PageType.Help => HelpPage ??= new HelpPage(),
                PageType.Null => throw new InvalidCastException("Page type is Null"),
                _ => throw new InvalidDataException($"Unknown page type {type}"),
            };

        private static PageType GetNextPage(PageType type) =>
            type switch
            {
                // Default loop
                PageType.Discover => PageType.Updates,
                PageType.Updates => PageType.Installed,
                PageType.Installed => PageType.Bundles,
                PageType.Bundles => PageType.Settings,
                PageType.Settings => PageType.Managers,
                PageType.Managers => PageType.Discover,

                // "Extra" pages
                PageType.OperationHistory => PageType.Discover,
                PageType.OwnLog => PageType.Discover,
                PageType.ManagerLog => PageType.Discover,
                PageType.Help => PageType.Discover,
                PageType.Null => PageType.Discover,
                _ => throw new InvalidDataException($"Unknown page type {type}"),
            };

        private static PageType GetPreviousPage(PageType type) =>
            type switch
            {
                // Default loop
                PageType.Discover => PageType.Settings,
                PageType.Updates => PageType.Discover,
                PageType.Installed => PageType.Updates,
                PageType.Bundles => PageType.Installed,
                PageType.Settings => PageType.Bundles,
                PageType.Managers => PageType.Settings,

                // "Extra" pages
                PageType.OperationHistory => PageType.Discover,
                PageType.OwnLog => PageType.Discover,
                PageType.ManagerLog => PageType.Discover,
                PageType.Help => PageType.Discover,
                PageType.Null => PageType.Discover,
                _ => throw new InvalidDataException($"Unknown page type {type}"),
            };

        private void SettingsNavButton_Click(object sender, EventArgs e) =>
            NavigateTo(PageType.Settings);

        private void ManagersNavButton_Click(object sender, EventArgs e) =>
            NavigateTo(PageType.Managers);

        private bool _lastNavItemSelectionWasAuto;

        private void SelectNavButtonForPage(PageType page)
        {
            _lastNavItemSelectionWasAuto = true;
            NavView.SelectedItem = page switch
            {
                PageType.Discover => DiscoverNavBtn,
                PageType.Updates => UpdatesNavBtn,
                PageType.Installed => InstalledNavBtn,
                PageType.Bundles => BundlesNavBtn,
                PageType.Settings => SettingsNavBtn,
                PageType.Managers => ManagersNavBtn,
                _ => MoreNavBtn,
            };
            _lastNavItemSelectionWasAuto = false;
        }

        private void AboutNavButton_Click(object sender, RoutedEventArgs e) =>
            _ = _aboutNavButton_Click();

        private async Task _aboutNavButton_Click()
        {
            SelectNavButtonForPage(PageType.Null);
            await DialogHelper.ShowAboutUniGetUI();
            SelectNavButtonForPage(CurrentPage_t);
        }

        public void NavigateTo(PageType NewPage_t, bool toHistory = true)
        {
            SelectNavButtonForPage(NewPage_t);
            if (CurrentPage_t == NewPage_t)
                return;

            Page NewPage = GetPageForType(NewPage_t);
            Page? oldPage = ContentFrame.Content as Page;
            ContentFrame.Content = NewPage;

            OldPage_t = CurrentPage_t;
            CurrentPage_t = NewPage_t;

            (oldPage as IEnterLeaveListener)?.OnLeave();
            if (oldPage is ISearchBoxPage oldSPage)
            {
                MainTextBlock.TextChanged -= oldSPage.SearchBox_TextChanged;
                MainTextBlock.QuerySubmitted -= oldSPage.SearchBox_QuerySubmitted;
                oldSPage.QueryBackup = MainTextBlock.Text;
            }

            if (toHistory && OldPage_t is not PageType.Null)
            {
                NavigationHistory.Add(OldPage_t);
                CanGoBackChanged?.Invoke(this, true);
            }

            (NewPage as AbstractPackagesPage)?.FocusPackageList();
            (NewPage as AbstractPackagesPage)?.FilterPackages();
            (NewPage as IEnterLeaveListener)?.OnEnter();

            if (NewPage is ISearchBoxPage newSPage)
            {
                MainTextBlock.TextChanged += newSPage.SearchBox_TextChanged;
                MainTextBlock.QuerySubmitted += newSPage.SearchBox_QuerySubmitted;
                MainTextBlock.Text = newSPage.QueryBackup;
                MainTextBlock.PlaceholderText = newSPage.SearchBoxPlaceholder;
                MainTextBlock.IsEnabled = true;
            }
            else
            {
                MainTextBlock.Text = "";
                MainTextBlock.PlaceholderText = "";
                MainTextBlock.IsEnabled = false;
            }
        }

        public void NavigateBack()
        {
            if (ContentFrame.Content is IInnerNavigationPage navPage && navPage.CanGoBack())
            {
                navPage.GoBack();
            }
            else
            {
                NavigateTo(NavigationHistory.Last(), toHistory: false);
                NavigationHistory.RemoveAt(NavigationHistory.Count - 1);
                CanGoBackChanged?.Invoke(
                    this,
                    NavigationHistory.Any()
                        || ((ContentFrame.Content as IInnerNavigationPage)?.CanGoBack() ?? false)
                );
            }
        }

        private void ReleaseNotesMenu_Click(object sender, RoutedEventArgs e) =>
            _ = DialogHelper.ShowReleaseNotes();

        private void OperationHistoryMenu_Click(object sender, RoutedEventArgs e) =>
            NavigateTo(PageType.OperationHistory);

        private void ManagerLogsMenu_Click(object sender, RoutedEventArgs e) => OpenManagerLogs();

        public void OpenManagerLogs(IPackageManager? manager = null)
        {
            NavigateTo(PageType.ManagerLog);
            if (manager is not null)
                ManagerLogPage?.LoadForManager(manager);
        }

        public void OpenManagerSettings(IPackageManager? manager = null)
        {
            NavigateTo(PageType.Managers);
            if (manager is not null)
                ManagersPage?.NavigateTo(manager);
        }

        public void OpenSettingsPage(Type page)
        {
            NavigateTo(PageType.Settings);
            SettingsPage?.NavigateTo(page);
        }

        public void UniGetUILogs_Click(object sender, RoutedEventArgs e) =>
            NavigateTo(PageType.OwnLog);

        private void HelpMenu_Click(object sender, RoutedEventArgs e) => ShowHelp();

        public void ShowHelp(string uriAttachment = "")
        {
            NavigateTo(PageType.Help);
            HelpPage?.NavigateTo(uriAttachment);
        }

        private void QuitUniGetUI_Click(object sender, RoutedEventArgs e) =>
            MainApp.Instance.DisposeAndQuit();

        private bool ResizingOPLayout;
        private int OpListChanges;

        bool isCollapsed;

        private void UpdateOperationsLayout()
        {
            OpListChanges++;

            ResizingOPLayout = true;
            int OpCount = MainApp.Operations._operationList.Count;
            int maxHeight = Math.Max((OpCount * 58) - 7, 0);

            ContentGrid.RowDefinitions[2].MaxHeight = maxHeight;

            if (OpCount > 0)
            {
                if (isCollapsed)
                {
                    ContentGrid.RowDefinitions[2].Height = new GridLength(0);
                    ContentGrid.RowDefinitions[1].Height = new GridLength(16);
                    OperationSplitter.Visibility = Visibility.Visible;
                    OperationSplitterMenuButton.Visibility = Visibility.Visible;
                    OperationSplitter.IsEnabled = false;
                }
                else
                {
                    ContentGrid.RowDefinitions[2].Height = new GridLength(Math.Min(maxHeight, 200));
                    ContentGrid.RowDefinitions[1].Height = new GridLength(16);
                    OperationSplitter.Visibility = Visibility.Visible;
                    OperationSplitterMenuButton.Visibility = Visibility.Visible;
                    OperationSplitter.IsEnabled = true;
                }
            }
            else
            {
                ContentGrid.RowDefinitions[1].Height = new GridLength(0);
                ContentGrid.RowDefinitions[2].Height = new GridLength(0);
                OperationSplitter.Visibility = Visibility.Collapsed;
                OperationSplitterMenuButton.Visibility = Visibility.Collapsed;
            }
            ResizingOPLayout = false;
        }

        private void OperationScrollView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ResizingOPLayout)
                return;

            if (OpListChanges > 0)
            {
                OpListChanges--;
            }
        }

        private void OperationSplitterMenuButton_Click(object sender, RoutedEventArgs e)
        {
            OperationListMenu.ShowAt(
                OperationSplitterMenuButton,
                new FlyoutShowOptions { ShowMode = FlyoutShowMode.Standard }
            );
        }

        private void ExpandCollapseOpList_Click(object sender, RoutedEventArgs e)
        {
            if (isCollapsed)
            {
                isCollapsed = false;
                ExpandCollapseOpList.Content = new FontIcon { Glyph = "\uE96E", FontSize = 14 };
                UpdateOperationsLayout();
            }
            else
            {
                isCollapsed = true;
                ExpandCollapseOpList.Content = new FontIcon { Glyph = "\uE96D", FontSize = 14 };
                UpdateOperationsLayout();
            }
        }

        private void CancellAllOps_Click(object sender, RoutedEventArgs e)
        {
            foreach (var widget in MainApp.Operations._operationList)
            {
                var operation = widget.Operation;
                if (operation.Status is OperationStatus.InQueue or OperationStatus.Running)
                    operation.Cancel();
            }
        }

        private void RetryFailedOps_Click(object sender, RoutedEventArgs e)
        {
            foreach (var widget in MainApp.Operations._operationList)
            {
                var operation = widget.Operation;
                if (operation.Status is OperationStatus.Failed)
                    operation.Retry(AbstractOperation.RetryMode.Retry);
            }
        }

        private void ClearSuccessfulOps_Click(object sender, RoutedEventArgs e)
        {
            foreach (var widget in MainApp.Operations._operationList.ToArray())
            {
                var operation = widget.Operation;
                if (operation.Status is OperationStatus.Succeeded)
                    widget.Close();
            }
        }

        private void NavigationView_SelectionChanged(
            NavigationView sender,
            NavigationViewSelectionChangedEventArgs args
        )
        {
            if (_lastNavItemSelectionWasAuto)
                return;

            if (args.SelectedItem is CustomNavViewItem item && item.Tag is not PageType.Null)
            {
                NavigateTo(item.Tag);
            }
        }

        private void MoreNavBtn_Tapped(
            object sender,
            Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e
        )
        {
            (VersionMenuItem as MenuFlyoutItem).Text = CoreTools.Translate(
                "UniGetUI Version {0}",
                CoreData.VersionName
            );
            MoreNavButtonMenu.ShowAt(sender as FrameworkElement);
        }

        internal void LoadBundleFromFile(string param)
        {
            NavigateTo(PageType.Bundles);
            BundlesPage?.OpenFromFile(param);
        }

        internal void LoadBundleFromString(
            string payload,
            BundleFormatType format,
            string source,
            int loadingId
        )
        {
            NavigateTo(PageType.Bundles);
            BundlesPage?.OpenFromString(payload, format, source, loadingId);
        }

        private void ClearAllFinished_OnClick(object sender, RoutedEventArgs e)
        {
            foreach (var widget in MainApp.Operations._operationList.ToArray())
            {
                var operation = widget.Operation;
                if (
                    operation.Status
                    is OperationStatus.Succeeded
                        or OperationStatus.Failed
                        or OperationStatus.Canceled
                )
                    widget.Close();
            }
        }
    }
}
