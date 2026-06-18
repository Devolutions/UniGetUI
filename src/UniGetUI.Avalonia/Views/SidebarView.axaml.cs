using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Avalonia.Views;

public partial class SidebarView : BaseView<SidebarViewModel>
{
    private bool _lastNavItemSelectionWasAuto;

    /// <summary>
    /// Whether the nav item text labels are shown. False renders an icon-only rail; true renders the
    /// full labeled pane. Decoupled from the view-model's pane state so the same view can be used both
    /// as the always-visible rail and as the sliding flyout simultaneously.
    /// </summary>
    public static readonly StyledProperty<bool> ShowLabelsProperty =
        AvaloniaProperty.Register<SidebarView, bool>(nameof(ShowLabels), defaultValue: true);

    public bool ShowLabels
    {
        get => GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    public SidebarView()
    {
        InitializeComponent();
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
            ViewModel?.RequestNavigation(pageType.ToString());
    }

    public void FocusSelectedItem()
    {
        if (NavListBox.SelectedItem is InputElement item)
            item.Focus();
        else
            NavListBox.Focus();
    }
}
