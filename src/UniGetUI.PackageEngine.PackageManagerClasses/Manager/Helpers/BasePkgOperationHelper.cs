using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
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

    protected virtual IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation,
        bool standalone
    ) => _getOperationParameters(package, options, operation);

    public IReadOnlyList<string> GetParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation
    ) => BuildParameters(package, options, operation, standalone: false);

    public IReadOnlyList<string> GetStandaloneParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation
    ) => BuildParameters(package, options, operation, standalone: true);

    private IReadOnlyList<string> BuildParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation,
        bool standalone
    )
    {
        if (Manager.CommandLineIsShellInterpreted)
        {
            if (!CoreTools.IsValidPackageIdentifier(package.Id))
                throw new InvalidOperationException(
                    $"Refusing to build a {Manager.Name} command line for the package identifier \"{package.Id}\": it is not a valid package identifier."
                );

            if (options.Version.Length > 0 && !CoreTools.IsValidPackageVersion(options.Version))
                throw new InvalidOperationException(
                    $"Refusing to build a {Manager.Name} command line for package {package.Id}: the requested version \"{options.Version}\" is not a valid package version."
                );
        }

        var parameters = _getOperationParameters(package, options, operation, standalone);
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
