using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.FlatpakManager;

internal sealed class FlatpakPkgOperationHelper : BasePkgOperationHelper
{
    public FlatpakPkgOperationHelper(Flatpak manager)
        : base(manager) { }

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation)
    {
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

        parameters.Add("--noninteractive");
        parameters.Add("-y");
        parameters.Add(package.Id);

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
