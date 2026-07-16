using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.PowerShell7Manager;

internal sealed class PowerShell7PkgOperationHelper : BasePkgOperationHelper
{
    public PowerShell7PkgOperationHelper(PowerShell7 manager)
        : base(manager) { }

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation
    )
    {
        List<string> parameters =
        [
            operation switch
            {
                OperationType.Install => Manager.Properties.InstallVerb,
                OperationType.Update => Manager.Properties.UpdateVerb,
                OperationType.Uninstall => Manager.Properties.UninstallVerb,
                _ => throw new InvalidDataException("Invalid package operation"),
            },
        ];
        parameters.AddRange(["-Name", package.Id, "-Confirm:$false"]);

        if (operation is OperationType.Install)
        {
            if (options.Version != "")
                parameters.AddRange(["-Version", options.Version]);
        }
        else if (operation is OperationType.Update)
        {
            parameters.Add("-Force");
        }
        else if (operation is OperationType.Uninstall)
        {
            parameters.AddRange(["-Version", package.VersionString]);
        }

        if (operation is not OperationType.Uninstall)
        {
            parameters.AddRange(["-TrustRepository", "-AcceptLicense"]);

            if (options.PreRelease)
                parameters.Add("-Prerelease");

            // The scope chosen in the options dialog wins; fall back to the auto-detected install scope
            string scope = options.InstallationScope.Length > 0
                ? options.InstallationScope
                : package.OverridenOptions.Scope ?? "";
            parameters.AddRange(["-Scope", scope == PackageScope.Global ? "AllUsers" : "CurrentUser"]);
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
        string output_string = string.Join("\n", processOutput);

        if (
            package.OverridenOptions.RunAsAdministrator is not true
            && (
                output_string.Contains("AdminPrivilegesAreRequired")
                || output_string.Contains("AdminPrivilegeRequired")
            )
        )
        {
            package.OverridenOptions.RunAsAdministrator = true;
            return OperationVeredict.AutoRetry;
        }

        return returnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;
    }
}
