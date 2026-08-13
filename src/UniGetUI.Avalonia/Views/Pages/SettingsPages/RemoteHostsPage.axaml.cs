using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.RemoteHosts;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class RemoteHostsPage : UserControl, ISettingsPage
{
    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Remote hosts");

    public event EventHandler? RestartRequired { add { } remove { } }
    public event EventHandler<Type>? NavigationRequested { add { } remove { } }

    private RemoteHostsViewModel VM => (RemoteHostsViewModel)DataContext!;

    public RemoteHostsPage()
    {
        DataContext = new RemoteHostsViewModel();
        InitializeComponent();

        AddButton.Click += async (_, _) => await EditHostAsync(null);
        EditButton.Click += async (_, _) =>
        {
            if (VM.SelectedHost is { } host)
                await EditHostAsync(host);
        };
        RemoveButton.Click += (_, _) =>
        {
            if (VM.SelectedHost is { } host)
                RemoteHostService.Instance.RemoveHost(host.Id);
        };
        TestButton.Click += async (_, _) => await TestSelectedAsync();
    }

    private async Task EditHostAsync(RemoteHost? existing)
    {
        if (MainWindow.Instance is not { } owner)
            return;

        var dialog = new RemoteHostEditorDialog(existing);
        await dialog.ShowDialog(owner);
        VM.Reload();
    }

    private async Task TestSelectedAsync()
    {
        if (VM.SelectedHost is null)
        {
            VM.StatusMessage = CoreTools.Translate("Select a host to test.");
            return;
        }

        VM.StatusMessage = CoreTools.Translate("Connecting…");
        try
        {
            var session = RemoteHostService.Instance.GetSession(VM.SelectedHost);
            string description = await session.TestConnectionAsync();
            VM.StatusMessage = CoreTools.Translate("Connected to {0}.", description);
        }
        catch (Exception ex)
        {
            VM.StatusMessage = ex.Message;
        }
    }
}
