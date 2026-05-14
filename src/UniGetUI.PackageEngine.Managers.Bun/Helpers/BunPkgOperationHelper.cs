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
        List<string> parameters = operation switch {
            OperationType.Install => [Manager.Properties.InstallVerb, $"'{package.Id}@{(options.Version == string.Empty? package.VersionString: options.Version)}'"],
            OperationType.Update => [Manager.Properties.UpdateVerb, $"'{package.Id}@{package.NewVersionString}'"],
            OperationType.Uninstall => [Manager.Properties.UninstallVerb, package.Id],
            _ => throw new InvalidDataException("Invalid package operation")
        };

        if (package.OverridenOptions.Scope == PackageScope.Global ||
            (package.OverridenOptions.Scope is null && options.InstallationScope == PackageScope.Global))
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
