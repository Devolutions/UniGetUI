using Avalonia.Automation;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

/// <summary>
/// Shared "can we close/navigate away now" guard for every surface that hosts a
/// <see cref="PolicyEditorSessionViewModel"/> (the modal <c>PolicyEditorDialog</c> window's own
/// Closing event, and <c>AgentPolicyInspector</c>'s <c>IAsyncLeaveGuard.CanLeaveAsync</c> for
/// page navigation/app shutdown while the dialog is open).
/// </summary>
/// <remarks>
/// Blocker 31: closing/navigating/quitting while a remote policy operation
/// (validate/save/overwrite/raw-validation, and transitively the elevated-helper write exchange) is
/// in flight must first try to <em>cancel</em> that operation and wait a bounded amount of time for
/// it to actually settle, instead of either abruptly tearing down the session mid-flight or leaving
/// the caller no better off than an unconditional refusal. If the in-flight operation does not settle
/// within <see cref="DefaultCancelWaitTimeout"/> (e.g. the elevated helper is unresponsive), the guard
/// reports failure so the caller can refuse the close/leave with an accessible busy status instead of
/// silently discarding a session a command could still be mutating.
/// </remarks>
public static class PolicyEditorSessionCloseGuard
{
    /// <summary>
    /// How long to wait for a canceled in-flight operation to actually observe the cancellation and
    /// unwind (release <see cref="PolicyEditorSessionViewModel.IsBusy"/>) before giving up and treating
    /// the session as still busy.
    /// </summary>
    public static readonly TimeSpan DefaultCancelWaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// If <paramref name="session"/> currently has a remote operation in flight, requests its
    /// cancellation and waits up to <paramref name="timeout"/> for it to settle.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if no operation was running, or the running operation settled within
    /// <paramref name="timeout"/>; <see langword="false"/> if it is still running once the bound
    /// elapses (the caller must refuse to close/leave and must not dispose or clear the session).
    /// </returns>
    public static async Task<bool> TryCancelActiveOperationAsync(
        PolicyEditorSessionViewModel session,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        IAsyncRelayCommand? running = FindRunningCommand(session);
        if (running is null)
            return true;

        if (running.CanBeCanceled)
        {
            running.Cancel();
        }

        Task? executionTask = running.ExecutionTask;
        if (executionTask is null || executionTask.IsCompleted)
            return true;

        Task delay = Task.Delay(timeout, cancellationToken);
        Task completed = await Task.WhenAny(executionTask, delay).ConfigureAwait(false);
        if (completed != executionTask)
            return false;

        // Accessing Exception observes a fault without turning close handling into an error sink.
        _ = executionTask.Exception;

        return true;
    }

    private static IAsyncRelayCommand? FindRunningCommand(PolicyEditorSessionViewModel session)
    {
        if (session.ValidateCommand.IsRunning) return session.ValidateCommand;
        if (session.SaveCommand.IsRunning) return session.SaveCommand;
        if (session.ConfirmOverwriteCommand.IsRunning) return session.ConfirmOverwriteCommand;
        if (session.SwitchToStructuredCommand.IsRunning) return session.SwitchToStructuredCommand;
        return null;
    }

    /// <summary>
    /// Announces (accessibly, via <see cref="AccessibilityAnnouncementService"/>) that a close or
    /// navigate-away request was refused because the in-flight policy operation could not be
    /// canceled within <see cref="DefaultCancelWaitTimeout"/>. Call this whenever
    /// <see cref="TryCancelActiveOperationAsync"/> returns <see langword="false"/>.
    /// </summary>
    public static void AnnounceCloseBlockedByBusyOperation()
    {
        AccessibilityAnnouncementService.Announce(
            CoreTools.Translate("The current policy operation could not be canceled in time. Please wait, then try closing again."),
            AutomationLiveSetting.Assertive);
    }
}
