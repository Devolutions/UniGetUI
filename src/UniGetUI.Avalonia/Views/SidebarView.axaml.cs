using Avalonia.Controls;
using Avalonia.Interactivity;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views;

public partial class SidebarView : BaseView<SidebarViewModel>
{
    private bool _lastNavItemSelectionWasAuto;

    public SidebarView()
    {
        InitializeComponent();
        VersionMenuItem.Header = CoreTools.Translate("WingetUI Version {0}", CoreData.VersionName);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SidebarViewModel vm)
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SidebarViewModel.SelectedPageType))
                    SyncListBoxSelection(vm.SelectedPageType);
            };
    }

    private void SyncListBoxSelection(PageType page)
    {
        _lastNavItemSelectionWasAuto = true;
        NavListBox.SelectedItem = page switch
        {
            PageType.Discover => DiscoverNavBtn,
            PageType.Updates => UpdatesNavBtn,
            PageType.Installed => InstalledNavBtn,
            PageType.Bundles => BundlesNavBtn,
            _ => null,
        };
        _lastNavItemSelectionWasAuto = false;
    }

    private void NavListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_lastNavItemSelectionWasAuto) return;
        if (NavListBox.SelectedItem is ListBoxItem item && item.Tag is string tag
            && Enum.TryParse<PageType>(tag, out var pageType))
            ViewModel?.RequestNavigation(pageType);
    }

    private void SettingsNavBtn_Click(object? sender, RoutedEventArgs e) =>
        ViewModel?.RequestNavigation(PageType.Settings);

    private void ManagersNavBtn_Click(object? sender, RoutedEventArgs e) =>
        ViewModel?.RequestNavigation(PageType.Managers);

    private void UniGetUILogs_Click(object? sender, RoutedEventArgs e) =>
        ViewModel?.RequestNavigation(PageType.OwnLog);

    private void ManagerLogsMenu_Click(object? sender, RoutedEventArgs e) =>
        ViewModel?.RequestNavigation(PageType.ManagerLog);

    private void OperationHistoryMenu_Click(object? sender, RoutedEventArgs e) =>
        ViewModel?.RequestNavigation(PageType.OperationHistory);

    private void ReleaseNotesMenu_Click(object? sender, RoutedEventArgs e) =>
        ViewModel?.RequestNavigation(PageType.ReleaseNotes);

    private void HelpMenu_Click(object? sender, RoutedEventArgs e) =>
        ViewModel?.RequestNavigation(PageType.Help);

    private void AboutNavButton_Click(object? sender, RoutedEventArgs e) =>
        ViewModel?.RequestNavigation(PageType.About);

    private void QuitUniGetUI_Click(object? sender, RoutedEventArgs e) =>
        ViewModel?.RequestNavigation(PageType.Quit);
}
