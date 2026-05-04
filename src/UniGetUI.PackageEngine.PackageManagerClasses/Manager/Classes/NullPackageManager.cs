using UniGetUI.Core.IconEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Classes.Manager
{
    public class NullPackageManager : IPackageManager
    {
        public static NullPackageManager Instance = new();
        public ManagerProperties Properties { get; }
        public ManagerCapabilities Capabilities { get; }
        public ManagerStatus Status { get; }
        public string Id
        {
            get => string.IsNullOrWhiteSpace(Properties.Id) ? Properties.Name : Properties.Id;
        }
        public string Name
        {
            get => Properties.Name;
        }
        public string DisplayName
        {
            get => Properties.DisplayName ?? Properties.Name;
        }
        public IManagerSource DefaultSource
        {
            get => Properties.DefaultSource;
        }
        public bool ManagerReady
        {
            get => true;
        }
        public IManagerLogger TaskLogger { get; }
        public IMultiSourceHelper SourcesHelper { get; }
        public IPackageDetailsHelper DetailsHelper { get; }
        public IPackageOperationHelper OperationHelper { get; }
        public IReadOnlyList<ManagerDependency> Dependencies { get; }

        public NullPackageManager()
        {
            TaskLogger = new ManagerLogger(this);
            var nullsource = NullSource.Instance;
            SourcesHelper = new NullSourceHelper();
            DetailsHelper = new NullPkgDetailsHelper();
            OperationHelper = new NullPkgOperationHelper();
            Properties = new ManagerProperties
            {
                IsDummy = true,
                Id = "unknown",
                Name = CoreTools.Translate("Unknown"),
                Description = "Unset",
                IconId = IconType.Help,
                ColorIconId = "Unset",
                ExecutableFriendlyName = "Unset",
                InstallVerb = "Unset",
                UpdateVerb = "Unset",
                UninstallVerb = "Unset",
                KnownSources = [nullsource],
                DefaultSource = nullsource,
            };
            Capabilities = new ManagerCapabilities();
            Status = new ManagerStatus
            {
                ExecutablePath = "C:/file.exe",
                ExecutableCallArgs = "Unset",
                Found = false,
                Version = "0",
            };
            Dependencies = [];
        }

        public IReadOnlyList<IPackage> FindPackages(string query) =>
            throw new NotImplementedException();

        public IReadOnlyList<IPackage> GetAvailableUpdates() => throw new NotImplementedException();

        public IReadOnlyList<IPackage> GetInstalledPackages() =>
            throw new NotImplementedException();

        public void Initialize() => throw new NotImplementedException();

        public bool IsEnabled() => throw new NotImplementedException();

        public bool IsReady() => throw new NotImplementedException();

        public void RefreshPackageIndexes() => throw new NotImplementedException();

        public void AttemptFastRepair() => throw new NotImplementedException();

        public IReadOnlyList<string> FindCandidateExecutableFiles() =>
            throw new NotImplementedException();

        public Tuple<bool, string> GetExecutableFile() => throw new NotImplementedException();
    }

    internal sealed class NullSourceHelper : IMultiSourceHelper
    {
        public ISourceFactory Factory => throw new NotImplementedException();

        public string[] GetAddSourceParameters(IManagerSource source) =>
            throw new NotImplementedException();

        public string[] GetRemoveSourceParameters(IManagerSource source) =>
            throw new NotImplementedException();

        public OperationVeredict GetAddOperationVeredict(
            IManagerSource source,
            int ReturnCode,
            string[] Output
        ) => throw new NotImplementedException();

        public OperationVeredict GetRemoveOperationVeredict(
            IManagerSource source,
            int ReturnCode,
            string[] Output
        ) => throw new NotImplementedException();

        public IReadOnlyList<IManagerSource> GetSources() => throw new NotImplementedException();
    }

    internal sealed class NullPkgDetailsHelper : IPackageDetailsHelper
    {
        public void GetDetails(IPackageDetails details) => throw new NotImplementedException();

        public IReadOnlyList<string> GetVersions(IPackage package) =>
            throw new NotImplementedException();

        public CacheableIcon? GetIcon(IPackage package) => throw new NotImplementedException();

        public IReadOnlyList<Uri> GetScreenshots(IPackage package) =>
            throw new NotImplementedException();

        public string? GetInstallLocation(IPackage package) => throw new NotImplementedException();
    }

    internal sealed class NullPkgOperationHelper : IPackageOperationHelper
    {
        public IReadOnlyList<string> GetParameters(
            IPackage package,
            InstallOptions options,
            OperationType operation
        ) => throw new NotImplementedException();

        public OperationVeredict GetResult(
            IPackage package,
            OperationType operation,
            IReadOnlyList<string> processOutput,
            int returnCode
        ) => throw new NotImplementedException();
    }
}
