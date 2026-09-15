using Avalonia.Controls;
using Avalonia.Input.Platform;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class AgentPolicyInspector : UserControl, ISettingsPage, IDisposable
{
    private readonly AgentPolicyInspectorViewModel _viewModel;

    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Active package broker policy");

    public event EventHandler? RestartRequired { add { } remove { } }
    public event EventHandler<Type>? NavigationRequested { add { } remove { } }

    public AgentPolicyInspector()
    {
        _viewModel = new AgentPolicyInspectorViewModel();
        DataContext = _viewModel;
        InitializeComponent();

        _viewModel.CopyTextRequested += OnCopyTextRequested;
        _ = _viewModel.LoadAsync();
    }

    private async void OnCopyTextRequested(object? sender, string text)
    {
        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            Logger.Error("[AgentBroker] The policy inspector clipboard is unavailable.");
            _viewModel.ReportCopyFailure();
            return;
        }

        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            Logger.Error("[AgentBroker] Failed to copy the active policy JSON to the clipboard.");
            Logger.Error(ex);
            _viewModel.ReportCopyFailure();
        }
    }

    public void Dispose()
    {
        _viewModel.CopyTextRequested -= OnCopyTextRequested;
        _viewModel.Dispose();
    }
}
