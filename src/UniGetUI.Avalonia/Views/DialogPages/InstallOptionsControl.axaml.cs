using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class InstallOptionsControl : UserControl
{
    private InstallOptionsViewModel ViewModel => (InstallOptionsViewModel)DataContext!;

    public InstallOptionsControl()
    {
        InitializeComponent();
    }

    public void FocusProfileSelector() => ProfileSelectorComboBox.Focus();

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

    // ── Manual install: copy the generated command / open a terminal to run it by hand ──

    private async void CopyCommand_Click(object? sender, RoutedEventArgs e)
    {
        var command = await ViewModel.BuildCurrentCommandAsync();
        if (string.IsNullOrWhiteSpace(command)) return;

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(command);

        if (sender is Button btn)
        {
            var original = btn.Content;
            btn.Content = "✓";
            await Task.Delay(1000);
            btn.Content = original;
        }
    }

    private async void Manual_Click(object? sender, RoutedEventArgs e)
    {
        var command = await ViewModel.BuildCurrentCommandAsync();
        if (string.IsNullOrWhiteSpace(command)) return;

        // Copies the command and opens a terminal with it pre-typed at the prompt.
        await ManualInstallHelper.LaunchManualAsync(command);
    }
}
