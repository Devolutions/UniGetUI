using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Devolutions.Now.Policy.Model;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

/// <summary>
/// A single checkbox-style option for a multi-select enum match field (e.g. Operations, Managers,
/// Scopes, Architectures, Elevation). Deliberately non-generic (one concrete type serves every enum
/// list) so a single compiled AXAML <c>DataTemplate</c> can render all of them.
/// </summary>
public sealed partial class PolicyEditorEnumOption : ObservableObject
{
    private readonly Action<bool> _onToggled;

    public string Display { get; }
    public string HelpText { get; }
    public string AdvisoryText { get; }
    public bool IsAdvisoryVisible => IsSelected && !string.IsNullOrEmpty(AdvisoryText);

    [ObservableProperty]
    private bool _isSelected;

    public PolicyEditorEnumOption(
        string display,
        string helpText,
        string advisoryText,
        bool isSelected,
        Action<bool> onToggled)
    {
        Display = display;
        HelpText = helpText;
        AdvisoryText = advisoryText;
        _isSelected = isSelected;
        _onToggled = onToggled;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAdvisoryVisible));
        _onToggled(value);
    }
}

/// <summary>Builds <see cref="PolicyEditorEnumOption"/> lists for every value of a match enum.</summary>
internal static class PolicyEditorEnumOptionFactory
{
    public static List<PolicyEditorEnumOption> Build<TEnum>(List<TEnum> backing, Action markDirty)
        where TEnum : struct, Enum
    {
        return Enum.GetValues<TEnum>()
            .Select(value => new PolicyEditorEnumOption(
                CoreTools.Translate(value.ToString()),
                PolicyEditorHelp.EnumOption(value),
                "",
                backing.Contains(value),
                selected =>
                {
                    if (selected)
                    {
                        if (!backing.Contains(value)) backing.Add(value);
                    }
                    else
                    {
                        backing.Remove(value);
                    }

                    markDirty();
                }))
            .ToList();
    }
}

/// <summary>
/// Shared, index-based single-select enum lists (Decision, tri-state). Mirrors the codebase's
/// established "translated display strings + <c>SelectedIndex</c>" ComboBox pattern (see
/// <c>BaseLogPage.axaml</c>) instead of a <c>ComboBox.ItemTemplate</c>, so no compiled-binding
/// <c>x:DataType</c> is needed for a raw enum value.
/// </summary>
internal static class PolicyEditorEnumDisplay
{
    public static readonly Decision[] Decisions = [Decision.Allow, Decision.Deny];

    public static readonly IReadOnlyList<string> DecisionDisplayItems =
        Decisions.Select(value => CoreTools.Translate(value.ToString())).ToList();

    public static readonly TriState[] TriStates = [TriState.Omitted, TriState.False, TriState.True];

    public static readonly IReadOnlyList<string> TriStateDisplayItems =
    [
        CoreTools.Translate("Does not matter"),
        CoreTools.Translate("No"),
        CoreTools.Translate("Yes"),
    ];

    /// <summary>Not set / No / Yes, for the nullable-boolean audit-mode field.</summary>
    public static readonly IReadOnlyList<string> NullableBooleanDisplayItems =
    [
        CoreTools.Translate("Not set"),
        CoreTools.Translate("No"),
        CoreTools.Translate("Yes"),
    ];

    public static readonly IReadOnlyList<string> AuditModeDisplayItems =
    [
        CoreTools.Translate("No"),
        CoreTools.Translate("Yes"),
    ];

    public static int IndexOfDecision(Decision value) => Array.IndexOf(Decisions, value);

    public static int IndexOfTriState(TriState value) => Array.IndexOf(TriStates, value);

    public static int IndexOfNullableBoolean(bool? value) => value switch
    {
        null => 0,
        false => 1,
        true => 2,
    };

    public static bool? NullableBooleanFromIndex(int index) => index switch
    {
        1 => false,
        2 => true,
        _ => null,
    };
}

/// <summary>
/// UI-facing wrapper over the document-level <see cref="PolicyEditorDraftDocument.Metadata"/> and
/// <see cref="PolicyEditorDraftDocument.Enforcement"/>, exposing convenience index/text properties the
/// structured editor's AXAML can bind directly (compiled bindings require a concrete get/set surface;
/// the draft POCOs are plain mutable objects with no change notification of their own). Every setter
/// routes through <see cref="PolicyEditorSessionViewModel.NotifyDraftChangedCommand"/> so validation,
/// findings and dirty state stay in sync without rebuilding this wrapper on every keystroke.
/// </summary>
public sealed class PolicyEditorDocumentUi : ObservableObject
{
    private readonly PolicyEditorSessionViewModel _sessionViewModel;
    private readonly object _validFromErrorKey = new();
    private readonly object _validUntilErrorKey = new();
    private string _validFromText;
    private string _validUntilText;
    private string? _validFromError;
    private string? _validUntilError;

    public PolicyEditorDocumentUi(PolicyEditorSessionViewModel sessionViewModel)
    {
        _sessionViewModel = sessionViewModel;
        _validFromText = Format(Draft.Metadata.ValidFrom);
        _validUntilText = Format(Draft.Metadata.ValidUntil);
    }

    private PolicyEditorDraftDocument Draft => _sessionViewModel.Draft;

    public bool IsIdentityLocked => _sessionViewModel.IsIdentityLocked;

    public void NotifyIdentityLockChanged() =>
        OnPropertyChanged(nameof(IsIdentityLocked));

    public string PolicyFormatVersion => Draft.PolicyFormatVersion.Value;
    public IReadOnlyList<PolicyValidationFinding> PolicyFormatVersionFindings =>
        FindingsFor("/PolicyFormatVersion");
    public bool HasPolicyFormatVersionErrors => HasErrors(PolicyFormatVersionFindings);

    public string Id
    {
        get => Draft.Metadata.Id;
        set { Draft.Metadata.Id = value ?? ""; MarkDirty(); }
    }
    public IReadOnlyList<PolicyValidationFinding> IdFindings => FindingsFor("/Metadata/Id");
    public bool HasIdErrors => HasErrors(IdFindings);

    public string Publisher
    {
        get => Draft.Metadata.Publisher;
        set { Draft.Metadata.Publisher = value ?? ""; MarkDirty(); }
    }
    public IReadOnlyList<PolicyValidationFinding> PublisherFindings => FindingsFor("/Metadata/Publisher");
    public bool HasPublisherErrors => HasErrors(PublisherFindings);

    public string? Description
    {
        get => Draft.Metadata.Description;
        set
        {
            if (!HasDescription && string.IsNullOrEmpty(value)) return;
            if (string.Equals(Draft.Metadata.Description, value, StringComparison.Ordinal)) return;
            bool hadDescription = HasDescription;
            Draft.Metadata.Description = value;
            if (hadDescription != HasDescription)
                OnPropertyChanged(nameof(HasDescription));
            MarkDirty();
        }
    }
    public IReadOnlyList<PolicyValidationFinding> DescriptionFindings => FindingsFor("/Metadata/Description");
    public bool HasDescriptionErrors => HasErrors(DescriptionFindings);

    public bool HasDescription
    {
        get => Draft.Metadata.Description is not null;
        set
        {
            if (value == HasDescription) return;
            Draft.Metadata.Description = value ? "" : null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Description));
            MarkDirty();
        }
    }

    public string? SupportUrl
    {
        get => Draft.Metadata.SupportUrl;
        set
        {
            string? normalized = string.IsNullOrEmpty(value) ? null : value;
            if (string.Equals(Draft.Metadata.SupportUrl, normalized, StringComparison.Ordinal))
                return;
            Draft.Metadata.SupportUrl = normalized;
            MarkDirty();
        }
    }
    public IReadOnlyList<PolicyValidationFinding> SupportUrlFindings => FindingsFor("/Metadata/SupportUrl");
    public bool HasSupportUrlErrors => HasErrors(SupportUrlFindings);

    /// <summary>Round-trip ISO-8601 text. Invalid input is retained and blocks validation/save.</summary>
    public string ValidFromText
    {
        get => _validFromText;
        set
        {
            value ??= "";
            if (string.Equals(_validFromText, value, StringComparison.Ordinal)) return;
            _validFromText = value;
            OnPropertyChanged();
            if (TryParse(value, out DateTimeOffset? parsed))
            {
                Draft.Metadata.ValidFrom = parsed;
                SetValidFromError(null);
                MarkDirty();
            }
            else
            {
                SetValidFromError(CoreTools.Translate("Enter a valid ISO 8601 date and time."));
                _sessionViewModel.NotifyLocalInputChanged();
            }
        }
    }

    public string ValidUntilText
    {
        get => _validUntilText;
        set
        {
            value ??= "";
            if (string.Equals(_validUntilText, value, StringComparison.Ordinal)) return;
            _validUntilText = value;
            OnPropertyChanged();
            if (TryParse(value, out DateTimeOffset? parsed))
            {
                Draft.Metadata.ValidUntil = parsed;
                SetValidUntilError(null);
                MarkDirty();
            }
            else
            {
                SetValidUntilError(CoreTools.Translate("Enter a valid ISO 8601 date and time."));
                _sessionViewModel.NotifyLocalInputChanged();
            }
        }
    }

    public string? ValidFromError => _validFromError;
    public string? ValidUntilError => _validUntilError;
    public IReadOnlyList<PolicyValidationFinding> ValidFromFindings =>
        [.. FindingsFor("/Metadata/ValidFrom"), .. FindingsForExact("/Metadata")];
    public IReadOnlyList<PolicyValidationFinding> ValidUntilFindings =>
        [.. FindingsFor("/Metadata/ValidUntil"), .. FindingsForExact("/Metadata")];
    public bool HasValidFromErrors => HasErrors(ValidFromFindings);
    public bool HasValidUntilErrors => HasErrors(ValidUntilFindings);

    public int DecisionIndex
    {
        get => PolicyEditorEnumDisplay.IndexOfDecision(Draft.Enforcement.DefaultDecision);
        set
        {
            if (value >= 0 && value < PolicyEditorEnumDisplay.Decisions.Length)
            {
                Draft.Enforcement.DefaultDecision = PolicyEditorEnumDisplay.Decisions[value];
                OnPropertyChanged(nameof(IsDefaultAllow));
                MarkDirty();
            }
        }
    }
    public bool IsDefaultAllow =>
        Draft.Enforcement.DefaultDecision == Decision.Allow;
    public IReadOnlyList<PolicyValidationFinding> DefaultDecisionFindings =>
        FindingsFor("/Enforcement/DefaultDecision");
    public bool HasDefaultDecisionErrors => HasErrors(DefaultDecisionFindings);

    public string RulePrecedenceDisplay => CoreTools.Translate(Draft.Enforcement.RulePrecedence.ToString());
    public IReadOnlyList<PolicyValidationFinding> RulePrecedenceFindings =>
        FindingsFor("/Enforcement/RulePrecedence");
    public bool HasRulePrecedenceErrors => HasErrors(RulePrecedenceFindings);

    public int AuditModeIndex
    {
        get => Draft.Enforcement.AuditMode is true ? 1 : 0;
        set
        {
            if (value is 0 or 1)
            {
                Draft.Enforcement.AuditMode = value == 1;
                OnPropertyChanged(nameof(IsAuditModeEnabled));
                MarkDirty();
            }
        }
    }
    public bool IsAuditModeEnabled => Draft.Enforcement.AuditMode is true;
    public IReadOnlyList<PolicyValidationFinding> AuditModeFindings =>
        FindingsFor("/Enforcement/AuditMode");
    public bool HasAuditModeErrors => HasErrors(AuditModeFindings);

    private void MarkDirty() => _sessionViewModel.NotifyDraftChangedCommand.Execute(null);

    public void RefreshFromDraft()
    {
        _validFromText = Format(Draft.Metadata.ValidFrom);
        _validUntilText = Format(Draft.Metadata.ValidUntil);
        SetValidFromError(null);
        SetValidUntilError(null);
        OnPropertyChanged(nameof(PolicyFormatVersion));
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(Publisher));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(SupportUrl));
        OnPropertyChanged(nameof(ValidFromText));
        OnPropertyChanged(nameof(ValidUntilText));
        OnPropertyChanged(nameof(ValidFromError));
        OnPropertyChanged(nameof(ValidUntilError));
        OnPropertyChanged(nameof(DecisionIndex));
        OnPropertyChanged(nameof(IsDefaultAllow));
        OnPropertyChanged(nameof(AuditModeIndex));
        OnPropertyChanged(nameof(IsAuditModeEnabled));
        OnPropertyChanged(nameof(RulePrecedenceDisplay));
        OnPropertyChanged(nameof(IsIdentityLocked));
        RefreshFindings();
    }

    public void RefreshFindings()
    {
        foreach (string property in new[]
        {
            nameof(PolicyFormatVersionFindings), nameof(HasPolicyFormatVersionErrors),
            nameof(IdFindings), nameof(HasIdErrors),
            nameof(PublisherFindings), nameof(HasPublisherErrors),
            nameof(DescriptionFindings), nameof(HasDescriptionErrors),
            nameof(SupportUrlFindings), nameof(HasSupportUrlErrors),
            nameof(ValidFromFindings), nameof(HasValidFromErrors),
            nameof(ValidUntilFindings), nameof(HasValidUntilErrors),
            nameof(DefaultDecisionFindings), nameof(HasDefaultDecisionErrors),
            nameof(RulePrecedenceFindings), nameof(HasRulePrecedenceErrors),
            nameof(AuditModeFindings), nameof(HasAuditModeErrors),
        })
        {
            OnPropertyChanged(property);
        }
    }

    private IReadOnlyList<PolicyValidationFinding> FindingsFor(params string[] pointers) =>
        _sessionViewModel.Session.Findings.All
            .Where(finding => pointers.Any(finding.TargetsPointer))
            .ToArray();

    private IReadOnlyList<PolicyValidationFinding> FindingsForExact(string pointer) =>
        _sessionViewModel.Session.Findings.All
            .Where(finding => finding.Pointer.Equals(pointer, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static bool HasErrors(IEnumerable<PolicyValidationFinding> findings) =>
        findings.Any(finding => finding.IsError);

    private void SetValidFromError(string? error)
    {
        if (string.Equals(_validFromError, error, StringComparison.Ordinal)) return;
        _validFromError = error;
        _sessionViewModel.SetLocalInputError(_validFromErrorKey, error);
        OnPropertyChanged(nameof(ValidFromError));
    }

    private void SetValidUntilError(string? error)
    {
        if (string.Equals(_validUntilError, error, StringComparison.Ordinal)) return;
        _validUntilError = error;
        _sessionViewModel.SetLocalInputError(_validUntilErrorKey, error);
        OnPropertyChanged(nameof(ValidUntilError));
    }

    private static string Format(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture) ?? "";

    private static bool TryParse(string? text, out DateTimeOffset? parsed)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            parsed = null;
            return true;
        }

        string normalized = text.EndsWith('Z')
            ? text[..^1] + "+00:00"
            : text;
        string[] formats =
        [
            "yyyy-MM-dd'T'HH:mm:sszzz",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        ];
        if (DateTimeOffset.TryParseExact(
                normalized,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset value))
        {
            parsed = value;
            return true;
        }

        parsed = null;
        return false;
    }
}

/// <summary>
/// UI-facing wrapper over a single <see cref="PolicyEditorDraftRule"/>: every field of
/// <see cref="PolicyEditorDraftMatch"/> and <see cref="PolicyEditorDraftConstraints"/>, projected as
/// bindable properties (string-joined lists, index-based enum pickers, on-demand nullable
/// sub-object creation for <c>VersionRange</c>/<c>Constraints</c>). See <see cref="PolicyEditorDocumentUi"/>
/// for why every setter routes through <c>NotifyDraftChangedCommand</c> instead of raising its own
/// change notification.
/// </summary>
public sealed class PolicyEditorRuleUi : ObservableObject, IDisposable
{
    private readonly PolicyEditorSessionViewModel _sessionViewModel;
    private readonly int _ruleIndex;

    public PolicyEditorDraftRule Rule { get; }

    public PolicyEditorRuleUi(
        PolicyEditorDraftRule rule,
        int ruleIndex,
        PolicyEditorSessionViewModel sessionViewModel)
    {
        Rule = rule;
        _ruleIndex = ruleIndex;
        _sessionViewModel = sessionViewModel;

        OperationOptions = PolicyEditorEnumOptionFactory.Build(Rule.Match.Operations, MarkDirty);
        ManagerOptions = PolicyEditorEnumOptionFactory.Build(Rule.Match.Managers, MarkDirty);
        ScopeOptions = PolicyEditorEnumOptionFactory.Build(Rule.Match.Scopes, MarkDirty);
        ArchitectureOptions = PolicyEditorEnumOptionFactory.Build(Rule.Match.Architectures, MarkDirty);
        ElevationOptions = PolicyEditorEnumOptionFactory.Build(Rule.Match.Elevation, MarkDirty);
    }

    public PolicyEditorRuleUi(
        PolicyEditorDraftRule rule,
        PolicyEditorSessionViewModel sessionViewModel)
        : this(rule, sessionViewModel.Draft.Rules.IndexOf(rule), sessionViewModel)
    {
    }

    public string Id
    {
        get => Rule.Id;
        set
        {
            value ??= "";
            if (string.Equals(Rule.Id, value, StringComparison.Ordinal)) return;
            Rule.Id = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutomationName));
            MarkDirty();
        }
    }
    public IReadOnlyList<PolicyValidationFinding> IdFindings => FindingsFor("/Id");
    public bool HasIdErrors => HasErrors(IdFindings);

    public bool Enabled
    {
        get => Rule.Enabled;
        set { Rule.Enabled = value; MarkDirty(); }
    }

    public int EvaluationOrder => _ruleIndex + 1;
    public bool CanMoveUp => _ruleIndex > 0;
    public bool CanMoveDown => _ruleIndex < _sessionViewModel.Rules.Count - 1;

    public int DecisionIndex => PolicyEditorEnumDisplay.IndexOfDecision(Rule.Decision);
    public bool IsAllowDecision => Rule.Decision == Decision.Allow;
    public IReadOnlyList<PolicyValidationFinding> DecisionFindings => FindingsFor("/Decision");
    public bool HasDecisionErrors => HasErrors(DecisionFindings);

    public string? Reason
    {
        get => Rule.Reason;
        set
        {
            if (!HasReason && string.IsNullOrEmpty(value)) return;
            if (string.Equals(Rule.Reason, value, StringComparison.Ordinal)) return;
            bool hadReason = HasReason;
            Rule.Reason = value;
            if (hadReason != HasReason)
                OnPropertyChanged(nameof(HasReason));
            MarkDirty();
        }
    }
    public IReadOnlyList<PolicyValidationFinding> ReasonFindings => FindingsFor("/Reason");
    public bool HasReasonErrors => HasErrors(ReasonFindings);

    public bool HasReason
    {
        get => Rule.Reason is not null;
        set
        {
            if (value == HasReason) return;
            Rule.Reason = value ? "" : null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Reason));
            MarkDirty();
        }
    }

    public string AutomationName => CoreTools.Translate(
        "Rule: {0}",
        string.IsNullOrWhiteSpace(Rule.Id) ? CoreTools.Translate("(untitled)") : Rule.Id);

    public IReadOnlyList<PolicyEditorEnumOption> OperationOptions { get; }
    public IReadOnlyList<PolicyEditorEnumOption> ManagerOptions { get; }
    public IReadOnlyList<PolicyEditorEnumOption> ScopeOptions { get; }
    public IReadOnlyList<PolicyEditorEnumOption> ArchitectureOptions { get; }
    public IReadOnlyList<PolicyEditorEnumOption> ElevationOptions { get; }

    public string Sources
    {
        get => Join(Rule.Match.Sources);
        set => SetListField(Rule.Match.Sources, value);
    }

    public string PackageIdentifiers
    {
        get => Join(Rule.Match.PackageIdentifiers);
        set => SetListField(Rule.Match.PackageIdentifiers, value);
    }

    public string PackageNames
    {
        get => Join(Rule.Match.PackageNames);
        set => SetListField(Rule.Match.PackageNames, value);
    }
    public IReadOnlyList<PolicyValidationFinding> PackageNamesFindings =>
        FindingsFor("/Match/PackageNames");
    public bool HasPackageNamesErrors => HasErrors(PackageNamesFindings);

    public string Versions
    {
        get => Join(Rule.Match.Versions);
        set => SetListField(Rule.Match.Versions, value);
    }
    public IReadOnlyList<PolicyValidationFinding> VersionsFindings => FindingsFor("/Match/Versions");
    public bool HasVersionsErrors => HasErrors(VersionsFindings);

    public bool HasVersionRange
    {
        get => Rule.Match.VersionRange is not null;
        set
        {
            if (value == (Rule.Match.VersionRange is not null)) return;
            Rule.Match.VersionRange = value ? new PolicyEditorDraftVersionRange() : null;
            MarkDirty();
            OnPropertyChanged();
            NotifyVersionRangePropertiesChanged();
        }
    }

    public string? MinVersion
    {
        get => Rule.Match.VersionRange?.MinVersion;
        set { EnsureVersionRange().MinVersion = string.IsNullOrEmpty(value) ? null : value; MarkDirty(); }
    }
    public IReadOnlyList<PolicyValidationFinding> MinVersionFindings =>
        [.. FindingsFor("/Match/VersionRange/MinVersion"), .. FindingsEndingAt("/Match/VersionRange")];
    public bool HasMinVersionErrors => HasErrors(MinVersionFindings);

    public string? MaxVersion
    {
        get => Rule.Match.VersionRange?.MaxVersion;
        set { EnsureVersionRange().MaxVersion = string.IsNullOrEmpty(value) ? null : value; MarkDirty(); }
    }
    public IReadOnlyList<PolicyValidationFinding> MaxVersionFindings =>
        [.. FindingsFor("/Match/VersionRange/MaxVersion"), .. FindingsEndingAt("/Match/VersionRange")];
    public bool HasMaxVersionErrors => HasErrors(MaxVersionFindings);

    public bool IncludePrerelease
    {
        get => Rule.Match.VersionRange?.IncludePrerelease ?? false;
        set { EnsureVersionRange().IncludePrerelease = value; MarkDirty(); }
    }

    public int InteractiveIndex
    {
        get => PolicyEditorEnumDisplay.IndexOfTriState(Rule.Match.Interactive);
        set => SetTriState(v => Rule.Match.Interactive = v, value);
    }

    public int SkipHashCheckIndex
    {
        get => PolicyEditorEnumDisplay.IndexOfTriState(Rule.Match.SkipHashCheck);
        set => SetTriState(v => Rule.Match.SkipHashCheck = v, value);
    }

    public int PreReleaseIndex
    {
        get => PolicyEditorEnumDisplay.IndexOfTriState(Rule.Match.PreRelease);
        set => SetTriState(v => Rule.Match.PreRelease = v, value);
    }

    public int HasCustomParametersIndex
    {
        get => PolicyEditorEnumDisplay.IndexOfTriState(Rule.Match.HasCustomParameters);
        set => SetTriState(v => Rule.Match.HasCustomParameters = v, value);
    }

    public int HasCustomInstallLocationIndex
    {
        get => PolicyEditorEnumDisplay.IndexOfTriState(Rule.Match.HasCustomInstallLocation);
        set => SetTriState(v => Rule.Match.HasCustomInstallLocation = v, value);
    }

    public int HasPrePostCommandsIndex
    {
        get => PolicyEditorEnumDisplay.IndexOfTriState(Rule.Match.HasPrePostCommands);
        set => SetTriState(v => Rule.Match.HasPrePostCommands = v, value);
    }

    public int HasKillBeforeOperationIndex
    {
        get => PolicyEditorEnumDisplay.IndexOfTriState(Rule.Match.HasKillBeforeOperation);
        set => SetTriState(v => Rule.Match.HasKillBeforeOperation = v, value);
    }

    public int HasUninstallPreviousIndex
    {
        get => PolicyEditorEnumDisplay.IndexOfTriState(Rule.Match.HasUninstallPrevious);
        set => SetTriState(v => Rule.Match.HasUninstallPrevious = v, value);
    }

    public bool IsDisabled => !Rule.Enabled;
    public bool IsIncompleteNewRule =>
        _sessionViewModel.IsDeferredBlankRule(Rule);
    public bool IsEnabledWithoutMatchConditions =>
        Rule.Enabled && PolicyEditorRuleSemantics.IsCatchAll(Rule.Match);
    public IReadOnlyList<string> SafetyAdvisories =>
        PolicyEditorAdvisories.ForRule(Rule);
    public bool HasSafetyAdvisories => SafetyAdvisories.Count > 0;
    public IReadOnlyList<PolicyValidationFinding> MatchFindings =>
        FindingsEndingAt("/Match");
    public bool HasMatchErrors => HasErrors(MatchFindings);

    public bool HasConstraints
    {
        get => Rule.Constraints is not null;
        set
        {
            if (!IsAllowDecision) return;
            if (value == (Rule.Constraints is not null)) return;
            Rule.Constraints = value ? new PolicyEditorDraftConstraints() : null;
            MarkDirty();
            OnPropertyChanged();
            NotifyConstraintPropertiesChanged();
        }
    }

    internal void ApplyDecision(Decision decision)
    {
        Rule.Decision = decision;
        if (!IsAllowDecision)
        {
            Rule.Constraints = null;
            NotifyConstraintPropertiesChanged();
        }
        OnPropertyChanged(nameof(DecisionIndex));
        OnPropertyChanged(nameof(IsAllowDecision));
        OnPropertyChanged(nameof(HasConstraints));
        MarkDirty();
    }

    internal void RefreshDecisionPresentation()
    {
        OnPropertyChanged(nameof(DecisionIndex));
        OnPropertyChanged(nameof(IsAllowDecision));
        OnPropertyChanged(nameof(HasConstraints));
    }

    public bool AllowInteractive
    {
        get => Rule.Constraints?.AllowInteractive ?? false;
        set { EnsureConstraints().AllowInteractive = value; MarkDirty(); }
    }

    public bool AllowSkipHashCheck
    {
        get => Rule.Constraints?.AllowSkipHashCheck ?? false;
        set { EnsureConstraints().AllowSkipHashCheck = value; MarkDirty(); }
    }

    public bool AllowPreRelease
    {
        get => Rule.Constraints?.AllowPreRelease ?? false;
        set { EnsureConstraints().AllowPreRelease = value; MarkDirty(); }
    }

    public bool AllowCustomInstallLocation
    {
        get => Rule.Constraints?.AllowCustomInstallLocation ?? false;
        set { EnsureConstraints().AllowCustomInstallLocation = value; MarkDirty(); }
    }

    public string AllowedInstallLocationPatterns
    {
        get => Join(Rule.Constraints?.AllowedInstallLocationPatterns);
        set => SetListField(EnsureConstraints().AllowedInstallLocationPatterns, value);
    }

    public bool AllowCustomParameters
    {
        get => Rule.Constraints?.AllowCustomParameters ?? false;
        set { EnsureConstraints().AllowCustomParameters = value; MarkDirty(); }
    }

    public string AllowedCustomParameters
    {
        get => Join(Rule.Constraints?.AllowedCustomParameters);
        set => SetListField(EnsureConstraints().AllowedCustomParameters, value);
    }

    public string AllowedCustomParameterPatterns
    {
        get => Join(Rule.Constraints?.AllowedCustomParameterPatterns);
        set => SetListField(EnsureConstraints().AllowedCustomParameterPatterns, value);
    }

    public string DeniedCustomParameters
    {
        get => Join(Rule.Constraints?.DeniedCustomParameters);
        set => SetListField(EnsureConstraints().DeniedCustomParameters, value);
    }

    public bool AllowPrePostCommands
    {
        get => Rule.Constraints?.AllowPrePostCommands ?? false;
        set { EnsureConstraints().AllowPrePostCommands = value; MarkDirty(); }
    }

    public bool AllowKillBeforeOperation
    {
        get => Rule.Constraints?.AllowKillBeforeOperation ?? false;
        set { EnsureConstraints().AllowKillBeforeOperation = value; MarkDirty(); }
    }

    public bool AllowUninstallPrevious
    {
        get => Rule.Constraints?.AllowUninstallPrevious ?? false;
        set { EnsureConstraints().AllowUninstallPrevious = value; MarkDirty(); }
    }

    public bool AllowUpgrade
    {
        get => Rule.Constraints?.AllowUpgrade ?? false;
        set { EnsureConstraints().AllowUpgrade = value; MarkDirty(); }
    }

    /// <summary>Findings attributed to this rule's identifier or document index.</summary>
    public IReadOnlyList<PolicyValidationFinding> Findings =>
        _sessionViewModel.Session.Findings.All.Where(finding =>
            string.Equals(finding.RuleId, Rule.Id, StringComparison.Ordinal)
            || finding.TargetsPointer($"/Rules/{_ruleIndex}")).ToArray();

    public bool HasFindings => Findings.Count > 0;

    /// <summary>
    /// Re-raises change notification for the findings-derived properties without rebuilding this
    /// wrapper or its parent collection, so a Validate/Save click never steals focus from whichever
    /// field the user was editing.
    /// </summary>
    public void RefreshFindings()
    {
        OnPropertyChanged(nameof(Findings));
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(IsIncompleteNewRule));
        foreach (string property in new[]
        {
            nameof(IdFindings), nameof(HasIdErrors),
            nameof(DecisionFindings), nameof(HasDecisionErrors),
            nameof(ReasonFindings), nameof(HasReasonErrors),
            nameof(MatchFindings), nameof(HasMatchErrors),
            nameof(PackageNamesFindings), nameof(HasPackageNamesErrors),
            nameof(VersionsFindings), nameof(HasVersionsErrors),
            nameof(MinVersionFindings), nameof(HasMinVersionErrors),
            nameof(MaxVersionFindings), nameof(HasMaxVersionErrors),
        })
        {
            OnPropertyChanged(property);
        }
    }

    private IReadOnlyList<PolicyValidationFinding> FindingsFor(params string[] suffixes) =>
        Findings.Where(finding => suffixes.Any(suffix =>
            finding.Pointer.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            || finding.Pointer.Contains(
                suffix + "/",
                StringComparison.OrdinalIgnoreCase))).ToArray();

    private IReadOnlyList<PolicyValidationFinding> FindingsEndingAt(string suffix) =>
        Findings.Where(finding =>
            finding.Pointer.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToArray();

    private static bool HasErrors(IEnumerable<PolicyValidationFinding> findings) =>
        findings.Any(finding => finding.IsError);

    private void SetTriState(Action<TriState> assign, int index)
    {
        if (index < 0 || index >= PolicyEditorEnumDisplay.TriStates.Length) return;
        assign(PolicyEditorEnumDisplay.TriStates[index]);
        MarkDirty();
    }

    private void NotifyVersionRangePropertiesChanged()
    {
        OnPropertyChanged(nameof(MinVersion));
        OnPropertyChanged(nameof(MaxVersion));
        OnPropertyChanged(nameof(IncludePrerelease));
    }

    private void NotifyConstraintPropertiesChanged()
    {
        OnPropertyChanged(nameof(AllowInteractive));
        OnPropertyChanged(nameof(AllowSkipHashCheck));
        OnPropertyChanged(nameof(AllowPreRelease));
        OnPropertyChanged(nameof(AllowCustomInstallLocation));
        OnPropertyChanged(nameof(AllowedInstallLocationPatterns));
        OnPropertyChanged(nameof(AllowCustomParameters));
        OnPropertyChanged(nameof(AllowedCustomParameters));
        OnPropertyChanged(nameof(AllowedCustomParameterPatterns));
        OnPropertyChanged(nameof(DeniedCustomParameters));
        OnPropertyChanged(nameof(AllowPrePostCommands));
        OnPropertyChanged(nameof(AllowKillBeforeOperation));
        OnPropertyChanged(nameof(AllowUninstallPrevious));
        OnPropertyChanged(nameof(AllowUpgrade));
    }

    private PolicyEditorDraftVersionRange EnsureVersionRange() =>
        Rule.Match.VersionRange ??= new PolicyEditorDraftVersionRange();

    private PolicyEditorDraftConstraints EnsureConstraints() =>
        Rule.Constraints ??= new PolicyEditorDraftConstraints();

    private void MarkDirty()
    {
        OnPropertyChanged(nameof(IsDisabled));
        OnPropertyChanged(nameof(IsIncompleteNewRule));
        OnPropertyChanged(nameof(IsEnabledWithoutMatchConditions));
        OnPropertyChanged(nameof(SafetyAdvisories));
        OnPropertyChanged(nameof(HasSafetyAdvisories));
        _sessionViewModel.NotifyDraftChangedCommand.Execute(null);
    }

    public void Dispose()
    {
    }

    private static string Join(IEnumerable<string>? values) =>
        values is null ? "" : string.Join(Environment.NewLine, values);

    private void SetListField(List<string> backing, string? value)
    {
        backing.Clear();
        if (!string.IsNullOrEmpty(value))
        {
            backing.AddRange(value.Split(
                ["\r\n", "\n", "\r"],
                StringSplitOptions.RemoveEmptyEntries));
        }

        MarkDirty();
    }
}
