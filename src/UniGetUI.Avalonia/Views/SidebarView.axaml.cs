using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Avalonia.Views;

public partial class SidebarView : BaseView<SidebarViewModel>
{
    private bool _lastNavItemSelectionWasAuto;
    private bool _isMoreFlyoutOpen;

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
        if (FlyoutBase.GetAttachedFlyout(MoreNavBtn) is { } moreFlyout)
        {
            moreFlyout.Opened += (_, _) => _isMoreFlyoutOpen = true;
            moreFlyout.Closed += (_, _) =>
            {
                _isMoreFlyoutOpen = false;
                SyncListBoxSelection(ViewModel?.SelectedPageType ?? PageType.Null);
            };
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SidebarViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SidebarViewModel.SelectedPageType))
                    SyncListBoxSelection(vm.SelectedPageType);
            };
            // The startup page may already be set before this view subscribes, so apply it now.
            SyncListBoxSelection(vm.SelectedPageType);
        }
    }

    private void SyncListBoxSelection(PageType page)
    {
        if (_isMoreFlyoutOpen)
            return;

        // Selection lives in two ListBoxes (main + footer); only one may hold a selection at a time.
        _lastNavItemSelectionWasAuto = true;
        NavListBox.SelectedItem = page switch
        {
            PageType.Discover => DiscoverNavBtn,
            PageType.Updates => UpdatesNavBtn,
            PageType.Installed => InstalledNavBtn,
            PageType.Bundles => BundlesNavBtn,
            _ => null,
        };
        FooterNavListBox.SelectedItem = page switch
        {
            PageType.Settings => SettingsNavBtn,
            PageType.Managers => ManagersNavBtn,
            _ => null,
        };
        _lastNavItemSelectionWasAuto = false;
    }

    private void NavListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => HandleNavSelectionChanged(NavListBox.SelectedItem);

    private void FooterNavListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => HandleNavSelectionChanged(FooterNavListBox.SelectedItem);

    private void HandleNavSelectionChanged(object? selectedItem)
    {
        if (_lastNavItemSelectionWasAuto) return;
        if (selectedItem is not ListBoxItem item || item.Tag is not string tag) return;

        if (tag == "More")
        {
            // Keep the item selected until the menu closes so its accent pill remains anchored.
            _isMoreFlyoutOpen = true;
            FlyoutBase.ShowAttachedFlyout(item);
            return;
        }

        if (Enum.TryParse<PageType>(tag, out var pageType))
            ViewModel?.RequestNavigation(pageType.ToString());
    }

    // One full gear rotation on click, mirroring the spin of WinUI's AnimatedSettingsVisualSource.
    // The TransformAnimator manages the icon's RenderTransform, so the animation runs on the Visual itself.
    private readonly Animation _settingsIconSpin = new()
    {
        Duration = TimeSpan.FromSeconds(0.5),
        Easing = new CubicEaseOut(),
        Children =
        {
            new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(RotateTransform.AngleProperty, 0d) } },
            new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(RotateTransform.AngleProperty, 360d) } },
        },
    };

    private void SettingsNavBtn_Tapped(object? sender, TappedEventArgs e)
        => _ = _settingsIconSpin.RunAsync(SettingsIcon);

    public void FocusSelectedItem()
    {
        if ((NavListBox.SelectedItem ?? FooterNavListBox.SelectedItem) is InputElement item)
            item.Focus();
        else
            NavListBox.Focus();
    }
}
