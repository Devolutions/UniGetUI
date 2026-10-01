namespace UniGetUI.PackageEngine.Enums
{
    /// <summary>
    /// Represents the installation scope of a package
    /// </summary>
    public static class PackageScope
    {
        public static HashSet<string> ValidValues = [Machine, User];
        public const string Machine = "machine";
        public const string Global = Machine;
        public const string User = "user";
        public const string Local = User;
    }

    public static class Architecture
    {
        public static HashSet<string> ValidValues = [x86, x64, arm32, arm64];
        public const string x86 = "x86";
        public const string x64 = "x64";
        public const string arm32 = "arm32";
        public const string arm64 = "arm64";
    }

    public enum DeserializedPackageStatus
    {
        ManagerNotFound,
        ManagerNotEnabled,
        ManagerNotReady,
        SourceNotFound,
        IsAvailable,
    }

    public enum BundleFormatType
    {
        UBUNDLE,
        JSON,
        YAML,
        XML,
    }

    public enum OperationVeredict
    {
        Success,
        Failure,
        Canceled,
        RestartRequired,
        AutoRetry,
    }

    public enum OperationStatus
    {
        InQueue,
        Running,
        Succeeded,
        Failed,
        Canceled,
    }

    public enum OperationType
    {
        Install,
        Update,
        Uninstall,
        None,
    }

    public enum LoggableTaskType
    {
        /// <summary>
        /// Installs a required dependency for a Package Manager
        /// </summary>
        InstallManagerDependency,

        /// <summary>
        /// Searches for packages with a specific query
        /// </summary>
        FindPackages,

        /// <summary>
        /// Lists all the available updates
        /// </summary>
        ListUpdates,

        /// <summary>
        /// Lists the installed packages
        /// </summary>
        ListInstalledPackages,

        /// <summary>
        /// Refreshes the package indexes
        /// </summary>
        RefreshIndexes,

        /// <summary>
        /// Lists the available sources for the manager
        /// </summary>
        ListSources,

        /// <summary>
        /// Loads the package details for a specific package
        /// </summary>
        LoadPackageDetails,

        /// <summary>
        /// Loads the available versions for a specific package
        /// </summary>
        LoadPackageVersions,

        /// <summary>
        /// Other, specific task
        /// </summary>
        OtherTask,
    }
}
