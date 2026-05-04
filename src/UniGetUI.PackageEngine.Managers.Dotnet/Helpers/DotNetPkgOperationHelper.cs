using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;
using Architecture = UniGetUI.PackageEngine.Enums.Architecture;

namespace UniGetUI.PackageEngine.Managers.DotNetManager;

internal sealed class DotNetPkgOperationHelper : BasePkgOperationHelper
{
    public DotNetPkgOperationHelper(DotNet manager)
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
            package.Id,
        ];

        if (options.CustomInstallLocation != "")
            parameters.AddRange(["--tool-path", "\"" + options.CustomInstallLocation + "\""]);

        if (
            package.OverridenOptions.Scope is PackageScope.Global
            || (
                package.OverridenOptions.Scope is null
                && options.InstallationScope is PackageScope.Global
            )
        )
            parameters.Add("--global");

        if (operation is OperationType.Install or OperationType.Update)
        {
            parameters.AddRange(
                options.Architecture switch
                {
                    Architecture.x86 => ["--arch", "x86"],
                    Architecture.x64 => ["--arch", "x64"],
                    Architecture.arm32 => ["--arch", "arm32"],
                    Architecture.arm64 => ["--arch", "arm64"],
                    _ => [],
                }
            );
        }

        if (operation is OperationType.Install or OperationType.Update)
        {
            if (options.Version != "")
            {
                parameters.AddRange(["--version", options.Version]);
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
        if (returnCode is not 0 && package.OverridenOptions.Scope is not PackageScope.Global)
        {
            package.OverridenOptions.Scope = PackageScope.Global;
            return OperationVeredict.AutoRetry;
        }

        return returnCode is 0 ? OperationVeredict.Success : OperationVeredict.Failure;
    }
}
