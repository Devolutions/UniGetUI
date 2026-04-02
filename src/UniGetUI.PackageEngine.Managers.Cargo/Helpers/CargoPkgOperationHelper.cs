using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.CargoManager;

internal sealed class CargoPkgOperationHelper(Cargo cargo) : BasePkgOperationHelper(cargo)
{
    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation
    )
    {
        var installVersion = options.Version == string.Empty ? package.VersionString : options.Version;

        bool hasBinstall = ((Cargo)Manager).HasBinstall;

        List<string> parameters;
        switch (operation)
        {
            case OperationType.Install:
                if (hasBinstall)
                    parameters = [Manager.Properties.InstallVerb, "--version", installVersion, package.Id];
                else
                    parameters = ["install", package.Id, "--version", installVersion];
                break;

            case OperationType.Update:
                if (hasBinstall)
                    parameters = [Manager.Properties.UpdateVerb, package.Id];
                else
                    parameters = ["install", package.Id, "--force"];
                break;

            case OperationType.Uninstall:
                parameters = [Manager.Properties.UninstallVerb, package.Id];
                break;

            default:
                throw new InvalidDataException("Invalid package operation");
        }

        if (operation is OperationType.Install or OperationType.Update)
        {
            if (hasBinstall)
            {
                parameters.Add("--no-confirm");

                if (options.SkipHashCheck)
                    parameters.Add("--skip-signatures");

                if (options.CustomInstallLocation != "")
                    parameters.AddRange(["--install-path", options.CustomInstallLocation]);
            }
        }

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
        if (returnCode == 0)
        {
            ((Cargo)Manager).InvalidateInstalledCache();
            return OperationVeredict.Success;
        }
        return OperationVeredict.Failure;
    }
}
