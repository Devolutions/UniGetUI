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
        // On Windows, npm runs through PowerShell (-Command), so single quotes act as
        // PowerShell string delimiters and are stripped before reaching npm.
        // On macOS/Linux, npm is called directly (no shell), so single quotes are passed
        // literally and must NOT be included.
        bool useShellQuotes = OperatingSystem.IsWindows();

        List<string> parameters = operation switch
        {
            OperationType.Install =>
            [
                Manager.Properties.InstallVerb,
                FormatSpec(
                    ResolveInstallSpec(package.Id, options.Version == string.Empty ? package.VersionString : options.Version),
                    useShellQuotes
                ),
            ],
            OperationType.Update =>
            [
                Manager.Properties.UpdateVerb,
                FormatSpec(ResolveInstallSpec(package.Id, package.NewVersionString), useShellQuotes),
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

    private static string FormatSpec(string spec, bool useShellQuotes) =>
        useShellQuotes ? $"'{spec}'" : spec;

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
    private static string ResolveInstallSpec(string id, string version) =>
        TryParseAlias(id, out string localName, out string targetName)
            ? $"{localName}@npm:{targetName}@{version}"
            : $"{id}@{version}";

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
