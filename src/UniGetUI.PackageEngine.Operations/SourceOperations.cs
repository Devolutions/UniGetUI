using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageOperations;
#if WINDOWS
using UniGetUI.PackageEngine.Managers.WingetManager;
#endif

namespace UniGetUI.PackageEngine.Operations
{
    public abstract class SourceOperation : AbstractProcessOperation
    {
        protected abstract void Initialize();

        protected IManagerSource Source;
        public bool ForceAsAdministrator { get; private set; }

        public SourceOperation(IManagerSource source, IReadOnlyList<InnerOperation>? preOps)
            : base(false, preOps)
        {
            Source = source;
            Initialize();
        }

        public override Task<Uri> GetOperationIcon()
        {
            return Task.FromResult(
                new Uri($"ms-appx:///Assets/Images/{Source.Manager.Properties.ColorIconId}.png")
            );
        }

        protected bool RequiresAdminRights() =>
            !Settings.Get(Settings.K.ProhibitElevation)
            && (ForceAsAdministrator || Source.Manager.Capabilities.Sources.MustBeInstalledAsAdmin);

        protected override void ApplyRetryAction(string retryMode)
        {
            switch (retryMode)
            {
                case RetryMode.Retry:
                    break;
                case RetryMode.Retry_AsAdmin:
                    ForceAsAdministrator = true;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Retry mode {retryMode} is not supported in this context"
                    );
            }
        }

        public static IReadOnlyList<InnerOperation> CreateInstallPreOps(
            IManagerSource source,
            bool forceLocalWinGet
        )
        {
            if (!IsWinGetManager(source.Manager))
                return [];
            if (forceLocalWinGet)
                return [];
            if (source.Manager.Status.ExecutablePath == GetBundledWinGetPath())
                return [];
            return [new(new AddSourceOperation(source, true), mustSucceed: true)];
        }

        public static IReadOnlyList<InnerOperation> CreateUninstallPreOps(
            IManagerSource source,
            bool forceLocalWinGet
        )
        {
            if (!IsWinGetManager(source.Manager))
                return [];
            if (forceLocalWinGet)
                return [];
            if (source.Manager.Status.ExecutablePath == GetBundledWinGetPath())
                return [];
            // In this case, must succeed is set to false since we cannot ensure that bundled WinGet will
            // have this source added
            return [new(new RemoveSourceOperation(source, true), mustSucceed: false)];
        }

        protected static bool IsWinGetManager(IPackageManager manager)
        {
#if WINDOWS
            return manager is WinGet;
#else
            return false;
#endif
        }

        protected static string? GetBundledWinGetPath()
        {
#if WINDOWS
            return WinGet.BundledWinGetPath;
#else
            return null;
#endif
        }
    }

    public class AddSourceOperation : SourceOperation
    {
        private readonly bool _forceLocalWinGet;

        public AddSourceOperation(IManagerSource source, bool forceLocalWinGet = false)
            : base(source, CreateInstallPreOps(source, forceLocalWinGet))
        {
            _forceLocalWinGet = forceLocalWinGet;
        }

        protected override void PrepareProcessStartInfo()
        {
            var exePath = Source.Manager.Status.ExecutablePath;
            if (IsWinGetManager(Source.Manager) && _forceLocalWinGet)
            {
                exePath = GetBundledWinGetPath() ?? exePath;
            }

            bool admin = false;
            if (RequiresAdminRights())
            {
                if (
                    OperatingSystem.IsLinux()
                    || Settings.Get(Settings.K.DoCacheAdminRights)
                    || Settings.Get(Settings.K.DoCacheAdminRightsForBatches)
                )
                    RequestCachingOfUACPrompt();

                if (IsWinGetManager(Source.Manager))
                    RedirectWinGetTempFolder();

                admin = true;
                process.StartInfo.FileName = CoreData.ElevatorPath;
                process.StartInfo.Arguments =
                    ($"{CoreData.ElevatorArgs} \"{exePath}\" "
                    + Source.Manager.Status.ExecutableCallArgs
                    + " "
                    + string.Join(" ", Source.Manager.SourcesHelper.GetAddSourceParameters(Source))).TrimStart();
            }
            else
            {
                process.StartInfo.FileName = exePath;
                process.StartInfo.Arguments =
                    Source.Manager.Status.ExecutableCallArgs
                    + " "
                    + string.Join(" ", Source.Manager.SourcesHelper.GetAddSourceParameters(Source));
            }

            ApplyCapabilities(admin, false, false, null);
        }

        protected override Task<OperationVeredict> GetProcessVeredict(
            int ReturnCode,
            List<string> Output
        )
        {
            return Task.Run(() =>
                Source.Manager.SourcesHelper.GetAddOperationVeredict(
                    Source,
                    ReturnCode,
                    Output.ToArray()
                )
            );
        }

        protected override void Initialize()
        {
            Metadata.OperationInformation =
                "Starting adding source operation for source="
                + Source.Name
                + "with Manager="
                + Source.Manager.Name;

            Metadata.Title = CoreTools.Translate(
                "Adding source {source}",
                new Dictionary<string, object?> { { "source", Source.Name } }
            );
            Metadata.Status = CoreTools.Translate(
                "Adding source {source} to {manager}",
                new Dictionary<string, object?>
                {
                    { "source", Source.Name },
                    { "manager", Source.Manager.Name },
                }
            );
            ;
            Metadata.SuccessTitle = CoreTools.Translate("Source added successfully");
            Metadata.SuccessMessage = CoreTools.Translate(
                "The source {source} was added to {manager} successfully",
                new Dictionary<string, object?>
                {
                    { "source", Source.Name },
                    { "manager", Source.Manager.Name },
                }
            );
            Metadata.FailureTitle = CoreTools.Translate("Could not add source");
            Metadata.FailureMessage = CoreTools.Translate(
                "Could not add source {source} to {manager}",
                new Dictionary<string, object?>
                {
                    { "source", Source.Name },
                    { "manager", Source.Manager.Name },
                }
            );
        }
    }

    public class RemoveSourceOperation : SourceOperation
    {
        private readonly bool _forceLocalWinGet;

        public RemoveSourceOperation(IManagerSource source, bool forceLocalWinGet = false)
            : base(source, CreateUninstallPreOps(source, forceLocalWinGet))
        {
            _forceLocalWinGet = forceLocalWinGet;
        }

        protected override void PrepareProcessStartInfo()
        {
            var exePath = Source.Manager.Status.ExecutablePath;
            if (IsWinGetManager(Source.Manager) && _forceLocalWinGet)
            {
                exePath = GetBundledWinGetPath() ?? exePath;
            }
            bool admin = false;
            if (RequiresAdminRights())
            {
                if (
                    OperatingSystem.IsLinux()
                    || Settings.Get(Settings.K.DoCacheAdminRights)
                    || Settings.Get(Settings.K.DoCacheAdminRightsForBatches)
                )
                    RequestCachingOfUACPrompt();

                if (IsWinGetManager(Source.Manager))
                    RedirectWinGetTempFolder();

                admin = true;
                process.StartInfo.FileName = CoreData.ElevatorPath;
                process.StartInfo.Arguments =
                    ($"{CoreData.ElevatorArgs} \"{exePath}\" "
                    + Source.Manager.Status.ExecutableCallArgs
                    + " "
                    + string.Join(
                        " ",
                        Source.Manager.SourcesHelper.GetRemoveSourceParameters(Source)
                    )).TrimStart();
            }
            else
            {
                process.StartInfo.FileName = exePath;
                process.StartInfo.Arguments =
                    Source.Manager.Status.ExecutableCallArgs
                    + " "
                    + string.Join(
                        " ",
                        Source.Manager.SourcesHelper.GetRemoveSourceParameters(Source)
                    );
            }
            ApplyCapabilities(admin, false, false, null);
        }

        protected override Task<OperationVeredict> GetProcessVeredict(
            int ReturnCode,
            List<string> Output
        )
        {
            return Task.Run(() =>
                Source.Manager.SourcesHelper.GetRemoveOperationVeredict(
                    Source,
                    ReturnCode,
                    Output.ToArray()
                )
            );
        }

        protected override void Initialize()
        {
            Metadata.OperationInformation =
                "Starting remove source operation for source="
                + Source.Name
                + "with Manager="
                + Source.Manager.Name;

            Metadata.Title = CoreTools.Translate(
                "Removing source {source}",
                new Dictionary<string, object?> { { "source", Source.Name } }
            );
            Metadata.Status = CoreTools.Translate(
                "Removing source {source} from {manager}",
                new Dictionary<string, object?>
                {
                    { "source", Source.Name },
                    { "manager", Source.Manager.Name },
                }
            );
            ;
            Metadata.SuccessTitle = CoreTools.Translate("Source removed successfully");
            Metadata.SuccessMessage = CoreTools.Translate(
                "The source {source} was removed from {manager} successfully",
                new Dictionary<string, object?>
                {
                    { "source", Source.Name },
                    { "manager", Source.Manager.Name },
                }
            );
            Metadata.FailureTitle = CoreTools.Translate("Could not remove source");
            Metadata.FailureMessage = CoreTools.Translate(
                "Could not remove source {source} from {manager}",
                new Dictionary<string, object?>
                {
                    { "source", Source.Name },
                    { "manager", Source.Manager.Name },
                }
            );
        }
    }
}
