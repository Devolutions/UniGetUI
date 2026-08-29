using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;
using UniGetUI.Avalonia.Views.DialogPages;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages.PolicyEditor;

/// <summary>
/// Modal structured/raw editor for a package broker policy draft. Hosted as an
/// <see cref="ImmersiveDialog"/> (not a settings page) so this Phase 2 surface never touches
/// <c>SettingsBasePage</c>'s page-navigation switch. <see cref="DataContext"/> must be a
/// <see cref="PolicyEditorDialogViewModel"/>.
/// </summary>
public partial class PolicyEditorDialog : ImmersiveDialog
{
    private PolicyEditorDialogViewModel? _viewModel;

    // Guards against RawEditor.TextChanged feeding back into the session while we are the ones
    // pushing Session.RawBuffer into the editor (mode switch, initial load, save/replace refresh).
    private bool _suppressRawSync;

    // Closing() re-raises the cancelable Closing event; this flag lets a confirmed close pass
    // through on the second call instead of asking the user again.
    private bool _closeConfirmed;
    private bool _closePromptPending;

    public PolicyEditorDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closing += OnClosing;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _viewModel = DataContext as PolicyEditorDialogViewModel;
        SyncEditorFromSession();
    }

    private void SyncEditorFromSession()
    {
        if (_viewModel is null) return;

        _suppressRawSync = true;
        try
        {
            RawEditor.Text = _viewModel.Session.RawBuffer;
        }
        finally
        {
            _suppressRawSync = false;
        }
    }

    private void RawEditor_TextChanged(object? sender, EventArgs e)
    {
        if (_suppressRawSync || _viewModel is null) return;
        _viewModel.Session.RawBuffer = RawEditor.Text ?? "";
    }

    private async void StructuredModeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.Session.SwitchToStructuredCommand.ExecuteAsync(null);
        if (_viewModel.Session.IsStructuredMode)
        {
            _viewModel.RefreshStructuredProjection();
        }
    }

    private void RawModeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.Session.SwitchToRawCommand.Execute(null);
        SyncEditorFromSession();
    }

    private void AddRuleButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _viewModel.Session.HasLocalInputErrors) return;
        _viewModel.Session.AddRuleCommand.Execute(null);
        _viewModel.RebuildRules();
    }

    private void DuplicateRuleButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null
            || _viewModel.Session.HasLocalInputErrors
            || GetRule(sender) is not { } rule) return;
        _viewModel.Session.DuplicateRuleCommand.Execute(rule.Rule);
        _viewModel.RebuildRules();
    }

    private void DeleteRuleButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null
            || _viewModel.Session.HasLocalInputErrors
            || GetRule(sender) is not { } rule) return;
        _viewModel.Session.DeleteRuleCommand.Execute(rule.Rule);
        _viewModel.RebuildRules();
    }

    private void MoveRuleUpButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null
            || _viewModel.Session.HasLocalInputErrors
            || GetRule(sender) is not { } rule) return;
        _viewModel.Session.MoveRuleUpCommand.Execute(rule.Rule);
        _viewModel.RebuildRules();
    }

    private void MoveRuleDownButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null
            || _viewModel.Session.HasLocalInputErrors
            || GetRule(sender) is not { } rule) return;
        _viewModel.Session.MoveRuleDownCommand.Execute(rule.Rule);
        _viewModel.RebuildRules();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private static PolicyEditorRuleUi? GetRule(object? sender) =>
        (sender as Control)?.DataContext as PolicyEditorRuleUi;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed) return;
        e.Cancel = true;
        _ = ConfirmAndCloseAsync();
    }

    private async Task ConfirmAndCloseAsync()
    {
        if (_closePromptPending) return;
        _closePromptPending = true;
        try
        {
            if (_viewModel is not null && _viewModel.Session.IsBusy)
            {
                // Blocker 31: never abandon a session while a command can still mutate it. Ask the
                // in-flight validate/save/overwrite/raw-validation operation to cancel and give it a
                // bounded window to actually settle before deciding whether the dirty prompt (or an
                // outright refusal) is appropriate.
                bool settled = await PolicyEditorSessionCloseGuard.TryCancelActiveOperationAsync(
                    _viewModel.Session,
                    PolicyEditorSessionCloseGuard.DefaultCancelWaitTimeout);
                if (!settled)
                {
                    PolicyEditorSessionCloseGuard.AnnounceCloseBlockedByBusyOperation();
                    return;
                }
            }

            bool canDiscard = _viewModel is null || await _viewModel.Session.ConfirmDiscardAsync();
            if (!canDiscard) return;

            _closeConfirmed = true;
            Close();
        }
        finally
        {
            _closePromptPending = false;
        }
    }

    public void CloseAfterExternalDiscard()
    {
        _closeConfirmed = true;
        Close();
    }
}
