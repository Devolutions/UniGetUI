using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.PackageEngine.RemoteHosts;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class RemoteHostsViewModel : ViewModelBase
{
    public ObservableCollection<RemoteHost> Hosts { get; } = [];

    [ObservableProperty] private RemoteHost? _selectedHost;
    [ObservableProperty] private string _statusMessage = "";

    public RemoteHostsViewModel()
    {
        Reload();
        RemoteHostService.Instance.HostsChanged += (_, _) => Reload();
    }

    public void Reload()
    {
        Hosts.Clear();
        foreach (RemoteHost host in RemoteHostService.Instance.Hosts)
            Hosts.Add(host);
    }
}
