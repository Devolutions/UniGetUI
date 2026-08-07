using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Classes.Manager.BaseProviders;

public abstract class BasePkgOperationHelper : IPackageOperationHelper
{
    protected IPackageManager Manager;

    public BasePkgOperationHelper(IPackageManager manager)
    {
        Manager = manager;
    }

    protected abstract IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation
    );

    protected abstract OperationVeredict _getOperationResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode
    );

    public IReadOnlyList<string> GetParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation
    )
    {
        var parameters = _getOperationParameters(package, options, operation);
        Logger.Info(
            $"Loaded operation parameters for package id={package.Id} on manager {Manager.Name} and operation {operation}: "
                + string.Join(' ', parameters)
        );
        return parameters.Where(x => x.Any()).ToArray();
    }

    public OperationVeredict GetResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode
    )
    {
        if (
            returnCode is 999
            && (
                !processOutput.Any()
                || processOutput[processOutput.Count - 1]
                    == "Error: The operation was canceled by the user."
            )
        )
        {
            Logger.Warn(
                "Elevator [or GSudo] UAC prompt was canceled, not showing error message..."
            );
            return OperationVeredict.Canceled;
        }

        return _getOperationResult(package, operation, processOutput, returnCode);
    }

    /// <summary>
    /// Default implementation: no manager-specific elevation requirements. Managers that
    /// can detect that a package needs (or prohibits) elevation override this to update
    /// <c>package.OverridenOptions.RunAsAdministrator</c> accordingly.
    /// </summary>
    public virtual void ApplyElevationRequirements(
        IPackage package,
        InstallOptions options,
        OperationType operation
    )
    { }
}
