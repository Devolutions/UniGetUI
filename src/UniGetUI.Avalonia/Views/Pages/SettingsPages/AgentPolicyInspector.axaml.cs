using Avalonia.Controls;
using Avalonia.Input.Platform;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
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
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    public void Dispose()
    {
        _viewModel.CopyTextRequested -= OnCopyTextRequested;
        _viewModel.Dispose();
    }
}
