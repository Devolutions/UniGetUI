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
                useShellQuotes
                    ? $"'{package.Id}@{(options.Version == string.Empty ? package.VersionString : options.Version)}'"
                    : $"{package.Id}@{(options.Version == string.Empty ? package.VersionString : options.Version)}",
            ],
            OperationType.Update =>
            [
                Manager.Properties.UpdateVerb,
                useShellQuotes
                    ? $"'{package.Id}@{package.NewVersionString}'"
                    : $"{package.Id}@{package.NewVersionString}",
            ],
            OperationType.Uninstall => [Manager.Properties.UninstallVerb, package.Id],
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
