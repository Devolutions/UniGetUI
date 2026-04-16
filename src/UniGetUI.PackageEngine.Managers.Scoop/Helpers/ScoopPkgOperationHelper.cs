using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;
using Architecture = UniGetUI.PackageEngine.Enums.Architecture;

namespace UniGetUI.PackageEngine.Managers.ScoopManager;

internal sealed class ScoopPkgOperationHelper : BasePkgOperationHelper
{
    public ScoopPkgOperationHelper(Scoop manager)
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

        // If source is ellipsed, a local path, or a URL manifest, omit source argument
        if (package.Source.Name.Contains("...") || package.Source.Name.Contains(":\\") || package.Source.Name.StartsWith("http"))
            parameters.Add($"{package.Id}");
        else
            parameters.Add($"{package.Source.Name}/{package.Id}");

        if (
            package.OverridenOptions.Scope == PackageScope.Global
            || (
                package.OverridenOptions.Scope is null
                && options.InstallationScope == PackageScope.Global
            )
        )
        {
            // Scoop requires admin rights to install global packages
            package.OverridenOptions.RunAsAdministrator = true;
            parameters.Add("--global");
        }

        parameters.AddRange(
            operation switch
            {
                OperationType.Update => options.CustomParameters_Update,
                OperationType.Uninstall => options.CustomParameters_Uninstall,
                _ => options.CustomParameters_Install,
            }
        );

        if (operation is OperationType.Uninstall)
        {
            if (options.RemoveDataOnUninstall)
                parameters.Add("--purge");
        }
        else
        {
            if (options.SkipHashCheck)
                parameters.Add("--skip-hash-check");
        }

        if (operation is OperationType.Install)
        {
            parameters.AddRange(
                options.Architecture switch
                {
                    Architecture.x64 => ["--arch", "64bit"],
                    Architecture.x86 => ["--arch", "32bit"],
                    Architecture.arm64 => ["--arch", "arm64"],
                    _ => [],
                }
            );
        }

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
            package.OverridenOptions.Scope != PackageScope.Global
            && output_string.Contains("Try again with the --global (or -g) flag instead")
        )
        {
            package.OverridenOptions.Scope = PackageScope.Global;
            package.OverridenOptions.RunAsAdministrator = true;
            return OperationVeredict.AutoRetry;
        }

        if (
            package.OverridenOptions.RunAsAdministrator != true
            && (
                output_string.Contains("requires admin rights")
                || output_string.Contains("requires administrator rights")
                || output_string.Contains("you need admin rights to install global apps")
            )
        )
        {
            package.OverridenOptions.RunAsAdministrator = true;
            return OperationVeredict.AutoRetry;
        }

        if (output_string.Contains("ERROR") || returnCode is not 0)
            return OperationVeredict.Failure;

        return OperationVeredict.Success;
    }
}
