using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;
// Aliased to avoid clashing with UniGetUI.PackageEngine.Enums.Architecture.
using BrokerArchitecture = Devolutions.Now.Policy.Api.Architecture;

namespace UniGetUI.PackageEngine.AgentBroker;

/// <summary>
/// Builds broker protocol requests from UniGetUI domain objects.
/// Maps IPackage + InstallOptions + OperationType into the canonical
/// <see cref="PackageRequest"/> consumed by the Devolutions Agent broker.
/// </summary>
public static class BrokerRequestBuilder
{
    /// <summary>Build a broker request from UniGetUI package operation parameters.</summary>
    /// <param name="effectiveInstallLocation">
    /// The install location resolved by the caller for this specific operation (e.g. the
    /// registry-detected portable location for WinGet updates), or null to omit it.
    /// </param>
    public static PackageOperationRequest Build(
        IPackage package,
        InstallOptions options,
        OperationType role,
        string? effectiveInstallLocation = null)
    {

        ManagerName manager = MapManagerName(package.Manager.Name);

        // The broker refuses empty custom parameters; blank ones carry nothing, so they are dropped.
        List<string> customParameters = [.. GetCustomParameters(options, role).Where(parameter => !string.IsNullOrWhiteSpace(parameter))];

        // Validate what will actually be sent.
        IReadOnlyList<string> issues = BrokerRequestValidator.Validate(
            package,
            options,
            role,
            effectiveInstallLocation,
            customParameters);
        if (issues.Count > 0)
            throw new BrokerRequestValidationException(issues);

        return new PackageOperationRequest
        {
            RequestId = BrokerClient.GenerateRequestId(),
            CreatedAt = DateTimeOffset.UtcNow,
            Operation = MapOperation(role),
            Manager = manager,
            CaptureOutput = true,
            Source = new RequestSource
            {
                Name = package.Source.Name,
                // Most managers identify their source by name and refuse a URL.
                Url = BrokerRequestValidator.ManagerAcceptsSourceUrl(manager)
                    ? package.Source.Url?.ToString()
                    : null,
            },
            Package = new RequestPackage
            {
                Id = package.Id,
                Version = ResolveVersion(manager, package, options, role),
                Architecture = ResolveArchitecture(manager, package, options, role),
            },
            Options = new RequestOptions
            {
                // The per-package scope override takes precedence over the saved options,
                // matching the local WinGet execution path.
                Scope = ResolveScope(manager, package, options),
                Interactive = options.InteractiveInstallation,
                // Neither flag means anything for an uninstall, matching the local path.
                SkipHashCheck = role is not OperationType.Uninstall && options.SkipHashCheck,
                PreRelease = role is not OperationType.Uninstall && options.PreRelease,
                CustomParameters = customParameters,
                CustomInstallLocation = NullIfEmpty(effectiveInstallLocation),
                // Kill/pre/post actions are owned by the broker for brokered operations:
                // they are sent in the request (and skipped locally) so that policy is
                // evaluated before anything runs and actions never execute twice.
                KillBeforeOperation = options.KillBeforeOperation ?? [],
                PreOperationCommand = GetPreCommand(options, role),
                PostOperationCommand = GetPostCommand(options, role),
                UninstallPrevious = role is OperationType.Update && options.UninstallPreviousVersionsOnUpdate,
                // NOTE: SkipMinorUpdates is UniGetUI loader-side filtering and must not be
                // mapped to the broker's NoUpgrade option.
            },
        };
    }

    /// <summary>
    /// Every problem that would stop <see cref="Build"/> for these values, without building a
    /// request: the field rules of <see cref="BrokerRequestValidator"/> as well as the
    /// command-line safety guards. Used to preview a brokered operation before it starts.
    /// </summary>
    public static IReadOnlyList<string> FindProblems(
        IPackage package,
        InstallOptions options,
        OperationType role,
        string? effectiveInstallLocation = null)
    {
        if (!SupportsManager(package.Manager.Name))
            return [];

        try
        {
            Build(package, options, role, effectiveInstallLocation);
            return [];
        }
        catch (BrokerRequestValidationException ex)
        {
            return ex.Issues;
        }
        catch (InvalidOperationException ex)
        {
            return [ex.Message];
        }
    }

    /// <summary>
    /// Whether the operation is the local PowerShell 5 retry that adds <c>-AllowClobber</c>
    /// after a command conflict. The broker accepts no PowerShell custom parameter, so the retry
    /// cannot be sent and the conflict has to be reported instead.
    /// </summary>
    public static bool IsUnsupportedAllowClobberRetry(IPackage package, OperationType role) =>
        role is OperationType.Install
        && package.OverridenOptions.PowerShell_AllowClobber
        && TryMapManagerName(package.Manager.Name, out ManagerName manager)
        && manager is ManagerName.PowerShell;

    private static Operation MapOperation(OperationType role) => role switch
    {
        OperationType.Install => Operation.Install,
        OperationType.Update => Operation.Update,
        OperationType.Uninstall => Operation.Uninstall,
        _ => throw new ArgumentException($"Unsupported operation type: {role}"),
    };

    /// <summary>
    /// Whether a UniGetUI manager name can be mapped to a broker protocol manager,
    /// and therefore whether operations for it can be routed through the broker.
    /// </summary>
    public static bool SupportsManager(string managerName) =>
        TryMapManagerName(managerName, out _);

    /// <summary>
    /// Maps UniGetUI manager names to the broker protocol canonical managers.
    /// PowerShell 5 and PowerShell 7 are modeled as separate managers.
    /// </summary>
    private static ManagerName MapManagerName(string managerName) =>
        TryMapManagerName(managerName, out var mapped)
            ? mapped
            : throw new ArgumentException($"Unsupported manager for the broker: {managerName}");

    /// <summary>
    /// The concrete version the operation installs, when UniGetUI knows it.
    /// </summary>
    /// <remarks>
    /// The broker evaluates version conditions against the version sent in the request: a Deny
    /// rule with a version condition matches a request whose version is unknown. So the version
    /// is resolved here instead of letting the package manager pick "the latest": the one the
    /// user selected, else the one shown for an install, else the one an update moves to.
    /// A known version the broker would not accept for the manager is omitted rather than sent,
    /// and uninstalls never carry a version, since version conditions do not apply to them.
    /// </remarks>
    internal static string? ResolveVersion(
        ManagerName manager,
        IPackage package,
        InstallOptions options,
        OperationType role)
    {
        if (role is OperationType.Uninstall || !BrokerRequestValidator.ManagerAcceptsVersion(manager))
            return null;

        if (role is OperationType.Install && options.Version.Length > 0)
            return options.Version;

        // A pre-release install may resolve to a newer version than the one listed.
        if ((role is OperationType.Install && options.PreRelease)
            || !BrokerRequestValidator.ManagerHasKnownVersionRules(manager))
            return null;

        string? candidate = role switch
        {
            OperationType.Install when package.HasConcreteVersion => package.VersionString,
            OperationType.Update when package.IsUpgradable => package.NewVersionString,
            _ => null,
        };

        if (
            string.IsNullOrWhiteSpace(candidate)
            || candidate.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            || !CoreTools.IsOptionSafeValue(candidate)
            || BrokerRequestValidator.CheckVersion(manager, package.Manager.DisplayName, candidate) is not null
        )
            return null;

        return candidate;
    }

    internal static bool TryMapManagerName(string managerName, out ManagerName mapped)
    {
        ManagerName? result = managerName.ToLowerInvariant() switch
        {
            "winget" => ManagerName.Winget,
            "powershell" => ManagerName.PowerShell,
            "powershell7" or "pwsh" => ManagerName.PowerShell7,
            "apt" => ManagerName.Apt,
            "bun" => ManagerName.Bun,
            "cargo" => ManagerName.Cargo,
            "chocolatey" => ManagerName.Chocolatey,
            "dnf" => ManagerName.Dnf,
            ".net tool" or "dotnet" => ManagerName.Dotnet,
            "flatpak" => ManagerName.Flatpak,
            "homebrew" => ManagerName.Homebrew,
            "npm" => ManagerName.Npm,
            "pacman" => ManagerName.Pacman,
            "pip" => ManagerName.Pip,
            "scoop" => ManagerName.Scoop,
            "snap" => ManagerName.Snap,
            "vcpkg" => ManagerName.Vcpkg,
            _ => null,
        };

        mapped = result ?? default;
        return result is not null;
    }

    /// <summary>
    /// The architecture sent to the broker, or null to let the manager decide. Uninstalls never
    /// select one, and Scoop only selects one on install, like the local execution paths.
    /// </summary>
    /// <remarks>
    /// WinGet_DropArchAndScope is set after an "update not applicable" result to retry without
    /// the scope/architecture constraints; mirror the local WinGet behavior so the AutoRetry
    /// does not rebuild the same constrained request indefinitely.
    /// </remarks>
    internal static BrokerArchitecture? ResolveArchitecture(
        ManagerName manager,
        IPackage package,
        InstallOptions options,
        OperationType role)
    {
        return ArchitectureApplies(manager, package, role) ? MapArchitecture(options.Architecture) : null;
    }

    internal static bool ArchitectureApplies(ManagerName manager, IPackage package, OperationType role) =>
        !package.OverridenOptions.WinGet_DropArchAndScope
        && role is not OperationType.Uninstall
        && (manager is not ManagerName.Scoop || role is OperationType.Install);

    /// <summary>The scope sent to the broker for these options, or null to let it decide.</summary>
    internal static Scope? ResolveScope(ManagerName manager, IPackage package, InstallOptions options) =>
        package.OverridenOptions.WinGet_DropArchAndScope
            ? null
            : MapScope(manager, package.OverridenOptions.Scope ?? options.InstallationScope);

    private static Scope? MapScope(ManagerName manager, string? scope)
    {
        if (string.IsNullOrEmpty(scope))
        {
            return null;
        }

        Scope? mapped = scope.ToLowerInvariant() switch
        {
            "user" => Scope.User,
            "machine" => Scope.Machine,
            "global" => Scope.Machine,
            _ => null,
        };

        return mapped is Scope.Machine && !ManagerScopeDistinguishesSystemWideInstalls(manager)
            ? null
            : mapped;
    }

    private static bool ManagerScopeDistinguishesSystemWideInstalls(ManagerName manager) =>
        manager is not ManagerName.Pip;

    private static BrokerArchitecture? MapArchitecture(string? architecture)
    {
        if (string.IsNullOrEmpty(architecture))
        {
            return null;
        }

        return architecture.ToLowerInvariant() switch
        {
            "x86" => BrokerArchitecture.X86,
            "x64" => BrokerArchitecture.X64,
            "arm64" => BrokerArchitecture.Arm64,
            "neutral" => BrokerArchitecture.Neutral,
            _ => null,
        };
    }

    private static List<string> GetCustomParameters(InstallOptions options, OperationType role) => role switch
    {
        OperationType.Install => options.CustomParameters_Install ?? [],
        OperationType.Update => options.CustomParameters_Update ?? [],
        OperationType.Uninstall => options.CustomParameters_Uninstall ?? [],
        _ => [],
    };

    private static string? GetPreCommand(InstallOptions options, OperationType role) => role switch
    {
        OperationType.Install => NullIfEmpty(options.PreInstallCommand),
        OperationType.Update => NullIfEmpty(options.PreUpdateCommand),
        OperationType.Uninstall => NullIfEmpty(options.PreUninstallCommand),
        _ => null,
    };

    private static string? GetPostCommand(InstallOptions options, OperationType role) => role switch
    {
        OperationType.Install => NullIfEmpty(options.PostInstallCommand),
        OperationType.Update => NullIfEmpty(options.PostUpdateCommand),
        OperationType.Uninstall => NullIfEmpty(options.PostUninstallCommand),
        _ => null,
    };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
