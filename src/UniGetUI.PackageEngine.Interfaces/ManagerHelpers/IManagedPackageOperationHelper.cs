using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Interfaces.ManagerProviders;

/// <summary>An optional local execution path; null selects the ordinary manager process.</summary>
public interface IManagedPackageOperationHelper
{
    Task<OperationVeredict?> TryPerformAsync(
        IPackage package,
        InstallOptions options,
        OperationType operation,
        Action<string> log,
        Action<OperationProgress> progress,
        CancellationToken cancellationToken);
}
