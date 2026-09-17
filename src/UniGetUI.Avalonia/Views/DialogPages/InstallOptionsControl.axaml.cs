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
        ScrollViewer? scrollViewer = source?
            .FindAncestorOfType<TabControl>()?
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault(viewer => viewer.Name == "TabHeadersScrollViewer");
        if (scrollViewer is null) return;

        double maximum = Math.Max(0d,
            scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
        double target = Math.Clamp(scrollViewer.Offset.X + delta, 0d, maximum);
        scrollViewer.Offset = new Vector(target, scrollViewer.Offset.Y);
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
