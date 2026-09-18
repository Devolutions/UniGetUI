using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class InstallOptionsControl : UserControl
{
    private const double TabScrollStep = 160d;

    private InstallOptionsViewModel ViewModel => (InstallOptionsViewModel)DataContext!;

    public InstallOptionsControl()
    {
        InitializeComponent();
    }

    public void FocusProfileSelector() => ProfileSelectorComboBox.Focus();

    private void PreviousTabButton_Click(object? sender, RoutedEventArgs e)
        => ScrollTabHeaders(sender as Control, -TabScrollStep);

    private void NextTabButton_Click(object? sender, RoutedEventArgs e)
        => ScrollTabHeaders(sender as Control, TabScrollStep);

    private static void ScrollTabHeaders(Control? source, double delta)
    {
        TabControl? tabControl = source?.FindAncestorOfType<TabControl>();
        ScrollViewer? scrollViewer = FindTabHeadersScrollViewer(tabControl);
        if (tabControl is null || scrollViewer is null) return;

        double maximum = Math.Max(0d,
            scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
        double target = Math.Clamp(scrollViewer.Offset.X + delta, 0d, maximum);
        scrollViewer.Offset = new Vector(target, scrollViewer.Offset.Y);
        UpdateTabScrollButtons(tabControl);
    }

    private void InstallOptionsTabs_LayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is TabControl tabControl)
            UpdateTabScrollButtons(tabControl);
    }

    private static ScrollViewer? FindTabHeadersScrollViewer(TabControl? tabControl)
        => tabControl?
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault(viewer => viewer.Name == "TabHeadersScrollViewer");

    private static void UpdateTabScrollButtons(TabControl tabControl)
    {
        ScrollViewer? scrollViewer = FindTabHeadersScrollViewer(tabControl);
        if (scrollViewer?.Parent is not Grid headerGrid) return;

        Button? previous = headerGrid.Children
            .OfType<Button>()
            .FirstOrDefault(button => button.Name == "PreviousTabButton");
        Button? next = headerGrid.Children
            .OfType<Button>()
            .FirstOrDefault(button => button.Name == "NextTabButton");
        if (previous is null || next is null) return;

        // Decide overflow against the full header width, not the reduced viewport while the
        // chevrons are visible, otherwise the buttons can latch themselves on.
        bool hasOverflow = scrollViewer.Extent.Width > headerGrid.Bounds.Width + 0.5;
        previous.IsVisible = hasOverflow;
        next.IsVisible = hasOverflow;

        if (!hasOverflow)
        {
            previous.IsEnabled = false;
            next.IsEnabled = false;
            if (scrollViewer.Offset.X != 0)
                scrollViewer.Offset = new Vector(0, scrollViewer.Offset.Y);
            return;
        }

        double maximum = Math.Max(0d,
            scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
        previous.IsEnabled = scrollViewer.Offset.X > 0.5;
        next.IsEnabled = scrollViewer.Offset.X < maximum - 0.5;
    }

    private async void SelectDir_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var results = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { AllowMultiple = false });
        if (results is [{ } folder])
            ViewModel.LocationText = folder.TryGetLocalPath() ?? folder.Name;
    }

    private void KillProcessBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter or Key.OemComma)
            ViewModel.AddKillProcessCommand.Execute(null);
    }

    // Opens a terminal with the generated command pre-typed at the prompt, ready to run by hand.
    private async void Manual_Click(object? sender, RoutedEventArgs e)
    {
        var command = await ViewModel.BuildCurrentCommandAsync();
        if (string.IsNullOrWhiteSpace(command)) return;

        await ManualInstallHelper.LaunchManualAsync(command);
    }
}
