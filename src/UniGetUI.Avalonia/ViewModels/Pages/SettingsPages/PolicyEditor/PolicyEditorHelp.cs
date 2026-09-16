using UniGetUI.Core.Tools;
using Devolutions.Now.Policy.Model;
using PolicyElevation = Devolutions.Now.Policy.Model.Elevation;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

/// <summary>Shared localized help used by tooltips and accessibility descriptions.</summary>
public static class PolicyEditorHelp
{
    public static string StructuredMode => T("Use guided fields to edit the policy. Values managed by UniGetUI or Devolutions Agent are shown read-only.");
    public static string RawMode => T("Edit the complete policy as JSON. Use this view for review or fields not shown in the guided editor; invalid JSON must be corrected before returning.");
    public static string PolicyId => T("Permanent identifier used in policy history and diagnostics, not a display name. Use 1 to 128 characters starting with a letter or number; then use letters, numbers, '.', '_', ':' or '-', for example contoso-policy.");
    public static string Publisher => T("Organization or administrator responsible for this policy. Use a name that users can recognize when reviewing policy details.");
    public static string PolicyFormatVersion => T("Policy document version supported by UniGetUI and Devolutions Agent. It is read-only and is different from the policy revision.");
    public static string ServerVersion => T("Version of Devolutions Agent that supplied this policy information. Use it when comparing behavior or troubleshooting compatibility.");
    public static string Revision => T("Change number assigned by Devolutions Agent when the policy is saved. It increases independently of the policy document version.");
    public static string Published => T("Date and time when Devolutions Agent last committed this policy revision.");
    public static string Description => T("Optional summary of the policy's purpose. Turn this field off to leave the description out.");
    public static string SupportUrl => T("Optional HTTP or HTTPS page where users can learn about this policy or request an exception.");
    public static string ValidFrom => T("Optional date and time when enforcement begins. Leave empty to make the policy effective immediately after it is saved.");
    public static string ValidUntil => T("Optional date and time when enforcement ends. Leave empty for no expiry; when set, it must be later than Valid from.");
    public static string DefaultDecision => T("Action taken when no enabled rule matches. Choose Deny for a least-privilege policy; choose Allow only when unmatched package requests should proceed.");
    public static string RulePrecedence => T("Rules with lower priority numbers are considered first, and Deny wins when priorities tie. This order is fixed.");
    public static string AuditMode => T("When Yes, the broker still evaluates and logs policy decisions but permits requests the policy would deny. Use Yes only temporarily to evaluate rollout; set No to enforce policy.");
    public static string AuditModeWarning => T("Audit mode is on. Policy decisions are evaluated and logged, but requests the policy would deny are still permitted. Set Audit mode to No to enforce policy.");
    public static string AddRule => T("Add an enabled Deny rule. Choose at least one match condition; otherwise the rule applies to every package request.");
    public static string RuleEnabled => T("Turn off to keep the rule for future use without applying it. Only enabled rules can allow or deny requests.");
    public static string DuplicateRule => T("Copy this rule as a starting point for a similar exception or restriction. Give the copy a unique rule ID.");
    public static string MoveRule => T("Move the rule for readability. The priority number—not its position here—determines which matching rule wins.");
    public static string DeleteRule => T("Remove this rule from the policy. The removal takes effect when the policy is saved.");
    public static string RuleId => T("Permanent identifier used in decision logs, not a display name. Use 1 to 128 characters starting with a letter or number; then use letters, numbers, '.', '_', ':' or '-', for example allow-winget-updates.");
    public static string Priority => T("Controls which matching rule wins. Lower numbers run first; if an Allow and Deny rule share the winning priority, Deny wins.");
    public static string Decision => T("Choose Allow to permit a matching request or Deny to block it. Disabled rules have no effect.");
    public static string RuleReason => T("Optional administrator-facing explanation recorded with the rule's decision. Describe why the request is allowed or denied.");
    public static string Operations => T("Limit this rule to package installs, updates, or removals. Leave all choices clear to include every operation.");
    public static string Managers => T("Limit this rule to selected package managers. Leave all choices clear to include requests from every manager.");
    public static string Sources => T("Limit this rule to source names, one per line. Wildcards such as corp-* are supported; leave empty to include every source.");
    public static string PackageIdentifiers => T("Limit this rule to package identifiers, one per line. Wildcards such as Contoso.* are supported; leave empty to include every package identifier.");
    public static string PackageNames => T("Package display-name matching is not currently available. Leave this field empty and use package identifiers instead.");
    public static string Versions => T("Limit this rule to exact package version text, one per line. Leave empty to include any version, including requests that do not specify one.");
    public static string VersionRange => T("Limit this rule to a semantic-version range. A request without a valid semantic version will not match; leave the range off to accept other version formats.");
    public static string MinimumVersion => T("Lowest semantic version included by this rule. Leave empty for no lower limit.");
    public static string MaximumVersion => T("Highest semantic version included by this rule. Leave empty for no upper limit.");
    public static string IncludePrerelease => T("Include prerelease versions such as 2.0.0-beta within this range. Leave off to match stable versions only.");
    public static string Scopes => T("Limit this rule to current-user or all-users package requests. Leave both clear to include either scope and requests that do not specify one.");
    public static string Architectures => T("Limit this rule to selected target architectures. Leave all choices clear to include any architecture and requests that do not specify one.");
    public static string Elevation => T("Limit this rule by requested administrator privileges. Leave both clear to include standard and elevated requests.");
    private static string MatchOption => T("Select a value to restrict this rule to that value. Leaving every value in the group clear means the group does not restrict matching.");
    public static string InteractiveMatch => T("Controls whether the package operation may show or require user interaction instead of running unattended. Any accepts either; Yes requires interactive; No requires unattended.");
    public static string SkipHashMatch => T("Controls whether the request bypasses package integrity checks. Any accepts either; Yes requires bypassing checks; No requires normal verification.");
    public static string PrereleaseMatch => T("Controls whether the request allows prerelease packages. Any accepts either; Yes requires prerelease enabled; No requires stable releases only.");
    public static string CustomParametersMatch => T("Controls whether extra package-manager command options are supplied. Any accepts either; Yes requires extra options; No requires none.");
    public static string CustomLocationMatch => T("Controls whether the request chooses a non-default install folder. Any accepts either; Yes requires a custom location; No requires the manager's default location.");
    public static string PrePostCommandsMatch => T("Controls whether commands run before or after the package operation. Any accepts either; Yes requires such commands; No requires none.");
    public static string KillBeforeMatch => T("Controls whether the request names processes to close before the package operation. Any accepts either; Yes requires a process list; No requires none.");
    public static string UninstallPreviousMatch => T("Controls whether an existing version is removed before an update is installed. Any accepts either; Yes requires removal first; No requires an in-place operation.");
    public static string Constraints => T("Turn on to restrict what a request may do after this rule matches. Without constraints, interactive mode, integrity bypasses, custom options, and other advanced actions remain allowed. Dependency installation, agreement acceptance, and restart behavior remain controlled by the package manager.");
    public static string AllowInteractive => T("Permit a matching request to show installer or package-manager prompts. Turn off to require unattended operation.");
    public static string AllowSkipHashCheck => T("Permit a matching request to bypass package integrity checks. Turn off unless a narrowly reviewed exception requires it.");
    public static string AllowPrerelease => T("Permit a matching request to use prerelease package versions. Turn off to require stable releases.");
    public static string AllowCustomLocation => T("Permit a matching request to choose a non-default install folder. Use the location patterns below to limit approved folders.");
    public static string LocationPatterns => T("Approved custom install folders, one wildcard pattern per line. If custom locations are allowed and this list is empty, any folder is accepted.");
    public static string AllowCustomParameters => T("Permit extra package-manager command options. Use the lists below to allow known options and block dangerous ones.");
    public static string AllowedParameters => T("Extra command options allowed exactly as written, one per line. If both allowed lists are empty, any option is accepted unless denied below.");
    public static string AllowedParameterPatterns => T("Wildcard patterns for allowed extra command options, one per line. Use these for options whose values vary.");
    public static string DeniedParameters => T("Extra command options that must be rejected, one wildcard pattern per line. A denied option always wins over an allowed option.");
    public static string AllowPrePostCommands => T("Permit commands to run before or after the package operation. Turn off unless a narrowly reviewed workflow requires this high-risk capability.");
    public static string AllowKillBefore => T("Permit named processes to be closed before the package operation. Turn off to prevent policy-approved requests from terminating applications.");
    public static string AllowUninstallPrevious => T("Permit removing an installed version before applying an update. Turn off to require updates that do not uninstall first.");
    public static string AllowUpgrade => T("Permit an install request to leave an already installed package unchanged instead of upgrading it. Turn off to reject requests that use this option.");
    public static string Validate => T("Check the current policy with Devolutions Agent without saving it. Correct errors before saving and review every warning.");
    public static string Save => T("Check and save the policy. Warnings require acknowledgement, and Windows may ask for administrator approval.");
    public static string Overwrite => T("Replace a policy that changed after editing began. Review the newer policy first because overwriting discards those external changes.");
    public static string Findings => T("Errors must be corrected before saving; warnings require review and confirmation. Use Go to field to open the affected setting.");
    public static string GoToFinding => T("Open and focus the setting associated with this finding.");
    public static string GoToRawError => T("Focus the policy JSON so you can correct the reported formatting or structure problem.");
    public static string CanonicalJson => T("Read-only JSON for the active policy exactly as Devolutions Agent recognizes it. Use it for review, diagnostics, or comparison.");
    public static string CopyCanonicalJson => T("Copy the complete active-policy JSON for review, diagnostics, or comparison.");
    public static string RefreshPolicy => T("Reload the policy state and active policy details from Devolutions Agent.");
    public static string AgentWriteCapability => T("Whether Devolutions Agent allows policy files to be changed. Read-only means the Agent configuration or policy location prevents changes.");
    public static string AppWriteAvailability => T("Whether this UniGetUI installation can safely request policy changes. Available requires both Agent permission and a trusted administrator helper.");
    public static string AppWriteReason => T("Explains the Agent restriction or local installation condition that prevents policy changes.");
    public static string ElevationRequired => T("Whether saving policy changes requires Windows administrator approval.");
    public static string EditPolicy => T("Open the active policy for editing. The policy ID remains locked so this updates the same policy.");
    public static string CreatePolicy => T("Create the first policy for the configured location. New policies start with Deny as the default and no rules.");
    public static string RepairPolicy => T("Replace an invalid policy with a new valid policy after reviewing the reported problems.");
    public static string ReplaceIdentity => T("Replace the active policy with a different policy ID. Use only when intentionally creating a new policy identity.");
    public static string ManagementState => T("Current policy state: Active is usable, Missing means no policy file exists, and Invalid means the stored policy needs repair.");
    public static string ConfiguredPath => T("Location where Devolutions Agent reads and writes the policy file. Change this location in Agent configuration, not here.");
    public static string PathSource => T("Shows whether Devolutions Agent is using its default policy location or an explicitly configured location.");

    internal static string EnumOption<TEnum>(TEnum value) where TEnum : struct, Enum =>
        value switch
        {
            Operation.Install =>
                T("Install applies this rule to requests that add a package. Leave all operations clear to include installs, updates, and removals."),
            Operation.Update =>
                T("Update applies this rule to requests that change an installed package to another version. Leave all operations clear to include every operation."),
            Operation.Uninstall =>
                T("Uninstall applies this rule to requests that remove a package. Leave all operations clear to include every operation."),
            Scope.User =>
                T("User applies this rule to packages installed for the current user. Leave both scope choices clear to include all scopes and requests that do not state one."),
            Scope.Machine =>
                T("Machine applies this rule to packages installed for all users and commonly requires administrator approval. Leave both scope choices clear to include all scopes and requests that do not state one."),
            Architecture.X86 =>
                T("X86 applies this rule to 32-bit Intel or AMD packages. Leave all architectures clear to include any architecture and requests that do not specify one."),
            Architecture.X64 =>
                T("X64 applies this rule to 64-bit Intel or AMD packages. Leave all architectures clear to include any architecture and requests that do not specify one."),
            Architecture.Arm64 =>
                T("Arm64 applies this rule to 64-bit ARM packages. Leave all architectures clear to include any architecture and requests that do not specify one."),
            Architecture.Neutral =>
                T("Neutral applies this rule to packages that are not tied to one processor architecture. Leave all architectures clear to include any architecture."),
            PolicyElevation.Standard =>
                T("Standard applies this rule when the request runs without administrator privileges. It excludes elevated requests and machine-wide work that requests elevation; leave both choices clear to include either."),
            PolicyElevation.Elevated =>
                T("Elevated applies this rule when the request requires administrator privileges. Leave both elevation choices clear to include standard and elevated requests."),
            ManagerName manager =>
                CoreTools.Translate(
                    "{0} applies this rule to requests handled by that package manager. Leave all managers clear to include every package manager.",
                    CoreTools.Translate(manager.ToString())),
            _ => MatchOption,
        };

    private static string T(string value) => CoreTools.Translate(value);
}
