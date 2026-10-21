using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.CargoManager;

internal sealed class CargoPkgOperationHelper(Cargo cargo)
    : BasePkgOperationHelper(cargo), IManagedPackageOperationHelper
{
    internal static string RequestedVersion(IPackage package, InstallOptions options, OperationType operation) =>
        options.Version.Length > 0 ? options.Version
        : operation == OperationType.Update && package.IsUpgradable ? package.NewVersionString
        : package.HasConcreteVersion ? package.VersionString : "";

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package, InstallOptions options, OperationType operation)
    {
        if (options.SkipHashCheck && operation != OperationType.Uninstall)
            throw new InvalidOperationException("Cargo installation requires integrity verification.");
        string version = RequestedVersion(package, options, operation);
        if (operation == OperationType.Update
            && SemanticVersion.TryParse(version, out var requested)
            && SemanticVersion.TryParse(package.VersionString, out var installed)
            && requested.CompareTo(installed) < 0)
            throw new InvalidOperationException("Refusing to downgrade an installed Cargo package during an update.");
        List<string> arguments = [operation == OperationType.Uninstall ? "uninstall" : "install", package.Id];
        if (operation is not OperationType.Uninstall)
        {
            if (version.Length > 0)
                arguments.AddRange(["--version", CoreTools.EscapeCommandLineArgument(version)]);
            arguments.Add("--locked");
            if (operation == OperationType.Update)
                arguments.Add("--force");
        }
        if (options.CustomInstallLocation.Length > 0)
            arguments.AddRange(["--root", CoreTools.EscapeCommandLineArgument(
                CargoConfiguration.GetRoot(options.CustomInstallLocation))]);
        IReadOnlyList<string> custom = operation switch
        {
            OperationType.Update => options.CustomParameters_Update,
            OperationType.Uninstall => options.CustomParameters_Uninstall,
            _ => options.CustomParameters_Install,
        };
        if (operation != OperationType.Uninstall && package.Source.Name != cargo.DefaultSource.Name
            && !custom.Any(argument => argument is "--git" or "--path" or "--index" or "--registry"))
        {
            var source = package.Source.Url
                ?? throw new InvalidOperationException("The installed Cargo package has no original source.");
            if (source.IsFile)
                arguments.AddRange(["--path", CoreTools.EscapeCommandLineArgument(source.LocalPath)]);
            else if (package.Source.Name.StartsWith("registry+", StringComparison.Ordinal))
                arguments.AddRange(["--index", CoreTools.EscapeCommandLineArgument(source.AbsoluteUri)]);
            else if (source.Scheme == Uri.UriSchemeHttps)
            {
                arguments.AddRange(["--git", CoreTools.EscapeCommandLineArgument(source.GetLeftPart(UriPartial.Path))]);
                if (source.Fragment.Length > 1)
                    arguments.AddRange(["--rev", CoreTools.EscapeCommandLineArgument(source.Fragment[1..])]);
            }
            else
                throw new InvalidOperationException("The installed Cargo package source requires explicit Cargo source arguments.");
        }
        if (custom.Any(argument => argument is "--no-confirm" or "--skip-signatures"
            or "--install-path" or "--disable-strategies" or "--strategies"
            or "--pkg-url" or "--pkg-fmt" or "--bin-dir" or "--only-signed"
            || argument.StartsWith("--install-path=", StringComparison.Ordinal)
            || argument.StartsWith("--disable-strategies=", StringComparison.Ordinal)
            || argument.StartsWith("--bin-dir=", StringComparison.Ordinal)
            || argument.StartsWith("--strategies=", StringComparison.Ordinal)
            || argument.StartsWith("--pkg-", StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "Cargo extension arguments are no longer supported. Replace them with cargo install options.");
        arguments.AddRange(custom);
        return arguments;
    }

    public Task<OperationVeredict?> TryPerformAsync(IPackage package, InstallOptions options,
        OperationType operation, Action<string> log, Action<OperationProgress> progress,
        CancellationToken cancellationToken) =>
        CargoManagedInstaller.TryPerformAsync(cargo, package, options, operation, log, progress, cancellationToken);

    protected override OperationVeredict _getOperationResult(
        IPackage package, OperationType operation, IReadOnlyList<string> processOutput, int returnCode)
    {
        if (returnCode != 0)
            return OperationVeredict.Failure;
        cargo.InvalidateInstalledCache();
        return OperationVeredict.Success;
    }
}
