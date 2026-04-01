using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.PipManager;

internal sealed class PipPkgOperationHelper : BasePkgOperationHelper
{
    public PipPkgOperationHelper(Pip manager)
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
        parameters.AddRange([
            options.Version.Any() ? $"{package.Id}=={options.Version}" : package.Id,
            "--no-input",
            "--no-color",
            "--no-cache",
        ]);

        if (operation is OperationType.Uninstall)
        {
            parameters.Add("--yes");
        }
        else
        {
            if (options.PreRelease)
                parameters.Add("--pre");

            if (
                package.OverridenOptions.Scope == PackageScope.User
                || (
                    package.OverridenOptions.Scope is null
                    && options.InstallationScope == PackageScope.User
                )
            )
                parameters.Add("--user");
        }

        if (package.OverridenOptions.Pip_BreakSystemPackages)
            parameters.Add("--break-system-packages");

        parameters.Add(Pip.GetProxyArgument());

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
            return OperationVeredict.Success;
        }

        string output_string = string.Join("\n", processOutput);

        if (output_string.Contains("externally-managed-environment") && !package.OverridenOptions.Pip_BreakSystemPackages)
        {
            package.OverridenOptions.Pip_BreakSystemPackages = true;
            return OperationVeredict.AutoRetry;
        }

        if (output_string.Contains("--user") && package.OverridenOptions.Scope != PackageScope.User)
        {
            package.OverridenOptions.Scope = PackageScope.User;
            return OperationVeredict.AutoRetry;
        }
        return OperationVeredict.Failure;
    }
}
