using Avalonia.Controls;
using Avalonia.Threading;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views.Pages.AboutPages;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class AboutWindow : UniGetUI.Avalonia.Views.DialogPages.ImmersiveDialog
{
    private readonly DirectionalSlideTransition _slide = new();
    private readonly Control[] _pages =
    [
        new AboutPage(),
        new ThirdPartyLicenses(),
        new Contributors(),
        new Translators(),
    ];
    private int _selectedIndex;

    public AboutWindow()
    {
        InitializeComponent();
        // Seed the first page before enabling transitions. Running a page transition while the
        // dialog is still detached from a visual root prevents the About dialog from opening.
        ContentFrame.Content = _pages[0];
        ContentFrame.PageTransition = _slide;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() => MainTabControl.Focus(), DispatcherPriority.Background);
    }

    private void MainTabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        int selectedIndex = MainTabControl.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= _pages.Length || selectedIndex == _selectedIndex)
            return;

        _slide.Reverse = selectedIndex < _selectedIndex;
        _selectedIndex = selectedIndex;
        ContentFrame.Content = _pages[selectedIndex];
    }
}
