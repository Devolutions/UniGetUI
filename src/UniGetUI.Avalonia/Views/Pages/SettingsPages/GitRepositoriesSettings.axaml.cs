using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class GitRepositoriesSettings : UserControl, ISettingsPage
{
    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Git Repositories");

    public event EventHandler? RestartRequired { add { } remove { } }
    public event EventHandler<Type>? NavigationRequested { add { } remove { } }

    public GitRepositoriesSettings()
    {
        DataContext = new GitRepositoriesSettingsViewModel();
        InitializeComponent();

        foreach ((string label, string value) in GitRepositoriesSettingsViewModel.CheckIntervalItems)
            GitCheckIntervalCard.AddItem(label, value);
        GitCheckIntervalCard.ShowAddedItems();
    }
}
