using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Automation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Model;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.AgentBroker;
using UniGetUI.PackageEngine.AgentBroker.PolicyManagement;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;
using PolicyArchitecture = Devolutions.Now.Policy.Model.Architecture;
using PolicyDecision = Devolutions.Now.Policy.Model.Decision;
using PolicyElevation = Devolutions.Now.Policy.Model.Elevation;
using PolicyManagerName = Devolutions.Now.Policy.Model.ManagerName;
using PolicyOperation = Devolutions.Now.Policy.Model.Operation;
using PolicyScope = Devolutions.Now.Policy.Model.Scope;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public sealed record PolicyDetailRow(string Label, string Value)
{
    public string AutomationName => $"{Label}: {Value}";
}

/// <summary>
/// Raised by <see cref="AgentPolicyInspectorViewModel"/> when the user chooses Edit/Create/Repair/Replace
/// identity. Carries everything the (view-owned) dialog launcher needs to construct a
/// <c>PolicyEditorSession</c> without the view model itself depending on any Avalonia window/dialog type.
/// <see cref="SeedDraft"/> is populated for Create/Repair/ReplaceIdentity (there is no existing valid
/// draft to derive from); Update leaves it null since <c>PolicyEditorSession.StartUpdate</c> derives the
/// draft from <see cref="Management"/> itself.
/// </summary>
public sealed record PolicyEditorLaunchRequest(
    PolicyEditorOperationKind Operation,
    PolicyManagementSnapshot Management,
    PolicyEditorDraftDocument? SeedDraft = null);

public sealed record PolicyCopyRequest(string Text, long PageGeneration);

public sealed class PolicyRuleViewModel
{
    public required string AutomationName { get; init; }
    public required string Id { get; init; }
    public required string Enabled { get; init; }
    public required string Priority { get; init; }
    public required string Decision { get; init; }
    public required string Reason { get; init; }
    public required bool HasConstraints { get; init; }
    public required IReadOnlyList<PolicyDetailRow> MatchRows { get; init; }
    public required IReadOnlyList<PolicyDetailRow> ConstraintRows { get; init; }
}

public partial class AgentPolicyInspectorViewModel : ViewModelBase, IDisposable
{
    private readonly Action<string?, AutomationLiveSetting> _announce;
    private readonly IBrokerPolicyManagementService _managementService;
    private readonly IPolicyWriteElevationEligibility _writeElevationEligibility;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _managementRefreshCancellation;
    private CancellationTokenSource? _pageRefreshCancellation;
    private long _pageRefreshGeneration;
    private long _managementRefreshGeneration;
    private long _appliedManagementGeneration;
    private int _isDisposed;
    private PolicyManagementSnapshot? _managementSnapshot;

    /// <summary>Single page-level status for policy management and rendering.</summary>
    public InfoBarViewModel ManagementStatus { get; } = new()
    {
        IsClosable = false,
        IsOpen = true,
    };

    public ObservableCollection<PolicyDetailRow> MetadataRows { get; } = [];
    public ObservableCollection<PolicyDetailRow> EnforcementRows { get; } = [];
    public ObservableCollection<PolicyRuleViewModel> Rules { get; } = [];

    /// <summary>Sanitized Invalid-state findings, or empty when the snapshot is not Invalid.</summary>
    public ObservableCollection<PolicyDetailRow> ManagementDiagnosticsRows { get; } = [];

    [ObservableProperty] private bool _isPageRefreshActive;
    [ObservableProperty] private bool _hasActivePolicyDetails;
    [ObservableProperty] private bool _hasPolicy;
    [ObservableProperty] private bool _hasNoRules;
    [ObservableProperty] private string _rawJson = "";

    [ObservableProperty] private bool _isManagementLoading;
    [ObservableProperty] private bool _hasManagementSnapshot;
    [ObservableProperty] private string _managementStateText = "";
    [ObservableProperty] private string _managementConfiguredPath = "";
    [ObservableProperty] private string _managementSourceText = "";
    [ObservableProperty] private string _agentWriteCapabilityText = "";
    [ObservableProperty] private string _policyChangesFromThisAppText = "";
    [ObservableProperty] private string _policyChangesReasonText = "";
    [ObservableProperty] private bool _managementElevationRequired;
    [ObservableProperty] private string _managementElevationRequiredText = "";
    [ObservableProperty] private bool _canEdit;
    [ObservableProperty] private bool _canCreate;
    [ObservableProperty] private bool _canRepair;
    [ObservableProperty] private bool _canReplaceIdentity;
    [ObservableProperty] private bool _hasManagementDiagnostics;

    public event EventHandler<PolicyCopyRequest>? CopyTextRequested;
    public event EventHandler<PolicyEditorLaunchRequest>? OpenPolicyEditorRequested;

    public AgentPolicyInspectorViewModel()
        : this(
            new BrokerPolicyManagementService(),
            new PackagedPolicyWriteElevationEligibility(),
            AccessibilityAnnouncementService.Announce)
    {
    }

    public AgentPolicyInspectorViewModel(
        IBrokerPolicyManagementService managementService)
        : this(
            managementService,
            new PackagedPolicyWriteElevationEligibility(),
            AccessibilityAnnouncementService.Announce)
    {
    }

    internal AgentPolicyInspectorViewModel(
        IBrokerPolicyManagementService managementService,
        Action<string?, AutomationLiveSetting> announce)
        : this(
            managementService,
            new PackagedPolicyWriteElevationEligibility(),
            announce)
    {
    }

    internal AgentPolicyInspectorViewModel(
        IBrokerPolicyManagementService managementService,
        IPolicyWriteElevationEligibility writeElevationEligibility,
        Action<string?, AutomationLiveSetting> announce)
    {
        _announce = announce;
        _managementService = managementService;
        _writeElevationEligibility = writeElevationEligibility;
        SetManagementStatus(
            CoreTools.Translate("Loading package broker policy"),
            CoreTools.Translate("Contacting the Devolutions Agent service."),
            InfoBarSeverity.Informational);
    }

    /// <summary>Loads the authoritative management state without consulting any other endpoint.</summary>
    public Task LoadManagementAsync() => LoadPageAsync();

    [RelayCommand(CanExecute = nameof(CanRefreshPage))]
    private Task RefreshPageAsync() =>
        CanRefreshPage() ? RefreshPageCoreAsync() : Task.CompletedTask;

    internal Task LoadPageAsync() => RefreshPageCoreAsync();

    private async Task RefreshPageCoreAsync()
    {
        if (Volatile.Read(ref _isDisposed) != 0) return;

        long generation = Interlocked.Increment(ref _pageRefreshGeneration);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        CancellationTokenSource? previous =
            Interlocked.Exchange(ref _pageRefreshCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();

        IsPageRefreshActive = true;
        RefreshPageCommand.NotifyCanExecuteChanged();
        ClearManagement();
        ClearPolicy();
        HasActivePolicyDetails = false;
        SetManagementStatus(
            CoreTools.Translate("Loading package broker policy"),
            CoreTools.Translate("Contacting the Devolutions Agent service."),
            InfoBarSeverity.Informational);
        try
        {
            BrokerPolicyManagementResult? management =
                await RefreshManagementCoreAsync(
                    announce: false,
                    cancellation.Token);
            if (!CanApplyPage(generation, cancellation) || management is null) return;

            AnnounceManagementStatus();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (CanApplyPage(generation, cancellation))
            {
                IsPageRefreshActive = false;
                RefreshPageCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private bool CanRefreshPage() =>
        Volatile.Read(ref _isDisposed) == 0
        && !IsPageRefreshActive
        && !IsManagementLoading;

    partial void OnIsManagementLoadingChanged(bool value) =>
        RefreshPageCommand.NotifyCanExecuteChanged();

    private bool CanApplyPage(long generation, CancellationTokenSource cancellation) =>
        Volatile.Read(ref _isDisposed) == 0
        && !cancellation.IsCancellationRequested
        && generation == Volatile.Read(ref _pageRefreshGeneration);

    [RelayCommand]
    private void CopyRawJson()
    {
        if (!string.IsNullOrEmpty(RawJson))
        {
            CopyTextRequested?.Invoke(
                this,
                new PolicyCopyRequest(RawJson, Volatile.Read(ref _pageRefreshGeneration)));
        }
    }

    internal void ReportCopyFailure(long pageGeneration)
    {
        if (Volatile.Read(ref _isDisposed) != 0
            || pageGeneration != Volatile.Read(ref _pageRefreshGeneration))
        {
            return;
        }

        SetManagementStatus(
            CoreTools.Translate("Could not copy policy JSON"),
            CoreTools.Translate("The canonical policy JSON could not be copied to the clipboard. Try again."),
            InfoBarSeverity.Error);
        AnnounceManagementStatus();
    }

    private async Task<BrokerPolicyManagementResult?> RefreshManagementCoreAsync(
        bool announce = true,
        CancellationToken externalCancellation = default)
    {
        if (Volatile.Read(ref _isDisposed) != 0) return null;

        long generation = Interlocked.Increment(ref _managementRefreshGeneration);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token,
            externalCancellation);
        CancellationTokenSource? previous = Interlocked.Exchange(ref _managementRefreshCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();

        IsManagementLoading = true;
        SetManagementStatus(
            CoreTools.Translate("Loading policy management state"),
            CoreTools.Translate("Contacting the Devolutions Agent service."),
            InfoBarSeverity.Informational);
        ClearPolicy();
        HasActivePolicyDetails = false;

        try
        {
            BrokerPolicyManagementResult result =
                await _managementService.GetManagementAsync(cancellation.Token);
            if (!CanApplyManagement(generation, cancellation)) return null;

            PolicyWriteElevationEligibility writeEligibility =
                PolicyWriteElevationEligibility.Eligible;
            if (result is
                {
                    Status: BrokerPolicyManagementStatus.Retrieved,
                    Snapshot.WriteCapability: PolicyWriteCapability.Writable,
                })
            {
                writeEligibility = await _writeElevationEligibility
                    .EvaluateAsync(cancellation.Token);
                if (!CanApplyManagement(generation, cancellation)) return null;
            }

            _appliedManagementGeneration = generation;
            ApplyManagementResult(result, writeEligibility);
            ApplyUnifiedPagePresentation(result);
            if (announce)
            {
                AnnounceManagementStatus();
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (CanApplyManagement(generation, cancellation))
            {
                IsManagementLoading = false;
            }
        }
    }

    [RelayCommand]
    private void EditPolicy()
    {
        if (!CanEdit || _managementSnapshot is not { State: PolicyManagementState.Active } snapshot) return;
        OpenPolicyEditorRequested?.Invoke(
            this,
            new PolicyEditorLaunchRequest(PolicyEditorOperationKind.Update, snapshot));
    }

    [RelayCommand]
    private void ReplaceIdentity()
    {
        if (!CanReplaceIdentity
            || _managementSnapshot is not { State: PolicyManagementState.Active, Policy: not null } snapshot)
        {
            return;
        }

        PolicyEditorDraftDocument seed = PolicyEditorTemplates.CreateNew(
            PolicyEditorTemplates.CreateReplacementId(snapshot.Policy.Metadata.Id),
            snapshot.Policy.Metadata.Publisher);
        OpenPolicyEditorRequested?.Invoke(
            this,
            new PolicyEditorLaunchRequest(PolicyEditorOperationKind.ReplaceIdentity, snapshot, seed));
    }

    [RelayCommand]
    private void CreatePolicy()
    {
        if (!CanCreate || _managementSnapshot is not { State: PolicyManagementState.Missing } snapshot) return;

        PolicyEditorDraftDocument seed = PolicyEditorTemplates.CreateNew(
            "new-policy",
            CoreTools.Translate("Your organization"));
        OpenPolicyEditorRequested?.Invoke(
            this,
            new PolicyEditorLaunchRequest(PolicyEditorOperationKind.Create, snapshot, seed));
    }

    [RelayCommand]
    private void RepairPolicy()
    {
        if (!CanRepair || _managementSnapshot is not { State: PolicyManagementState.Invalid } snapshot) return;

        PolicyEditorDraftDocument seed = PolicyEditorTemplates.CreateNew(
            "repaired-policy",
            CoreTools.Translate("Your organization"));
        OpenPolicyEditorRequested?.Invoke(
            this,
            new PolicyEditorLaunchRequest(PolicyEditorOperationKind.Repair, snapshot, seed));
    }

    private bool CanApplyManagement(long generation, CancellationTokenSource cancellation)
    {
        return Volatile.Read(ref _isDisposed) == 0
            && !cancellation.IsCancellationRequested
            && generation == Volatile.Read(ref _managementRefreshGeneration);
    }

    private void ApplyUnifiedPagePresentation(BrokerPolicyManagementResult result)
    {
        ClearPolicy();

        switch (result)
        {
            case
            {
                Status: BrokerPolicyManagementStatus.Retrieved,
                Snapshot.State: PolicyManagementState.Active,
                Snapshot.Policy: not null,
            }:
                HasActivePolicyDetails = true;
                ApplyPolicy(
                    result.Snapshot.Policy,
                    PolicySerializer.Serialize(result.Snapshot.Policy),
                    result.Server?.ServerVersion);
                break;
            case
            {
                Status: BrokerPolicyManagementStatus.Retrieved,
                Snapshot.State: PolicyManagementState.Missing or PolicyManagementState.Invalid,
            }:
                HasActivePolicyDetails = false;
                break;
            default:
                HasActivePolicyDetails = false;
                break;
        }
    }

    private void ApplyPolicy(
        PolicyDocument policy,
        string canonicalJson,
        string? serverVersion)
    {
        PolicyMetadata metadata = policy.Metadata;

        if (serverVersion is not null)
        {
            MetadataRows.Add(Row("Server version", Value(serverVersion)));
        }
        MetadataRows.Add(Row("Policy ID", Value(metadata.Id)));
        MetadataRows.Add(Row("Publisher", Value(metadata.Publisher)));
        MetadataRows.Add(Row("Revision", metadata.Revision.ToString(CultureInfo.CurrentCulture)));
        MetadataRows.Add(Row("Policy format version", policy.PolicyFormatVersion.Value));
        MetadataRows.Add(Row("Published", FormatDate(metadata.PublishedAt)));
        MetadataRows.Add(Row("Valid from", FormatDate(metadata.ValidFrom)));
        MetadataRows.Add(Row("Valid until", FormatDate(metadata.ValidUntil)));
        MetadataRows.Add(Row("Description", Value(metadata.Description)));
        MetadataRows.Add(Row("Support URL", Value(metadata.SupportUrl)));

        EnforcementRows.Add(Row("Default decision", TranslateEnum(policy.Enforcement.DefaultDecision)));
        EnforcementRows.Add(Row("Audit mode", FormatNullableBoolean(policy.Enforcement.AuditMode)));

        for (int index = 0; index < policy.Rules.Count; index++)
        {
            Rules.Add(BuildRule(policy.Rules[index], index));
        }

        RawJson = canonicalJson;
        HasNoRules = Rules.Count == 0;
        HasPolicy = true;
    }

    private static PolicyRuleViewModel BuildRule(PolicyRule rule, int index)
    {
        PolicyMatch match = rule.Match;
        bool hasConstraints = rule.Decision == PolicyDecision.Allow;
        PolicyConstraints? constraints = rule.Constraints;

        return new PolicyRuleViewModel
        {
            AutomationName = CoreTools.Translate("Rule {0}: {1}", index + 1, Value(rule.Id)),
            Id = Value(rule.Id),
            Enabled = FormatBoolean(rule.Enabled),
            Priority = rule.Priority.ToString(CultureInfo.CurrentCulture),
            Decision = TranslateEnum(rule.Decision),
            Reason = Value(rule.Reason),
            HasConstraints = hasConstraints,
            MatchRows =
            [
                Row("Operations", FormatEnumList<PolicyOperation>(match.Operations)),
                Row("Package managers", FormatEnumList<PolicyManagerName>(match.Managers)),
                Row("Source names", FormatList(match.SourceNames, anyWhenEmpty: true)),
                Row(
                    "Exact package identifiers",
                    FormatList(match.PackageIdentifiers?.Exact ?? [], anyWhenEmpty: true)),
                Row(
                    "Package identifier patterns",
                    FormatList(match.PackageIdentifiers?.Patterns ?? [], anyWhenEmpty: true)),
                Row(
                    "Exact versions",
                    FormatList(match.Version?.Exact ?? [], anyWhenEmpty: true)),
                Row("Version range", FormatVersionRange(match.Version?.Range)),
                Row("Scopes", FormatEnumList<PolicyScope>(match.Scopes)),
                Row("Architectures", FormatEnumList<PolicyArchitecture>(match.Architectures)),
                Row("Execution elevation", FormatEnumList<PolicyElevation>(match.ExecutionElevation)),
                Row("Interactive", FormatMatchBoolean(match.Interactive)),
                Row("Skip hash check", FormatMatchBoolean(match.SkipHashCheck)),
                Row("Prerelease", FormatMatchBoolean(match.PreRelease)),
                Row("Has custom parameters", FormatMatchBoolean(match.HasCustomParameters)),
                Row("Has custom install location", FormatMatchBoolean(match.HasCustomInstallLocation)),
                Row("Has pre/post commands", FormatMatchBoolean(match.HasPrePostCommands)),
                Row("Has kill-before-operation", FormatMatchBoolean(match.HasKillBeforeOperation)),
                Row("Has uninstall previous", FormatMatchBoolean(match.HasUninstallPrevious)),
            ],
            ConstraintRows = !hasConstraints
                ? []
                : constraints is null
                ? [Row("Constraints", CoreTools.Translate("Not set"))]
                :
                [
                    Row("Allow interactive", FormatBoolean(constraints.AllowInteractive)),
                    Row("Allow skip hash check", FormatBoolean(constraints.AllowSkipHashCheck)),
                    Row("Allow prerelease", FormatBoolean(constraints.AllowPreRelease)),
                    Row("Allow custom install location", FormatBoolean(constraints.AllowCustomInstallLocation)),
                    Row("Allowed install location patterns", FormatList(constraints.AllowedInstallLocationPatterns)),
                    Row("Allow custom parameters", FormatBoolean(constraints.AllowCustomParameters)),
                    Row("Allowed custom parameters", FormatList(constraints.AllowedCustomParameters)),
                    Row("Allowed custom parameter patterns", FormatList(constraints.AllowedCustomParameterPatterns)),
                    Row("Denied custom parameters", FormatList(constraints.DeniedCustomParameters)),
                    Row("Allow pre/post commands", FormatBoolean(constraints.AllowPrePostCommands)),
                    Row("Allow kill-before-operation", FormatBoolean(constraints.AllowKillBeforeOperation)),
                    Row("Allow uninstall previous", FormatBoolean(constraints.AllowUninstallPrevious)),
                    Row("Allow upgrade", FormatBoolean(constraints.AllowUpgrade)),
                ],
        };
    }

    private static PolicyDetailRow Row(string label, string value) =>
        new(CoreTools.Translate(label), value);

    private static string FormatDate(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        ?? CoreTools.Translate("Not set");

    private static string FormatBoolean(bool value) =>
        CoreTools.Translate(value ? "Yes" : "No");

    private static string FormatNullableBoolean(bool? value) =>
        value.HasValue ? FormatBoolean(value.Value) : CoreTools.Translate("Not set");

    private static string FormatMatchBoolean(bool? value) =>
        value.HasValue ? FormatBoolean(value.Value) : CoreTools.Translate("Any");

    private static string FormatEnumList<T>(IEnumerable<T> values) where T : struct, Enum =>
        FormatList(values.Select(TranslateEnum), anyWhenEmpty: true);

    private static string FormatList(IEnumerable<string> values, bool anyWhenEmpty = false)
    {
        string[] items = values.Where(value => !string.IsNullOrEmpty(value)).ToArray();
        return items.Length == 0
            ? CoreTools.Translate(anyWhenEmpty ? "Any" : "None")
            : string.Join(", ", items);
    }

    private static string FormatVersionRange(VersionRange? range)
    {
        if (range is null) return CoreTools.Translate("Any");

        return CoreTools.Translate(
            "{0} to {1}; include prerelease: {2}",
            Value(range.MinVersion, "Any"),
            Value(range.MaxVersion, "Any"),
            FormatBoolean(range.IncludePrerelease));
    }

    private static string TranslateEnum<T>(T value) where T : struct, Enum =>
        CoreTools.Translate(value.ToString());

    private static string Value(string? value, string fallback = "Not set") =>
        string.IsNullOrEmpty(value) ? CoreTools.Translate(fallback) : value;

    private void ClearPolicy()
    {
        MetadataRows.Clear();
        EnforcementRows.Clear();
        Rules.Clear();
        RawJson = "";
        HasPolicy = false;
        HasNoRules = false;
    }

    private void ApplyManagementResult(
        BrokerPolicyManagementResult result,
        PolicyWriteElevationEligibility writeEligibility)
    {
        ClearManagement();

        switch (result.Status)
        {
            case BrokerPolicyManagementStatus.Retrieved when result.Snapshot is not null:
                ApplyManagementSnapshot(
                    result.Snapshot,
                    result.Diagnostics,
                    writeEligibility);
                break;
            case BrokerPolicyManagementStatus.AgentUnavailable:
                SetManagementStatus(
                    CoreTools.Translate("Devolutions Agent is unavailable"),
                    CoreTools.Translate("Communication with the package broker could not be completed. Verify that Devolutions Agent is installed and running. If the problem persists, check the Agent logs, then refresh."),
                    InfoBarSeverity.Error);
                break;
            case BrokerPolicyManagementStatus.Unsupported:
                SetManagementStatus(
                    CoreTools.Translate("Policy management is unsupported"),
                    CoreTools.Translate("The installed Devolutions Agent is reachable but does not support policy management. Update the Agent and try again."),
                    InfoBarSeverity.Warning);
                break;
            case BrokerPolicyManagementStatus.AccessDenied:
                SetManagementStatus(
                    CoreTools.Translate("Access to policy management was denied"),
                    CoreTools.Translate("Devolutions Agent did not authorize UniGetUI to manage the package policy."),
                    InfoBarSeverity.Error);
                break;
            case BrokerPolicyManagementStatus.InvalidResponse:
                SetManagementStatus(
                    CoreTools.Translate("The policy management response is invalid"),
                    CoreTools.Translate("Devolutions Agent returned a malformed or incompatible policy management response."),
                    InfoBarSeverity.Error);
                break;
            case BrokerPolicyManagementStatus.UnsupportedPlatform:
                SetManagementStatus(
                    CoreTools.Translate("Policy management is available on Windows only"),
                    CoreTools.Translate("This page cannot manage the policy file through the Windows Devolutions Agent service on the current platform."),
                    InfoBarSeverity.Warning);
                break;
            case BrokerPolicyManagementStatus.UnsafePolicyPath:
                SetManagementStatus(
                    CoreTools.Translate("The configured policy path is unsafe"),
                    CoreTools.Translate("Devolutions Agent refused to manage the configured policy path because it is considered unsafe (for example, a path traversal or reparse point)."),
                    InfoBarSeverity.Error);
                break;
            case BrokerPolicyManagementStatus.UnsupportedPolicyFormat:
                SetManagementStatus(
                    CoreTools.Translate("The policy file format is unsupported"),
                    CoreTools.Translate("Devolutions Agent reported that the configured policy file format is not supported for management."),
                    InfoBarSeverity.Error);
                break;
            case BrokerPolicyManagementStatus.UnsupportedPolicyFilesystem:
                SetManagementStatus(
                    CoreTools.Translate("The policy file system is unsupported"),
                    CoreTools.Translate("Devolutions Agent reported that the file system hosting the configured policy path is not supported for management."),
                    InfoBarSeverity.Error);
                break;
            case BrokerPolicyManagementStatus.PolicyUnavailable:
                SetManagementStatus(
                    CoreTools.Translate("The policy management state is unavailable"),
                    CoreTools.Translate("Devolutions Agent supports policy management but could not provide the current state. Review the Agent configuration and try again."),
                    InfoBarSeverity.Error);
                break;
            default:
                SetManagementStatus(
                    CoreTools.Translate("The policy management response is invalid"),
                    CoreTools.Translate("Devolutions Agent returned a malformed or incompatible policy management response."),
                    InfoBarSeverity.Error);
                break;
        }
    }

    private void ApplyManagementSnapshot(
        PolicyManagementSnapshot snapshot,
        BrokerPolicyDiagnosticsView? diagnostics,
        PolicyWriteElevationEligibility writeEligibility)
    {
        _managementSnapshot = snapshot;
        HasManagementSnapshot = true;

        ManagementStateText = TranslateEnum(snapshot.State);
        ManagementConfiguredPath = Value(PolicyFindingPresentation.SanitizeAgentText(
            snapshot.ConfiguredPath,
            BrokerPolicyManagementLimits.MaxSanitizedPathLength));
        ManagementSourceText = TranslateEnum(snapshot.Source);
        AgentWriteCapabilityText = snapshot.WriteCapability switch
        {
            PolicyWriteCapability.Writable => CoreTools.Translate("Writable"),
            PolicyWriteCapability.ReadOnly => CoreTools.Translate("Read-only"),
            PolicyWriteCapability.Unsupported => CoreTools.Translate("Unsupported"),
            _ => CoreTools.Translate("Unknown"),
        };
        ManagementElevationRequired = snapshot.ElevationRequired;
        ManagementElevationRequiredText = FormatBoolean(snapshot.ElevationRequired);

        bool agentWritable = snapshot.WriteCapability == PolicyWriteCapability.Writable;
        bool writable = agentWritable && writeEligibility.IsEligible;
        PolicyChangesFromThisAppText = writable
            ? CoreTools.Translate("Available")
            : CoreTools.Translate("Unavailable");
        PolicyChangesReasonText = writable
            ? CoreTools.Translate("Not applicable")
            : agentWritable
                ? GetElevationEligibilityReason(writeEligibility.Status)
                : snapshot.ReadOnlyReason.HasValue
                    ? GetAgentReadOnlyReason(snapshot.ReadOnlyReason.Value)
                    : CoreTools.Translate("Devolutions Agent does not allow policy changes.");

        CanEdit = writable && snapshot.State == PolicyManagementState.Active;
        CanCreate = writable && snapshot.State == PolicyManagementState.Missing;
        CanRepair = writable && snapshot.State == PolicyManagementState.Invalid;
        CanReplaceIdentity = writable
            && snapshot.State == PolicyManagementState.Active
            && PolicyEditorTemplates.IsValidResourceId(snapshot.Policy?.Metadata.Id);

        if (diagnostics is not null)
        {
            foreach (BrokerPolicySanitizedFinding finding in diagnostics.Findings)
            {
                ManagementDiagnosticsRows.Add(BuildDiagnosticRow(finding));
            }

            if (diagnostics.FindingsTruncated)
            {
                ManagementDiagnosticsRows.Add(new PolicyDetailRow(
                    CoreTools.Translate("Note"),
                    CoreTools.Translate("Additional findings were omitted.")));
            }
        }

        HasManagementDiagnostics = ManagementDiagnosticsRows.Count > 0;

        switch (snapshot.State)
        {
            case PolicyManagementState.Active:
                SetManagementStatus(
                    CoreTools.Translate("Policy management is active"),
                    CoreTools.Translate("A valid policy file is configured and in effect."),
                    InfoBarSeverity.Success);
                break;
            case PolicyManagementState.Missing:
                SetManagementStatus(
                    CoreTools.Translate("No policy file exists"),
                    CoreTools.Translate("Create a new policy file to start enforcing package broker rules."),
                    InfoBarSeverity.Informational);
                break;
            case PolicyManagementState.Invalid:
                SetManagementStatus(
                    CoreTools.Translate("The configured policy file is invalid"),
                    CoreTools.Translate("Review the diagnostics below and repair the policy file."),
                    InfoBarSeverity.Warning);
                break;
            default:
                SetManagementStatus(
                    CoreTools.Translate("The policy management state is invalid"),
                    CoreTools.Translate("Devolutions Agent returned an unrecognized policy management state."),
                    InfoBarSeverity.Error);
                break;
        }
    }

    private static string GetElevationEligibilityReason(
        PolicyWriteElevationEligibilityStatus status) =>
        status switch
        {
            PolicyWriteElevationEligibilityStatus.HelperMissing => CoreTools.Translate(
                "The signed policy write helper is missing. Reinstall UniGetUI for all users in an administrator-protected location to enable policy changes."),
            PolicyWriteElevationEligibilityStatus.ProtectedInstallRequired => CoreTools.Translate(
                "Policy changes are disabled because this UniGetUI installation is not administrator-protected. Reinstall UniGetUI for all users in an administrator-protected location to enable them."),
            _ => CoreTools.Translate(
                "This UniGetUI installation cannot securely launch the policy write helper. Reinstall UniGetUI for all users in an administrator-protected location to enable policy changes."),
        };

    private static string GetAgentReadOnlyReason(PolicyReadOnlyReason reason) =>
        reason switch
        {
            PolicyReadOnlyReason.ManagementDisabled =>
                CoreTools.Translate("Policy management is disabled in Devolutions Agent."),
            PolicyReadOnlyReason.PathNotConfigured =>
                CoreTools.Translate("No policy path is configured in Devolutions Agent."),
            PolicyReadOnlyReason.UnsupportedFormat =>
                CoreTools.Translate("Devolutions Agent does not support the configured policy format."),
            PolicyReadOnlyReason.UnsafePath =>
                CoreTools.Translate("Devolutions Agent considers the configured policy path unsafe."),
            PolicyReadOnlyReason.InsufficientPermissions =>
                CoreTools.Translate("Devolutions Agent does not have permission to change the policy file."),
            PolicyReadOnlyReason.UnsupportedFileSystem =>
                CoreTools.Translate("Devolutions Agent does not support the policy file system."),
            _ => CoreTools.Translate("Devolutions Agent does not allow policy changes."),
        };

    private static PolicyDetailRow BuildDiagnosticRow(BrokerPolicySanitizedFinding finding)
    {
        string label = CoreTools.Translate("{0} ({1})", TranslateEnum(finding.Severity), TranslateEnum(finding.Code));
        string location = finding.Path is { Length: > 0 } path
            ? (finding.RuleId is { Length: > 0 } ruleId ? $"{path} \u00b7 {ruleId}" : path)
            : finding.RuleId is { Length: > 0 } ruleIdOnly ? ruleIdOnly : "";
        string message = PolicyFindingPresentation.Describe(
            finding.Code,
            finding.Arguments,
            finding.Message);
        string value = string.IsNullOrEmpty(location) ? message : $"{location}: {message}";
        return new PolicyDetailRow(label, value);
    }

    private void AnnounceManagementStatus()
    {
        string message = string.IsNullOrEmpty(ManagementStatus.Message)
            ? ManagementStatus.Title
            : $"{ManagementStatus.Title}. {ManagementStatus.Message}";
        _announce(
            message,
            ManagementStatus.Severity == InfoBarSeverity.Error
                ? AutomationLiveSetting.Assertive
                : AutomationLiveSetting.Polite);
    }

    private void SetManagementStatus(string title, string message, InfoBarSeverity severity)
    {
        ManagementStatus.Title = title;
        ManagementStatus.Message = message;
        ManagementStatus.Severity = severity;
        ManagementStatus.IsOpen = true;
    }

    private void ClearManagement()
    {
        ManagementDiagnosticsRows.Clear();
        _managementSnapshot = null;
        HasManagementSnapshot = false;
        ManagementStateText = "";
        ManagementConfiguredPath = "";
        ManagementSourceText = "";
        AgentWriteCapabilityText = "";
        PolicyChangesFromThisAppText = "";
        PolicyChangesReasonText = "";
        ManagementElevationRequired = false;
        ManagementElevationRequiredText = "";
        HasManagementDiagnostics = false;
        CanEdit = false;
        CanCreate = false;
        CanRepair = false;
        CanReplaceIdentity = false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;

        _lifetimeCancellation.Cancel();
        Interlocked.Exchange(ref _managementRefreshCancellation, null)?.Cancel();
        Interlocked.Exchange(ref _pageRefreshCancellation, null)?.Cancel();
    }
}
