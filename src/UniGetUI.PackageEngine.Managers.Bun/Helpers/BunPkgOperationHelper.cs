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
                $"{package.Id}@{(options.Version == string.Empty ? package.VersionString : options.Version)}",
            ],
            OperationType.Update =>
            [
                Manager.Properties.UpdateVerb,
                $"{package.Id}@{package.NewVersionString}",
            ],
            OperationType.Uninstall => [Manager.Properties.UninstallVerb, package.Id],
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

    protected override OperationVeredict _getOperationResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode)
    {
        return returnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;
    }
}
