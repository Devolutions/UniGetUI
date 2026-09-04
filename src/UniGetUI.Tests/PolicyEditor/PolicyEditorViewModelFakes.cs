using System.Text.Json;
using Devolutions.Now.Policy.Api;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

namespace UniGetUI.Tests.PolicyEditor;

/// <summary>Test doubles for the view-model's external seams. All are pure, in-memory fakes: no
/// network, no UI, no concrete Agent/broker dependency — matching the domain's own contract that
/// these must be interfaces, never concrete calls.</summary>
internal sealed class FakeValidationClient : IPolicyValidationClient
{
    public PolicyEditorValidationOutcome NextOutcome { get; set; } =
        new(new PolicyValidationResult
        {
            IsValid = true,
            ValidationReceipt = "receipt-1",
            CanonicalDraft = null,
            Findings = [],
        });

    /// <summary>When set, gates completion of <see cref="ValidateAsync"/> so a test can act while the
    /// call is still in flight (used for stale/cancellation generation-suppression tests at the
    /// view-model layer).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public int CallCount { get; private set; }
    public JsonElement LastDraft { get; private set; }

    public async Task<PolicyEditorValidationOutcome> ValidateAsync(JsonElement draft, CancellationToken cancellationToken)
    {
        CallCount++;
        LastDraft = draft.Clone();
        if (Gate is not null)
            await Gate.Task;
        return NextOutcome;
    }
}

internal sealed class FakeConfirmationPrompt : IPolicyEditorConfirmationPrompt
{
    public bool NextResult { get; set; } = true;
    public int CallCount { get; private set; }
    public PolicyEditorConfirmationRequest? LastRequest { get; private set; }
    public List<PolicyEditorConfirmationRequest> AllRequests { get; } = [];
    public TaskCompletionSource? Started { get; set; }
    public TaskCompletionSource? Gate { get; set; }
    public Action? BeforeReturn { get; set; }
    public Exception? NextException { get; set; }

    public async Task<bool> ConfirmAsync(
        PolicyEditorConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        AllRequests.Add(request);
        Started?.TrySetResult();
        if (Gate is not null)
            await Gate.Task.WaitAsync(cancellationToken);
        BeforeReturn?.Invoke();
        if (NextException is not null)
            throw NextException;
        return NextResult;
    }
}

internal sealed class FakeWriteClient : IPolicyWriteClient
{
    public PolicyWriteOutcome NextOutcome { get; set; } =
        PolicyWriteOutcome.Failure(PolicyWriteFailureKind.None);

    public int CallCount { get; private set; }
    public PolicyEditorWriteRequest? LastRequest { get; private set; }
    public TaskCompletionSource? Gate { get; set; }

    public async Task<PolicyWriteOutcome> WriteAsync(
        PolicyEditorWriteRequest request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        if (Gate is not null)
            await Gate.Task;
        return NextOutcome;
    }
}
