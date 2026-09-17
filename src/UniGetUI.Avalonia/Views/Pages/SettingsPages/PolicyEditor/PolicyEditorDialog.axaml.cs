using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        _viewModel?.FindingNavigationRequested -= OnFindingNavigationRequested;
        _viewModel = DataContext as PolicyEditorDialogViewModel;
        _viewModel?.FindingNavigationRequested += OnFindingNavigationRequested;
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

    private void ClearValidFromButton_Click(object? sender, RoutedEventArgs e) =>
        _viewModel?.Document.ClearValidFrom();

    private void ClearValidUntilButton_Click(object? sender, RoutedEventArgs e) =>
        _viewModel?.Document.ClearValidUntil();

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
        PolicyEditorDraftRule moved = rule.Rule;
        _viewModel.Session.MoveRuleUpCommand.Execute(rule.Rule);
        _viewModel.RebuildRules();
        _viewModel.AnnounceRulePosition(moved);
        FocusMovedRule(moved);
    }

    private void MoveRuleDownButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null
            || _viewModel.Session.HasLocalInputErrors
            || GetRule(sender) is not { } rule) return;
        PolicyEditorDraftRule moved = rule.Rule;
        _viewModel.Session.MoveRuleDownCommand.Execute(rule.Rule);
        _viewModel.RebuildRules();
        _viewModel.AnnounceRulePosition(moved);
        FocusMovedRule(moved);
    }

    private void FocusMovedRule(PolicyEditorDraftRule moved)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Expander? expander = this.GetVisualDescendants()
                .OfType<Expander>()
                .FirstOrDefault(control =>
                    control.DataContext is PolicyEditorRuleUi rule
                    && ReferenceEquals(rule.Rule, moved));
            expander?.BringIntoView();
            expander?.Focus();
        }, DispatcherPriority.Loaded);
    }

    private async void RuleDecision_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null
            || sender is not ComboBox { DataContext: PolicyEditorRuleUi rule } selector)
        {
            return;
        }

        await _viewModel.Session.ChangeRuleDecisionAsync(rule, selector.SelectedIndex);
    }

    private void FindingNavigateButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null
            || (sender as Control)?.DataContext is not PolicyValidationFinding finding)
        {
            return;
        }

        NavigateToFinding(finding);
    }

    private void PreviousFindingButton_Click(object? sender, RoutedEventArgs e) =>
        _viewModel?.SelectPreviousFinding();

    private void NextFindingButton_Click(object? sender, RoutedEventArgs e) =>
        _viewModel?.SelectNextFinding();

    private void TopFindingNavigateButton_Click(object? sender, RoutedEventArgs e) =>
        _viewModel?.NavigateToSelectedFinding();

    private void OnFindingNavigationRequested(
        object? sender,
        PolicyValidationFinding? finding)
    {
        if (finding is null)
        {
            RawEditor.BringIntoView();
            RawEditor.Focus();
            return;
        }

        NavigateToFinding(finding);
    }

    private void NavigateToFinding(PolicyValidationFinding finding)
    {
        if (_viewModel is null) return;
        if (_viewModel.Session.IsRawMode)
        {
            RawEditor.BringIntoView();
            RawEditor.Focus();
            return;
        }

        Control searchRoot = this;
        if (TryGetRuleIndex(finding.Pointer, out int ruleIndex)
            && ruleIndex >= 0
            && ruleIndex < _viewModel.Rules.Count)
        {
            PolicyEditorRuleUi rule = _viewModel.Rules[ruleIndex];
            Expander? expander = this.GetVisualDescendants()
                .OfType<Expander>()
                .FirstOrDefault(control => ReferenceEquals(control.DataContext, rule));
            if (expander is not null)
            {
                expander.IsExpanded = true;
                searchRoot = expander;
            }
            else
            {
                return;
            }
        }

        string normalizedPointer = NormalizeRulePointer(finding.Pointer);
        Dispatcher.UIThread.Post(
            () => FocusBestMatchingControl(searchRoot, normalizedPointer),
            DispatcherPriority.Loaded);
    }

    private void RawSyntaxNavigateButton_Click(object? sender, RoutedEventArgs e)
    {
        RawEditor.BringIntoView();
        RawEditor.Focus();
    }

    private static void FocusBestMatchingControl(Control root, string pointer)
    {
        Control? target = root.GetVisualDescendants()
            .OfType<Control>()
            .Where(control => control.Tag is string tag && PointerTargetsTag(pointer, tag))
            .OrderByDescending(control => ((string)control.Tag!).Length)
            .FirstOrDefault();
        target ??= root;
        target.BringIntoView();
        target.Focus();
    }

    internal static bool PointerTargetsTag(string pointer, string tag) =>
        pointer.Equals(tag, StringComparison.OrdinalIgnoreCase)
        || (pointer.StartsWith(tag, StringComparison.OrdinalIgnoreCase)
            && pointer.Length > tag.Length
            && pointer[tag.Length] == '/');

    internal static bool TryGetRuleIndex(string pointer, out int index)
    {
        index = -1;
        string[] segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2
            && segments[0].Equals("Rules", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(segments[1], out index);
    }

    internal static string NormalizeRulePointer(string pointer)
    {
        string[] segments = pointer.Split('/');
        if (segments.Length >= 3
            && segments[1].Equals("Rules", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(segments[2], out _))
        {
            segments[1] = "Rules";
            segments[2] = "*";
        }

        return string.Join('/', segments);
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
