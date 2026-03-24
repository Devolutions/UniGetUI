using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.HomebrewManager;

internal sealed class HomebrewPkgOperationHelper : BasePkgOperationHelper
{
    public HomebrewPkgOperationHelper(Homebrew manager)
        : base(manager) { }

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation)
    {
        List<string> parameters =
        [
            operation switch
            {
                OperationType.Install   => Manager.Properties.InstallVerb,
                OperationType.Update    => Manager.Properties.UpdateVerb,
                OperationType.Uninstall => Manager.Properties.UninstallVerb,
                _ => throw new InvalidDataException("Invalid package operation"),
            },
        ];

        // Casks need the --cask flag
        if (package.Source.Name == "Homebrew Cask")
            parameters.Add("--cask");

        parameters.Add(package.Id);

        // Custom parameters
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
        string output = string.Join("\n", processOutput);

        // Homebrew prints "Error:" for failures
        if (returnCode != 0 || output.Contains("Error:"))
            return OperationVeredict.Failure;

        return OperationVeredict.Success;
    }
}
