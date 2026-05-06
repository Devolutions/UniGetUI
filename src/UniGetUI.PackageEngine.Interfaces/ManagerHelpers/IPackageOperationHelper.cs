using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Interfaces.ManagerProviders
{
    /// <summary>
    /// Handled the process of installing and uninstalling packages
    /// </summary>
    public interface IPackageOperationHelper
    {
        /// <summary>
        /// Returns the list of arguments that need to be passed to the Package Manager executable so
        /// that the requested operation is performed over the given package, with its corresponding
        /// installation options.
        /// </summary>
        public IReadOnlyList<string> GetParameters(
            IPackage package,
            InstallOptions options,
            OperationType operation
        );

        /// <summary>
        /// Returns the veredict of the given package operation, given the package, the operation type,
        /// the corresponding output and the return code.
        /// </summary>
        public OperationVeredict GetResult(
            IPackage package,
            OperationType operation,
            IReadOnlyList<string> processOutput,
            int returnCode
        );
    }
}
