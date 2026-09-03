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
        /// Like <see cref="GetParameters"/>, but for a command line that has to stand on its own
        /// rather than be launched by UniGetUI: the install-options preview, the manual-install
        /// action and the exported install script. Managers whose normal launch path relies on a
        /// wrapper for part of the operation put that part back into the parameters here.
        /// </summary>
        public IReadOnlyList<string> GetStandaloneParameters(
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

        /// <summary>
        /// Applies manager-specific elevation requirements for the given operation, e.g. by
        /// setting <c>package.OverridenOptions.RunAsAdministrator</c> when the package's
        /// installer is known to require elevation. Called before the operation runs, on
        /// both the local execution path and the agent-broker path, so that elevation is
        /// requested consistently regardless of how the operation is executed.
        /// </summary>
        public void ApplyElevationRequirements(
            IPackage package,
            InstallOptions options,
            OperationType operation
        );
    }
}
