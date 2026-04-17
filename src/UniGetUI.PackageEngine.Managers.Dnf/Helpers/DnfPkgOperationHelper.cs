using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.DnfManager;

internal sealed class DnfPkgOperationHelper : BasePkgOperationHelper
{
    public DnfPkgOperationHelper(Dnf manager)
        : base(manager) { }

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation)
    {
        // dnf always requires root — force elevation via InstallOptions (reference type, persists)
        options.RunAsAdministrator = true;

        List<string> parameters =
        [
            operation switch
            {
                OperationType.Install   => Manager.Properties.InstallVerb,
                OperationType.Update    => Manager.Properties.UpdateVerb,
                OperationType.Uninstall => Manager.Properties.UninstallVerb,
                _ => throw new InvalidDataException("Invalid package operation"),
            },
            "-y",
            package.Id,
        ];

        if (options.SkipHashCheck)
            parameters.Add("--nogpgcheck");

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
