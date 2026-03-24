using System.Collections.ObjectModel;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public record ManagerButtonInfo(string DisplayName, string StatusText);

public partial class ManagersHomepageViewModel : ViewModelBase
{
    public ObservableCollection<ManagerButtonInfo> Managers { get; } = new();
}
