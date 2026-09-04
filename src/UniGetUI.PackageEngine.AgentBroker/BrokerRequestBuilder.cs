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
        // WinGet_DropArchAndScope is set after an "update not applicable" result to retry
        // without the scope/architecture constraints; mirror the local WinGet behavior so
        // the AutoRetry does not rebuild the same constrained request indefinitely.
        bool dropArchAndScope = package.OverridenOptions.WinGet_DropArchAndScope;

        ManagerName manager = MapManagerName(package.Manager.Name);

        // This path does not go through BasePkgOperationHelper, so the checks that apply to every
        // manager have to be repeated here: the broker builds a command line from these values,
        // and an identifier such as "requests --index-url https://host" would become real options.
        if (
            !CoreTools.IsOptionSafeIdentifier(
                package.Id,
                package.Manager.IdentifiersAreQuotedOnCommandLine
            )
        )
            throw new InvalidOperationException(
                $"Refusing to build a {manager} broker request for the package identifier \"{package.Id}\": it would be read as a command-line option or split into further arguments."
            );

        if (!CoreTools.IsOptionSafeValue(options.Version))
            throw new InvalidOperationException(
                $"Refusing to build a {manager} broker request for package {package.Id}: the requested version \"{options.Version}\" would be read as a command-line option."
            );

        if (ManagerCommandLineIsShellInterpreted(manager))
        {
            if (!CoreTools.IsValidPackageIdentifier(package.Id))
                throw new InvalidOperationException(
                    $"Refusing to build a {manager} broker request for the package identifier \"{package.Id}\": it is not a valid package identifier."
                );

            if (options.Version.Length > 0 && !CoreTools.IsValidPackageVersion(options.Version))
                throw new InvalidOperationException(
                    $"Refusing to build a {manager} broker request for package {package.Id}: the requested version \"{options.Version}\" is not a valid package version."
                );
        }

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
                Url = package.Source.Url?.ToString(),
            },
            Package = new RequestPackage
            {
                Id = package.Id,
                Version = string.IsNullOrEmpty(options.Version) ? null : options.Version,
                Architecture = dropArchAndScope ? null : MapArchitecture(options.Architecture),
            },
            Options = new RequestOptions
            {
                // The per-package scope override takes precedence over the saved options,
                // matching the local WinGet execution path.
                Scope = dropArchAndScope
                    ? null
                    : MapScope(package.OverridenOptions.Scope ?? options.InstallationScope),
                Interactive = options.InteractiveInstallation,
                SkipHashCheck = options.SkipHashCheck,
                PreRelease = options.PreRelease,
                CustomParameters = GetCustomParameters(options, role),
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

    private static bool ManagerCommandLineIsShellInterpreted(ManagerName manager) =>
        manager
            is ManagerName.PowerShell
                or ManagerName.PowerShell7
                or ManagerName.Scoop
                or ManagerName.Npm;

    private static bool TryMapManagerName(string managerName, out ManagerName mapped)
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

    private static Scope? MapScope(string? scope)
    {
        if (string.IsNullOrEmpty(scope))
        {
            return null;
        }

        return scope.ToLowerInvariant() switch
        {
            "user" => Scope.User,
            "machine" => Scope.Machine,
            "global" => Scope.Machine,
            _ => null,
        };
    }

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
