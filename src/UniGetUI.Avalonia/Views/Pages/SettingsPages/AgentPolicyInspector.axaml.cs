using Avalonia.Controls;
using Avalonia.Input.Platform;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Avalonia.Views.Pages.SettingsPages.PolicyEditor;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class AgentPolicyInspector : UserControl, ISettingsPage, IAsyncLeaveGuard, IDisposable
{
    private readonly AgentPolicyInspectorViewModel _viewModel;
    private PolicyEditorSessionViewModel? _activeEditorSession;
    private PolicyEditorDialog? _activeEditorDialog;

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
        _viewModel.OpenPolicyEditorRequested += OnOpenPolicyEditorRequested;
        _ = _viewModel.LoadAsync();
        _ = _viewModel.LoadManagementAsync();
    }

    private async void OnCopyTextRequested(object? sender, string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private async void OnOpenPolicyEditorRequested(object? sender, PolicyEditorLaunchRequest request)
    {
        if (MainWindow.Instance is not { } owner) return;
        if (_activeEditorSession is not null) return;

        PolicyEditorSession session = request.Operation switch
        {
            PolicyEditorOperationKind.Update =>
                PolicyEditorSession.StartUpdate(request.Management),
            PolicyEditorOperationKind.ReplaceIdentity =>
                PolicyEditorSession.StartReplaceIdentity(request.Management, request.SeedDraft!),
            PolicyEditorOperationKind.Create =>
                PolicyEditorSession.StartCreate(request.Management, request.SeedDraft!),
            PolicyEditorOperationKind.Repair =>
                PolicyEditorSession.StartRepair(request.Management, request.SeedDraft!),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };

        var dialog = new PolicyEditorDialog();
        var sessionViewModel = new PolicyEditorSessionViewModel(
            session,
            new BrokerPolicyEditorValidationClient(),
            new PolicyEditorConfirmationPrompt(owner),
            new WindowsPolicyEditorWriteClient());

        var dialogViewModel = new PolicyEditorDialogViewModel(sessionViewModel);
        _activeEditorSession = sessionViewModel;
        _activeEditorDialog = dialog;

        try
        {
            dialog.DataContext = dialogViewModel;
            await dialog.ShowDialog(owner);
        }
        finally
        {
            _activeEditorSession = null;
            _activeEditorDialog = null;
            dialogViewModel.Dispose();
        }

        _ = _viewModel.LoadAsync();
        _ = _viewModel.LoadManagementAsync();
    }

    public async Task<bool> CanLeaveAsync(PageLeaveReason reason, CancellationToken cancellationToken = default)
    {
        if (_activeEditorSession is null) return true;

        if (_activeEditorSession.IsBusy)
        {
            // Blocker 31: navigating away or quitting while the policy editor still has a
            // validate/save/overwrite/raw-validation operation in flight must first try to cancel it
            // and wait a bounded amount of time, never silently tear the session down mid-flight, and
            // refuse (with an accessible busy status) if it does not settle in time.
            bool settled = await PolicyEditorSessionCloseGuard.TryCancelActiveOperationAsync(
                _activeEditorSession,
                PolicyEditorSessionCloseGuard.DefaultCancelWaitTimeout,
                cancellationToken);
            if (!settled)
            {
                PolicyEditorSessionCloseGuard.AnnounceCloseBlockedByBusyOperation();
                return false;
            }
        }

        bool canLeave = await _activeEditorSession.ConfirmDiscardAsync(cancellationToken);
        if (canLeave)
            _activeEditorDialog?.CloseAfterExternalDiscard();
        return canLeave;
    }

    public void Dispose()
    {
        _viewModel.CopyTextRequested -= OnCopyTextRequested;
        _viewModel.OpenPolicyEditorRequested -= OnOpenPolicyEditorRequested;
        _viewModel.Dispose();
    }
}
