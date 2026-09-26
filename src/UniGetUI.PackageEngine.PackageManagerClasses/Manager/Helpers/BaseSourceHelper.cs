using UniGetUI.Core.Classes;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;

namespace UniGetUI.PackageEngine.Classes.Manager.Providers
{
    public abstract class BaseSourceHelper : IMultiSourceHelper
    {
        private const int PackageListingTaskTimeout = 60;

        public ISourceFactory Factory { get; }
        protected IPackageManager Manager;

        public BaseSourceHelper(IPackageManager manager)
            : this(manager, new SourceFactory(manager)) { }

        /// <summary>
        /// For managers that resolve source names with their own rules instead of the default
        /// <see cref="SourceFactory"/>.
        /// </summary>
        protected BaseSourceHelper(IPackageManager manager, ISourceFactory factory)
        {
            Manager = manager;
            Factory = factory;
        }

        /// <summary>
        /// Drops the cached source list, so the next <see cref="GetSources"/> call loads it again.
        /// </summary>
        protected void InvalidateSourcesCache() =>
            TaskRecycler<IReadOnlyList<IManagerSource>>.RemoveFromCache(_getSources);

        public abstract string[] GetAddSourceParameters(IManagerSource source);
        public abstract string[] GetRemoveSourceParameters(IManagerSource source);
        protected abstract OperationVeredict _getAddSourceOperationVeredict(
            IManagerSource source,
            int ReturnCode,
            string[] Output
        );
        protected abstract OperationVeredict _getRemoveSourceOperationVeredict(
            IManagerSource source,
            int ReturnCode,
            string[] Output
        );

        public OperationVeredict GetAddOperationVeredict(
            IManagerSource source,
            int ReturnCode,
            string[] Output
        )
        {
            InvalidateSourcesCache();
            if (
                ReturnCode is 999
                && Output.Last() == "Error: The operation was canceled by the user."
            )
            {
                Logger.Warn(
                    "Elevator [or GSudo] UAC prompt was canceled, not showing error message..."
                );
                return OperationVeredict.Canceled;
            }
            return _getAddSourceOperationVeredict(source, ReturnCode, Output);
        }

        public OperationVeredict GetRemoveOperationVeredict(
            IManagerSource source,
            int ReturnCode,
            string[] Output
        )
        {
            InvalidateSourcesCache();
            if (
                ReturnCode is 999
                && Output.Last() == "Error: The operation was canceled by the user."
            )
            {
                Logger.Warn(
                    "Elevator [or GSudo] UAC prompt was canceled, not showing error message..."
                );
                return OperationVeredict.Canceled;
            }
            return _getRemoveSourceOperationVeredict(source, ReturnCode, Output);
        }

        /// <summary>
        /// Loads the sources for the manager. This method SHOULD NOT handle exceptions
        /// </summary>
        protected abstract IReadOnlyList<IManagerSource> GetSources_UnSafe();

        public virtual IReadOnlyList<IManagerSource> GetSources() =>
            TaskRecycler<IReadOnlyList<IManagerSource>>.RunOrAttach(_getSources, 15);

        public virtual IReadOnlyList<IManagerSource> _getSources()
        {
            try
            {
                var task = Task.Run(GetSources_UnSafe);
                if (!task.Wait(TimeSpan.FromSeconds(PackageListingTaskTimeout)))
                {
                    if (!Settings.Get(Settings.K.DisableTimeoutOnPackageListingTasks))
                    {
                        CoreTools.FinalizeDangerousTask(task);
                        throw new TimeoutException(
                            $"Task _getInstalledPackages for manager {Manager.Name} did not finish after "
                                + $"{PackageListingTaskTimeout} seconds, aborting.  You may disable "
                                + $"timeouts from UniGetUI Advanced Settings"
                        );
                    }

                    task.Wait();
                }

                var sources = task.Result;
                Factory.Reset();

                foreach (IManagerSource source in sources)
                {
                    Factory.AddSource(source);
                }

                Logger.Debug($"Loaded {sources.Count} sources for manager {Manager.Name}");
                return sources;
            }
            catch (Exception e)
            {
                Logger.Error("Error finding sources for manager " + Manager.Name);
                Logger.Error(e);
                return [];
            }
        }
    }
}
