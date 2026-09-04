using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.BunManager;

internal sealed class BunPkgOperationHelper : BasePkgOperationHelper
{
    public BunPkgOperationHelper(Bun manager) : base(manager) { }

    protected override IReadOnlyList<string> _getOperationParameters(IPackage package,
        InstallOptions options, OperationType operation)
    {
        // Bun is called directly (not through PowerShell), so we do NOT use quotes.
        // Quotes would be passed literally to bun.exe, which doesn't understand them.
        // Unlike Npm which goes through PowerShell on Windows, Bun always executes directly.

        List<string> parameters = operation switch
        {
            OperationType.Install =>
            [
                Manager.Properties.InstallVerb,
                BuildSpec(package.Id, ResolveRequestedVersion(package, options)),
            ],
            OperationType.Update =>
            [
                Manager.Properties.UpdateVerb,
                BuildSpec(package.Id, package.NewVersionString),
            ],
            OperationType.Uninstall => [Manager.Properties.UninstallVerb, RequireInertId(package.Id)],
            _ => throw new InvalidDataException("Invalid package operation")
        };

        string effectiveScope = package.OverridenOptions.Scope ?? options.InstallationScope;
        if (effectiveScope is not PackageScope.Local)
            parameters.Add("--global");

        parameters.AddRange(operation switch
        {
            OperationType.Update => options.CustomParameters_Update,
            OperationType.Uninstall => options.CustomParameters_Uninstall,
            _ => options.CustomParameters_Install,
        });

        return parameters;
    }

    private static string RequireInertId(string id)
    {
        if (!CoreTools.IsCommandLineInertValue(id))
            throw new InvalidOperationException(
                $"Refusing to build a Bun command line for the package identifier \"{id}\": it contains characters that would split it into separate arguments."
            );

        return id;
    }

    /// <summary>
    /// The version to install: the requested one, or the package's own when it has a real version.
    /// An unpinned imported package has none, and bun then installs the latest.
    /// </summary>
    private static string ResolveRequestedVersion(IPackage package, InstallOptions options) =>
        options.Version.Length > 0 ? options.Version
        : package.HasConcreteVersion ? package.VersionString
        : "";

    // A version is appended only when there is one; bun installs the latest when none is given.
    // A version that is present but unusable is refused rather than dropped, so a request for a
    // specific version never quietly turns into a request for the latest.
    private static string BuildSpec(string id, string version)
    {
        if (version.Length > 0 && !CoreTools.IsValidPackageVersion(version))
            throw new InvalidOperationException(
                $"Refusing to build a Bun command line for package version \"{version}\": it is not a valid package version."
            );

        string spec = version.Length > 0 ? $"{id}@{version}" : id;

        if (!CoreTools.IsCommandLineInertValue(spec))
            throw new InvalidOperationException(
                $"Refusing to build a Bun command line for the package specifier \"{spec}\": it contains characters that would split it into separate arguments."
            );

        return spec;
    }

    protected override OperationVeredict _getOperationResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode)
    {
        return returnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;
    }
}
