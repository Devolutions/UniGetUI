using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
}
