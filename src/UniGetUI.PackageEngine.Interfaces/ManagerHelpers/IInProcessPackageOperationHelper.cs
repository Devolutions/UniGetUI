using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Interfaces.ManagerProviders
{
    /// <summary>
    /// Implemented, alongside <see cref="IPackageOperationHelper"/>, by the operation helper of a
    /// package manager that installs, updates and uninstalls packages inside UniGetUI (through a
    /// library) instead of launching an executable. Package operations call
    /// <see cref="PerformAsync"/> in place of starting a process, and never elevate it.
    /// </summary>
    public interface IInProcessPackageOperationHelper
    {
        /// <summary>
        /// Performs the requested operation over the given package, with its installation options.
        /// </summary>
        /// <param name="package">The package to install, update or uninstall</param>
        /// <param name="options">The installation options that apply to the operation</param>
        /// <param name="operation">The operation to perform</param>
        /// <param name="output">Receives the output of the operation</param>
        /// <param name="cancellationToken">Signals that the user canceled the operation</param>
        /// <returns>The veredict of the operation</returns>
        public Task<OperationVeredict> PerformAsync(
            IPackage package,
            InstallOptions options,
            OperationType operation,
            IOperationOutput output,
            CancellationToken cancellationToken
        );
    }
}
