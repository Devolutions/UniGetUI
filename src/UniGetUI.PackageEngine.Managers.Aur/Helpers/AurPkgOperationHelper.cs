using UniGetUI.Core.Data;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.AurManager;

internal sealed class AurPkgOperationHelper : BasePkgOperationHelper
{
    private readonly Aur _aur;

    public AurPkgOperationHelper(Aur manager)
        : base(manager)
    {
        _aur = manager;
    }

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation)
    {
        List<string> parameters = AurOperationArguments.Build(
            operation,
            package.Id,
            _aur.HelperKind,
            CoreData.ElevatorPath,
            CoreData.ElevatorArgs);

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

internal static class AurOperationArguments
{
    public static List<string> Build(
        OperationType operation,
        string packageId,
        AurHelperKind helper,
        string elevatorPath,
        string elevatorArgs)
    {
        List<string> parameters =
        [
            operation switch
            {
                OperationType.Install or OperationType.Update => "-S",
                OperationType.Uninstall => "-Rs",
                _ => throw new InvalidDataException("Invalid package operation"),
            },
        ];

        if (operation is not OperationType.Uninstall)
        {
            parameters.Add("--aur");
            if (helper is AurHelperKind.Paru)
                parameters.Add("--skipreview");
            else if (helper is AurHelperKind.Yay)
                parameters.AddRange(["--answerclean=None", "--answerdiff=None", "--answeredit=None"]);
        }

        parameters.Add("--noconfirm");

        if (!string.IsNullOrWhiteSpace(elevatorPath))
        {
            parameters.Add($"--sudo={elevatorPath}");
            if (!string.IsNullOrWhiteSpace(elevatorArgs))
                parameters.Add($"--sudoflags={elevatorArgs.Trim()}");
            if (!Path.GetFileName(elevatorPath).Equals("sudo", StringComparison.Ordinal))
            {
                if (helper is AurHelperKind.Paru)
                    parameters.Add("--nosudoloop");
                else if (helper is AurHelperKind.Yay)
                    parameters.Add("--sudoloop=false");
            }
        }

        parameters.Add(operation is OperationType.Uninstall ? packageId : $"aur/{packageId}");
        return parameters;
    }
}
