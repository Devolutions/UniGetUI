using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.PackageEngine.RemoteHosts;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class WslDistroItem : ObservableObject
{
    private bool _suppressEnabled;

    public string Name { get; }
    public string StatusText { get; }

    [ObservableProperty] private bool _isEnabled;

    public WslDistroItem(WslDistroInfo distro, bool enabled)
    {
        Name = distro.Name;
        string defaultMark = distro.IsDefault ? " · default" : "";
        StatusText = $"{distro.State} · WSL {distro.Version}{defaultMark}";
        _suppressEnabled = true;
        IsEnabled = enabled;
        _suppressEnabled = false;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (_suppressEnabled)
            return;
        WslDistroCatalog.SetEnabled(Name, value);
        RemoteHostService.Instance.ReloadFromStore();
    }
}

public partial class RemoteHostsViewModel : ViewModelBase
{
    public ObservableCollection<RemoteHost> Hosts { get; } = [];
    public ObservableCollection<WslDistroItem> WslDistros { get; } = [];

    [ObservableProperty] private RemoteHost? _selectedHost;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isWslSectionVisible;
    [ObservableProperty] private bool _hasWslDistros;

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

        IsWslSectionVisible = OperatingSystem.IsWindows();
        WslDistros.Clear();
        if (IsWslSectionVisible)
        {
            HashSet<string> disabled = WslDistroCatalog.GetDisabledNames();
            foreach (WslDistroInfo distro in WslDistroCatalog.ListInstalled())
                WslDistros.Add(new WslDistroItem(distro, !disabled.Contains(distro.Name)));
        }

        HasWslDistros = WslDistros.Count > 0;
    }
}
