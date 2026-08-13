using Avalonia.Threading;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.RemoteHosts;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class RemoteHostEditorDialog : ImmersiveDialog
{
    private readonly RemoteHost? _existing;
    public bool Saved { get; private set; }

    public RemoteHostEditorDialog(RemoteHost? existing = null)
    {
        _existing = existing;
        InitializeComponent();
        Title = existing is null
            ? CoreTools.Translate("Add remote host")
            : CoreTools.Translate("Edit remote host");

        if (existing is not null)
        {
            NameBox.Text = existing.Name ?? "";
            DestinationBox.Text = existing.Destination;
        }

        CancelButton.Click += (_, _) => Close();
        SaveButton.Click += (_, _) => Save();
        TestButton.Click += async (_, _) => await TestAsync();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() => DestinationBox.Focus(), DispatcherPriority.Background);
    }

    private RemoteHost BuildHost()
    {
        return new RemoteHost(
            DestinationBox.Text ?? "",
            NameBox.Text,
            _existing?.Id
        );
    }

    private void Save()
    {
        try
        {
            RemoteHostService.Instance.SaveHost(BuildHost());
            Saved = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async Task TestAsync()
    {
        StatusText.Text = CoreTools.Translate("Connecting…");
        try
        {
            RemoteHost host = BuildHost();
            string description = await new RemoteHostSession(host).TestConnectionAsync();
            StatusText.Text = CoreTools.Translate("Connected to {0}.", description);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }
}
