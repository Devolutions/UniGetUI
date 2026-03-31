using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class InstallOptionsPanel : UserControl
{
    private InstallOptionsPanelViewModel ViewModel => (InstallOptionsPanelViewModel)DataContext!;

    public event EventHandler? NavigateToAdministratorRequested;

    public InstallOptionsPanel(IPackageManager manager)
    {
        DataContext = new InstallOptionsPanelViewModel(manager);
        InitializeComponent();

        ViewModel.NavigateToAdministratorRequested += (s, e) =>
            NavigateToAdministratorRequested?.Invoke(s, e);

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InstallOptionsPanelViewModel.HasChanges))
            {
                if (ViewModel.HasChanges)
                    ApplyButton.Classes.Add("accent");
                else
                    ApplyButton.Classes.Remove("accent");
            }
        };

        // Wire location picker (needs Visual reference for StorageProvider)
        SelectDirButton.Click += (_, _) =>
            _ = ViewModel.SelectLocationCommand.ExecuteAsync(this);

        // Mark changed whenever the user edits a CLI textbox
        CustomInstallBox.TextChanged += (_, _) => ViewModel.MarkChanged();
        CustomUpdateBox.TextChanged += (_, _) => ViewModel.MarkChanged();
        CustomUninstallBox.TextChanged += (_, _) => ViewModel.MarkChanged();
    }
}
