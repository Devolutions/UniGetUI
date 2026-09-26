using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.PackageEngine.Interfaces.ManagerProviders
{
    /// <summary>
    /// Implemented, alongside <see cref="IMultiSourceHelper"/>, by the sources helper of a package
    /// manager that adds and removes sources inside UniGetUI instead of launching an executable.
    /// Source operations call these methods in place of starting a process.
    /// </summary>
    public interface IInProcessSourceHelper
    {
        /// <summary>
        /// Adds the given source to the manager.
        /// </summary>
        public Task<OperationVeredict> AddSourceAsync(
            IManagerSource source,
            IOperationOutput output,
            CancellationToken cancellationToken
        );

        /// <summary>
        /// Removes the given source from the manager.
        /// </summary>
        public Task<OperationVeredict> RemoveSourceAsync(
            IManagerSource source,
            IOperationOutput output,
            CancellationToken cancellationToken
        );
    }
}
