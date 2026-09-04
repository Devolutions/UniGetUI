using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.NpmManager;

internal sealed class NpmPkgOperationHelper : BasePkgOperationHelper
{
    public NpmPkgOperationHelper(Npm manager)
        : base(manager) { }

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation
    )
    {
        List<string> parameters = operation switch
        {
            OperationType.Install =>
            [
                Manager.Properties.InstallVerb,
                ResolveInstallSpec(package.Id, ResolveRequestedVersion(package, options)),
            ],
            OperationType.Update =>
            [
                Manager.Properties.UpdateVerb,
                ResolveInstallSpec(package.Id, package.NewVersionString),
            ],
            OperationType.Uninstall => [Manager.Properties.UninstallVerb, ResolveLocalName(package.Id)],
            _ => throw new InvalidDataException("Invalid package operation"),
        };

        if (
            package.OverridenOptions.Scope == PackageScope.Global
            || (
                package.OverridenOptions.Scope is null
                && options.InstallationScope == PackageScope.Global
            )
        )
            parameters.Add("--global");

        if (options.PreRelease)
            parameters.AddRange(["--include", "dev"]);

        parameters.AddRange(
            operation switch
            {
                OperationType.Update => options.CustomParameters_Update,
                OperationType.Uninstall => options.CustomParameters_Uninstall,
                _ => options.CustomParameters_Install,
            }
        );

        return parameters;
    }

    /// <summary>
    /// npm-aliased dependencies (package.json entries like "eslint-v9": "npm:eslint@^9.x")
    /// are reported by `npm outdated --json` / `npm list --json` with a package id shaped
    /// like "eslint-v9:eslint@^9.x" -- the local alias name, a literal colon, then the raw
    /// alias target specifier (see Npm.ParseAvailableUpdatesOutput / ParseInstalledPackagesOutput,
    /// which pass that id straight through as package.Id). Real npm package names can never
    /// contain a colon, so its presence in package.Id unambiguously identifies an alias.
    /// </summary>
    private static bool TryParseAlias(string id, out string localName, out string targetName)
    {
        int colonIndex = id.IndexOf(':');
        if (colonIndex <= 0)
        {
            localName = id;
            targetName = "";
            return false;
        }

        localName = id[..colonIndex];
        string targetSpec = id[(colonIndex + 1)..];
        int atIndex = targetSpec.LastIndexOf('@');
        targetName = atIndex > 0 ? targetSpec[..atIndex] : targetSpec;
        return true;
    }

    private static string ResolveLocalName(string id) =>
        TryParseAlias(id, out string localName, out _) ? localName : id;

    /// <summary>
    /// Builds the npm install/update specifier for a package, preserving alias syntax
    /// ("localName@npm:targetName@version") for aliased dependencies instead of treating
    /// package.Id as a literal, directly-installable package name.
    /// </summary>
    /// <summary>
    /// The version to install: the requested one, or the package's own when it has a real version.
    /// An unpinned imported package has none, and npm then installs the latest.
    /// </summary>
    private static string ResolveRequestedVersion(IPackage package, InstallOptions options) =>
        options.Version.Length > 0 ? options.Version
        : package.HasConcreteVersion ? package.VersionString
        : "";

    // A version is appended only when there is one; npm installs the latest when none is given.
    // A version that is present but unusable is refused rather than dropped, so a request for a
    // specific version never quietly turns into a request for the latest. A dist-tag such as
    // "next" or "beta" is a usable version and is passed through.
    private string ResolveInstallSpec(string id, string version)
    {
        if (version.Length > 0 && !CoreTools.IsValidPackageVersion(version))
            throw new InvalidOperationException(
                $"Refusing to build an {Manager.Name} command line for package version \"{version}\": it is not a valid package version."
            );

        bool aliased = TryParseAlias(id, out string localName, out string targetName);
        string suffix = version.Length > 0 ? $"@{version}" : "";

        return aliased ? $"{localName}@npm:{targetName}{suffix}" : $"{id}{suffix}";
    }

    protected override OperationVeredict _getOperationResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode
    )
    {
        return returnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;
    }
}
