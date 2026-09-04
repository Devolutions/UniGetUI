using System.Text.Json;
using Devolutions.Now.Policy.Api;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

namespace UniGetUI.Tests.PolicyEditor;

/// <summary>
/// Covers <see cref="PolicyEditorSessionCloseGuard"/>: closing the policy editor dialog (or navigating
/// away/quitting via <c>IAsyncLeaveGuard.CanLeaveAsync</c>) while a validate/save/overwrite/raw-validation
/// operation is in flight must first try to cancel that operation and wait a bounded amount of time for
/// it to actually settle, rather than either abandoning the session mid-flight (while a command could
/// still mutate it) or refusing unconditionally. If the operation does not honor cancellation within the
/// bound (e.g. an unresponsive elevated-helper exchange), the guard must report failure so the caller
/// refuses to close/leave with an accessible busy status and keeps the session alive.
/// </summary>
public class PolicyEditorSessionCloseGuardTests
{
    private static readonly TimeSpan ShortBound = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task CloseDuringValidation_WhenOperationHonorsCancellation_SettlesWithinBound()
    {
        var validation = new CancelAwareValidationClient();
        using PolicyEditorSessionViewModel viewModel = CreateViewModel(validation);

        Task validateTask = viewModel.ValidateCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsBusy);

        bool settled = await PolicyEditorSessionCloseGuard.TryCancelActiveOperationAsync(viewModel, ShortBound);

        Assert.True(settled);
        Assert.False(viewModel.IsBusy);
        await validateTask;
    }

    [Fact]
    public async Task CloseDuringValidation_WhenOperationIgnoresCancellation_RefusesAfterBoundAndKeepsSessionAlive()
    {
        var validation = new FakeValidationClient { Gate = new TaskCompletionSource() };
        using PolicyEditorSessionViewModel viewModel = CreateViewModel(validation);

        Task validateTask = viewModel.ValidateCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsBusy);

        bool settled = await PolicyEditorSessionCloseGuard.TryCancelActiveOperationAsync(viewModel, ShortBound);

        // The fake never observes cancellation, so the bound elapses first: the guard must refuse
        // rather than let the caller tear down a session a command can still mutate.
        Assert.False(settled);
        Assert.True(viewModel.IsBusy);

        // Release the gate so the still-pending command settles and the test host does not hang.
        validation.Gate.TrySetResult();
        await validateTask;
    }

    [Fact]
    public async Task CloseDuringWrite_WhenHelperExchangeHonorsCancellation_SettlesWithinBound()
    {
        var validation = new FakeValidationClient();
        var writer = new CancelAwareWriteClient();
        using PolicyEditorSessionViewModel viewModel = CreateViewModelForSave(validation, writer);

        Task saveTask = viewModel.SaveCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsBusy);

        bool settled = await PolicyEditorSessionCloseGuard.TryCancelActiveOperationAsync(viewModel, ShortBound);

        Assert.True(settled);
        Assert.False(viewModel.IsBusy);
        await saveTask;
    }

    [Fact]
    public async Task CloseDuringWrite_WhenHelperExchangeIgnoresCancellation_RefusesAfterBoundAndKeepsSessionAlive()
    {
        var validation = new FakeValidationClient();
        var writer = new FakeWriteClient { Gate = new TaskCompletionSource() };
        using PolicyEditorSessionViewModel viewModel = CreateViewModelForSave(validation, writer);

        Task saveTask = viewModel.SaveCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsBusy);

        bool settled = await PolicyEditorSessionCloseGuard.TryCancelActiveOperationAsync(viewModel, ShortBound);

        Assert.False(settled);
        Assert.True(viewModel.IsBusy);

        writer.Gate.TrySetResult();
        await saveTask;
    }

    [Fact]
    public async Task ShutdownDuringConfirmation_CancelsPromptAndSettlesWithinBound()
    {
        var validation = new FakeValidationClient();
        var prompt = new CancelAwareConfirmationPrompt();
        using PolicyEditorSessionViewModel viewModel =
            CreateViewModelForSave(validation, new FakeWriteClient(), prompt);

        Task saveTask = viewModel.SaveCommand.ExecuteAsync(null);
        await prompt.Started.Task.WaitAsync(ShortBound);

        bool settled = await PolicyEditorSessionCloseGuard.TryCancelActiveOperationAsync(
            viewModel,
            ShortBound);

        Assert.True(settled);
        Assert.False(viewModel.IsBusy);
        await saveTask;
    }

    [Fact]
    public async Task CloseDuringDispatchedWrite_UnknownResultAndDeclinedDiscard_BlockRetryUntilRefresh()
    {
        var validation = new FakeValidationClient();
        var writer = new UnknownOnCancellationWriteClient();
        var prompt = new FakeConfirmationPrompt { NextResult = true };
        using PolicyEditorSessionViewModel viewModel =
            CreateViewModelForSave(validation, writer, prompt);
        viewModel.Draft.Metadata.Description = "keep this draft";
        viewModel.NotifyDraftChangedCommand.Execute(null);
        validation.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
        {
            IsValid = true,
            ValidationReceipt = "receipt-unknown",
            CanonicalDraft = PolicyEditorMapper.ToSharedDraft(viewModel.Draft),
            Findings = [],
        });

        Task saveTask = viewModel.SaveCommand.ExecuteAsync(null);
        await writer.Started.Task.WaitAsync(ShortBound);
        bool settled = await PolicyEditorSessionCloseGuard.TryCancelActiveOperationAsync(
            viewModel,
            ShortBound);
        await saveTask;

        Assert.True(settled);
        Assert.True(viewModel.RequiresManagementRefresh);
        Assert.Equal(PolicyWriteFailureKind.WriteResultUnknown, viewModel.LastWriteFailureKind);
        Assert.Equal("keep this draft", viewModel.Draft.Metadata.Description);
        prompt.NextResult = false;
        Assert.False(await viewModel.ConfirmDiscardAsync());
        Assert.False(viewModel.SaveCommand.CanExecute(null));

        using PolicyEditorSessionViewModel refreshed = CreateViewModel();
        Assert.True(refreshed.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task NoOperationInFlight_SettlesImmediatelyWithoutTouchingTheSession()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();

        bool settled = await PolicyEditorSessionCloseGuard.TryCancelActiveOperationAsync(viewModel, ShortBound);

        Assert.True(settled);
        Assert.False(viewModel.IsBusy);
    }

    private static PolicyEditorSessionViewModel CreateViewModel(IPolicyValidationClient? validation = null) =>
        CreateViewModelForSave(validation ?? new FakeValidationClient(), new FakeWriteClient());

    private static PolicyEditorSessionViewModel CreateViewModelForSave(
        IPolicyValidationClient validation,
        IPolicyWriteClient writer,
        IPolicyEditorConfirmationPrompt? prompt = null)
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("test-policy", "Contoso");
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            draft);
        var viewModel = new PolicyEditorSessionViewModel(
            session,
            validation,
            prompt ?? new FakeConfirmationPrompt(),
            writer);

        // A SaveCommand execution always validates first when there is no current validation result;
        // give it one with a non-null CanonicalDraft up front so the save proceeds straight into the
        // write/helper exchange this test actually wants to exercise.
        if (validation is FakeValidationClient fake)
        {
            fake.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
            {
                IsValid = true,
                ValidationReceipt = "receipt-close-guard",
                CanonicalDraft = PolicyEditorMapper.ToSharedDraft(viewModel.Draft),
            });
        }

        return viewModel;
    }

    /// <summary>
    /// Validation fake that honors cancellation cooperatively (unlike <see cref="FakeValidationClient"/>'s
    /// unconditional <c>Gate</c>), to exercise the "cancellation is observed within the bound" branch of
    /// the close guard.
    /// </summary>
    private sealed class CancelAwareValidationClient : IPolicyValidationClient
    {
        public async Task<PolicyEditorValidationOutcome> ValidateAsync(
            JsonElement draft,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected: the close guard canceled us, and we must actually settle promptly.
            }

            return new PolicyEditorValidationOutcome(null, ErrorCode.InternalError);
        }
    }

    /// <summary>
    /// Write-client fake standing in for the elevated-helper exchange that honors cancellation
    /// cooperatively, unlike <see cref="FakeWriteClient"/>'s unconditional <c>Gate</c>.
    /// </summary>
    private sealed class CancelAwareWriteClient : IPolicyWriteClient
    {
        public async Task<PolicyWriteOutcome> WriteAsync(
            PolicyEditorWriteRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected: the close guard canceled us, and we must actually settle promptly.
            }

            return PolicyWriteOutcome.Failure(PolicyWriteFailureKind.None);
        }
    }

    private sealed class CancelAwareConfirmationPrompt : IPolicyEditorConfirmationPrompt
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> ConfirmAsync(
            PolicyEditorConfirmationRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            return false;
        }
    }

    private sealed class UnknownOnCancellationWriteClient : IPolicyWriteClient
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PolicyWriteOutcome> WriteAsync(
            PolicyEditorWriteRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            return PolicyWriteOutcome.Failure(PolicyWriteFailureKind.WriteResultUnknown);
        }
    }
}
