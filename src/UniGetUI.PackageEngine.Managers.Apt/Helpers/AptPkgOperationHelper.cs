using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.AptManager;

internal sealed class AptPkgOperationHelper : BasePkgOperationHelper
{
    public AptPkgOperationHelper(Apt manager)
        : base(manager) { }

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation)
    {
        // apt always requires root — force elevation via InstallOptions (reference type, persists)
        options.RunAsAdministrator = true;

        List<string> parameters =
        [
            operation switch
            {
                OperationType.Install   => Manager.Properties.InstallVerb,
                OperationType.Uninstall => Manager.Properties.UninstallVerb,
                OperationType.Update    => Manager.Properties.UpdateVerb,
                _ => throw new InvalidDataException("Invalid package operation"),
            },
        ];

        if (operation == OperationType.Update)
            parameters.Add("--only-upgrade");

        parameters.Add("-y");
        parameters.Add(package.Id);

        if (options.SkipHashCheck)
            parameters.Add("--allow-unauthenticated");

        parameters.AddRange(
            operation switch
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
