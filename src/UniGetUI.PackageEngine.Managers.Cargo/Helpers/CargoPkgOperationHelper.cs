using UniGetUI.Core.Tools;
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
        // --version is omitted when there is nothing to pin, so cargo picks the latest itself. An
        // unpinned imported package reports the localized "Latest" as its version, which is
        // display text rather than a version and is more than one word in several languages.
        string requestedVersion = options.Version.Length > 0
            ? options.Version
            : package.HasConcreteVersion
                ? package.VersionString
                : "";
        string[] versionArguments = requestedVersion.Length > 0
            ? ["--version", CoreTools.EscapeCommandLineArgument(requestedVersion)]
            : [];

        bool hasBinstall = ((Cargo)Manager).HasBinstall;

        List<string> parameters;
        switch (operation)
        {
            case OperationType.Install:
                if (hasBinstall)
                    parameters =
                        [Manager.Properties.InstallVerb, .. versionArguments, package.Id];
                else
                    parameters = ["install", package.Id, .. versionArguments];
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
                    parameters.AddRange(["--install-path", CoreTools.EscapeCommandLineArgument(options.CustomInstallLocation)]);
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
