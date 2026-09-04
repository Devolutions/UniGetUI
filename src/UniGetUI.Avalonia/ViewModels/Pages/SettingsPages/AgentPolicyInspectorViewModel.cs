using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Model;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.AgentBroker;
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

public sealed class PolicyRuleViewModel
{
    public required string AutomationName { get; init; }
    public required string Id { get; init; }
    public required string Enabled { get; init; }
    public required string Priority { get; init; }
    public required string Decision { get; init; }
    public required string Reason { get; init; }
    public required IReadOnlyList<PolicyDetailRow> MatchRows { get; init; }
    public required IReadOnlyList<PolicyDetailRow> ConstraintRows { get; init; }
}

public partial class AgentPolicyInspectorViewModel : ViewModelBase, IDisposable
{
    private readonly IBrokerPolicyInspector _inspector;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _refreshCancellation;
    private long _refreshGeneration;
    private int _isDisposed;

    public InfoBarViewModel Status { get; } = new()
    {
        IsClosable = false,
        IsOpen = true,
    };

    public ObservableCollection<PolicyDetailRow> MetadataRows { get; } = [];
    public ObservableCollection<PolicyDetailRow> EnforcementRows { get; } = [];
    public ObservableCollection<PolicyRuleViewModel> Rules { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasPolicy;
    [ObservableProperty] private bool _hasNoRules;
    [ObservableProperty] private string _rawJson = "";

    public event EventHandler<string>? CopyTextRequested;

    public AgentPolicyInspectorViewModel()
        : this(new BrokerPolicyInspector())
    {
    }

    public AgentPolicyInspectorViewModel(IBrokerPolicyInspector inspector)
    {
        _inspector = inspector;
        SetStatus(
            CoreTools.Translate("Loading active package broker policy"),
            CoreTools.Translate("Contacting the Devolutions Agent service."),
            InfoBarSeverity.Informational);
    }

    public Task LoadAsync() => RefreshAsync();

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RefreshAsync()
    {
        if (Volatile.Read(ref _isDisposed) != 0) return;

        long generation = Interlocked.Increment(ref _refreshGeneration);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        CancellationTokenSource? previous = Interlocked.Exchange(ref _refreshCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();

        IsLoading = true;
        HasPolicy = false;
        SetStatus(
            CoreTools.Translate("Loading active package broker policy"),
            CoreTools.Translate("Contacting the Devolutions Agent service."),
            InfoBarSeverity.Informational);

        try
        {
            BrokerPolicyInspectionResult result =
                await _inspector.InspectAsync(cancellation.Token);
            if (!CanApply(generation, cancellation)) return;

            ApplyResult(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (CanApply(generation, cancellation))
            {
                IsLoading = false;
            }
        }
    }

    [RelayCommand]
    private void CopyRawJson()
    {
        if (!string.IsNullOrEmpty(RawJson))
        {
            CopyTextRequested?.Invoke(this, RawJson);
        }
    }

    private bool CanApply(long generation, CancellationTokenSource cancellation)
    {
        return Volatile.Read(ref _isDisposed) == 0
            && !cancellation.IsCancellationRequested
            && generation == Volatile.Read(ref _refreshGeneration);
    }

    private void ApplyResult(BrokerPolicyInspectionResult result)
    {
        ClearPolicy();

        switch (result.Status)
        {
            case BrokerPolicyInspectionStatus.Connected when result.Response is not null:
                ApplyPolicy(result.Response, result.CanonicalJson ?? "");
                break;
            case BrokerPolicyInspectionStatus.AgentUnavailable:
                SetStatus(
                    CoreTools.Translate("Devolutions Agent is unavailable"),
                    CoreTools.Translate("The package broker could not be reached. Verify that Devolutions Agent is installed and running, then refresh."),
                    InfoBarSeverity.Error);
                break;
            case BrokerPolicyInspectionStatus.Unsupported:
                SetStatus(
                    CoreTools.Translate("Policy inspection is unsupported"),
                    CoreTools.Translate("The installed Devolutions Agent is reachable but does not support active policy inspection. Update the Agent and try again."),
                    InfoBarSeverity.Warning);
                break;
            case BrokerPolicyInspectionStatus.AccessDenied:
                SetStatus(
                    CoreTools.Translate("Access to the active policy was denied"),
                    CoreTools.Translate("Devolutions Agent did not authorize UniGetUI to inspect the active package policy."),
                    InfoBarSeverity.Error);
                break;
            case BrokerPolicyInspectionStatus.PolicyUnavailable:
                SetStatus(
                    CoreTools.Translate("The active policy is unavailable"),
                    CoreTools.Translate("Devolutions Agent supports policy inspection but could not provide the active policy. Review the Agent configuration and try again."),
                    InfoBarSeverity.Error);
                break;
            case BrokerPolicyInspectionStatus.InvalidResponse:
                SetStatus(
                    CoreTools.Translate("The policy response is invalid"),
                    CoreTools.Translate("Devolutions Agent returned a malformed or incompatible policy response."),
                    InfoBarSeverity.Error);
                break;
            case BrokerPolicyInspectionStatus.UnsupportedPlatform:
                SetStatus(
                    CoreTools.Translate("Policy inspection is available on Windows only"),
                    CoreTools.Translate("This page cannot contact the Windows Devolutions Agent service on the current platform."),
                    InfoBarSeverity.Warning);
                break;
            default:
                SetStatus(
                    CoreTools.Translate("The policy response is invalid"),
                    CoreTools.Translate("Devolutions Agent returned a malformed or incompatible policy response."),
                    InfoBarSeverity.Error);
                break;
        }
    }

    private void ApplyPolicy(PolicyResponse response, string canonicalJson)
    {
        PolicyDocument policy = response.Policy;
        PolicyMetadata metadata = policy.Metadata;

        MetadataRows.Add(Row("Server version", Value(response.Server.ServerVersion)));
        MetadataRows.Add(Row("Policy ID", Value(metadata.Id)));
        MetadataRows.Add(Row("Publisher", Value(metadata.Publisher)));
        MetadataRows.Add(Row("Revision", metadata.Revision.ToString(CultureInfo.CurrentCulture)));
        MetadataRows.Add(Row("Policy version", Value(policy.PolicyVersion)));
        MetadataRows.Add(Row("Published", FormatDate(metadata.PublishedAt)));
        MetadataRows.Add(Row("Valid from", FormatDate(metadata.ValidFrom)));
        MetadataRows.Add(Row("Valid until", FormatDate(metadata.ValidUntil)));
        MetadataRows.Add(Row("Description", Value(metadata.Description)));
        MetadataRows.Add(Row("Support URL", Value(metadata.SupportUrl)));

        EnforcementRows.Add(Row("Default decision", TranslateEnum(policy.Enforcement.DefaultDecision)));
        EnforcementRows.Add(Row("Rule precedence", TranslateEnum(policy.Enforcement.RulePrecedence)));
        EnforcementRows.Add(Row("Audit mode", FormatNullableBoolean(policy.Enforcement.AuditMode)));

        for (int index = 0; index < policy.Rules.Count; index++)
        {
            Rules.Add(BuildRule(policy.Rules[index], index));
        }

        RawJson = canonicalJson;
        HasNoRules = Rules.Count == 0;
        HasPolicy = true;
        SetStatus(
            CoreTools.Translate("Connected to Devolutions Agent"),
            CoreTools.Translate("The active package broker policy was loaded successfully."),
            InfoBarSeverity.Success);
    }

    private static PolicyRuleViewModel BuildRule(PolicyRule rule, int index)
    {
        PolicyMatch match = rule.Match;
        PolicyConstraints? constraints = rule.Constraints;

        return new PolicyRuleViewModel
        {
            AutomationName = CoreTools.Translate("Rule {0}: {1}", index + 1, Value(rule.Id)),
            Id = Value(rule.Id),
            Enabled = FormatBoolean(rule.Enabled),
            Priority = rule.Priority.ToString(CultureInfo.CurrentCulture),
            Decision = TranslateEnum(rule.Decision),
            Reason = Value(rule.Reason),
            MatchRows =
            [
                Row("Operations", FormatEnumList<PolicyOperation>(match.Operations)),
                Row("Package managers", FormatEnumList<PolicyManagerName>(match.Managers)),
                Row("Sources", FormatList(match.Sources, anyWhenEmpty: true)),
                Row("Package identifiers", FormatList(match.PackageIdentifiers, anyWhenEmpty: true)),
                Row("Package names", FormatList(match.PackageNames, anyWhenEmpty: true)),
                Row("Versions", FormatList(match.Versions, anyWhenEmpty: true)),
                Row("Version range", FormatVersionRange(match.VersionRange)),
                Row("Scopes", FormatEnumList<PolicyScope>(match.Scopes)),
                Row("Architectures", FormatEnumList<PolicyArchitecture>(match.Architectures)),
                Row("Elevation", FormatEnumList<PolicyElevation>(match.Elevation)),
                Row("Interactive", FormatBooleanList(match.Interactive)),
                Row("Skip hash check", FormatBooleanList(match.SkipHashCheck)),
                Row("Prerelease", FormatBooleanList(match.PreRelease)),
                Row("Has custom parameters", FormatBooleanList(match.HasCustomParameters)),
                Row("Has custom install location", FormatBooleanList(match.HasCustomInstallLocation)),
                Row("Has pre/post commands", FormatBooleanList(match.HasPrePostCommands)),
                Row("Has kill-before-operation", FormatBooleanList(match.HasKillBeforeOperation)),
                Row("Has uninstall previous", FormatBooleanList(match.HasUninstallPrevious)),
            ],
            ConstraintRows = constraints is null
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

    private static string FormatBooleanList(IEnumerable<bool> values) =>
        FormatList(values.Select(FormatBoolean), anyWhenEmpty: true);

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

    private void SetStatus(string title, string message, InfoBarSeverity severity)
    {
        Status.Title = title;
        Status.Message = message;
        Status.Severity = severity;
        Status.IsOpen = true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;

        _lifetimeCancellation.Cancel();
        Interlocked.Exchange(ref _refreshCancellation, null)?.Cancel();
    }
}
