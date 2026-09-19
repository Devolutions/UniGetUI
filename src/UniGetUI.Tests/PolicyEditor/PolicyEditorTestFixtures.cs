using Devolutions.Now.Policy.Model;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;
using PolicyManagementSnapshot = Devolutions.Now.Policy.Api.PolicyManagementSnapshot;
using PolicyManagementState = Devolutions.Now.Policy.Api.PolicyManagementState;
using PolicyReplacementResponse = Devolutions.Now.Policy.Api.PolicyReplacementResponse;

namespace UniGetUI.Tests.PolicyEditor;

/// <summary>Shared construction helpers for policy editor domain tests.</summary>
internal static class PolicyEditorTestFixtures
{
    public static PolicyDocument BuildDocument(
        string id = "contoso-policy",
        string publisher = "Contoso",
        uint revision = 3,
        Devolutions.Now.Policy.Model.Decision defaultDecision = Devolutions.Now.Policy.Model.Decision.Deny,
        params PolicyRule[] rules)
    {
        return new PolicyDocument
        {
            PolicyFormatVersion = PolicyFormatVersion.Parse("1.2.3"),
            Metadata = new PolicyMetadata
            {
                Id = id,
                Publisher = publisher,
                Revision = revision,
                PublishedAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
                ValidFrom = DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
                ValidUntil = DateTimeOffset.Parse("2027-08-01T00:00:00Z"),
                Description = "Test policy",
                SupportUrl = "https://contoso.example/support",
            },
            Enforcement = new PolicyEnforcement
            {
                DefaultDecision = defaultDecision,
                AuditMode = true,
            },
            Rules = [.. rules],
        };
    }

    public static PolicyRule BuildFullRule(string id = "first-rule")
    {
        return new PolicyRule
        {
            Id = id,
            Enabled = true,
            Priority = 10,
            Decision = Decision.Allow,
            Reason = "Allow trusted sources",
            Match = new PolicyMatch
            {
                Operations = [Operation.Install, Operation.Update],
                Managers = [ManagerName.Winget],
                SourceNames = ["winget"],
                PackageIdentifiers = ExactPackageIdentifiers("Contoso.App"),
                Version = RangeVersions("1.0.0", "2.0.0"),
                Scopes = [Scope.Machine],
                Architectures = [Architecture.X64],
                ExecutionElevation = [Elevation.Standard],
                Interactive = true,
                SkipHashCheck = false,
                PreRelease = null,
                HasCustomParameters = true,
                HasCustomInstallLocation = false,
                HasPrePostCommands = null,
                HasKillBeforeOperation = true,
                HasUninstallPrevious = false,
            },
            Constraints = new PolicyConstraints
            {
                AllowInteractive = true,
                AllowSkipHashCheck = false,
                AllowPreRelease = false,
                AllowCustomInstallLocation = true,
                AllowedInstallLocationPatterns = ["C:\\Apps\\*"],
                AllowCustomParameters = true,
                AllowedCustomParameters = ["/silent"],
                AllowedCustomParameterPatterns = ["/log:*"],
                DeniedCustomParameters = ["/evil"],
                AllowPrePostCommands = false,
                AllowKillBeforeOperation = true,
                AllowUninstallPrevious = false,
                AllowUpgrade = true,
            },
        };
    }

    public static PolicyRule BuildMinimalRule(string id = "minimal-rule")
    {
        return new PolicyRule
        {
            Id = id,
            Enabled = true,
            Priority = 0,
            Decision = Decision.Deny,
            Reason = null,
            Match = new PolicyMatch(),
            Constraints = null,
        };
    }

    public static PackageIdentifierCondition ExactPackageIdentifiers(params string[] identifiers)
    {
        var condition = new PackageIdentifierCondition();
        condition.UseExact([.. identifiers]);
        return condition;
    }

    public static PackageIdentifierCondition PatternPackageIdentifiers(params string[] patterns)
    {
        var condition = new PackageIdentifierCondition();
        condition.UsePatterns([.. patterns]);
        return condition;
    }

    public static VersionCondition ExactVersions(params string[] versions)
    {
        var condition = new VersionCondition();
        condition.UseExact([.. versions]);
        return condition;
    }

    public static VersionCondition RangeVersions(string? minVersion, string? maxVersion)
    {
        var condition = new VersionCondition();
        condition.UseRange(new VersionRange
        {
            MinVersion = minVersion,
            MaxVersion = maxVersion,
        });
        return condition;
    }

    /// <summary>Builds an <c>Active</c> management snapshot carrying <paramref name="policy"/> (or a
    /// freshly built default document) as the currently-stored policy.</summary>
    public static PolicyManagementSnapshot BuildActiveManagement(
        PolicyDocument? policy = null,
        string storeToken = "token-1")
    {
        return new PolicyManagementSnapshot
        {
            State = PolicyManagementState.Active,
            StoreToken = storeToken,
            Policy = policy ?? BuildDocument(),
        };
    }

    /// <summary>Builds a <c>Missing</c> management snapshot (no policy currently stored).</summary>
    public static PolicyManagementSnapshot BuildMissingManagement(string storeToken = "token-missing") =>
        new()
        {
            State = PolicyManagementState.Missing,
            StoreToken = storeToken,
            Policy = null,
        };

    /// <summary>Builds an <c>Invalid</c> management snapshot (a policy is stored but failed to parse).</summary>
    public static PolicyManagementSnapshot BuildInvalidManagement(string storeToken = "token-invalid") =>
        new()
        {
            State = PolicyManagementState.Invalid,
            StoreToken = storeToken,
            Policy = null,
        };

    /// <summary>Builds a successful <see cref="PolicyReplacementResponse"/> whose <c>Management</c> is
    /// <c>Active</c> and carries <paramref name="policy"/> as both the top-level and management-snapshot
    /// policy (mirroring what the real broker returns after a successful write).</summary>
    public static PolicyReplacementResponse BuildReplacementResponse(
        PolicyDocument policy,
        string storeToken = "token-after-save")
    {
        return new PolicyReplacementResponse
        {
            Policy = policy,
            Management = new PolicyManagementSnapshot
            {
                State = PolicyManagementState.Active,
                StoreToken = storeToken,
                Policy = policy,
            },
        };
    }
}
