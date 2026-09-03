using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageOperations;

namespace UniGetUI.PackageEngine.Operations
{
    public abstract class SourceOperation : AbstractProcessOperation
    {
        protected abstract void Initialize();

        protected IManagerSource Source;
        public IManagerSource ManagerSource => Source;
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

        protected void RequireShellSafeSource()
        {
            string url = Source.Url?.ToString() ?? "";

            if (!CoreTools.IsCommandLineInertValue(Source.Name))
                throw new InvalidOperationException(
                    $"Refusing to build a {Source.Manager.Name} command line for the source name \"{Source.Name}\": it contains characters that would alter the command line."
                );

            if (url.Length > 0 && !CoreTools.IsCommandLineInertValue(url))
                throw new InvalidOperationException(
                    $"Refusing to build a {Source.Manager.Name} command line for the source URL \"{url}\": it contains characters that would alter the command line."
                );
        }

        protected void PrepareSourceProcessStartInfo(IReadOnlyList<string> sourceParameters)
        {
            RequireShellSafeSource();

            var exePath = Source.Manager.Status.ExecutablePath;
            var callVector = Source.Manager.Status.OperationCallArgs;
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
                if (callVector.Count > 0)
                {
                    SetArgumentVector(
                        [.. ElevatorArgumentPrefix(), exePath, .. callVector, .. sourceParameters]
                    );
                }
                else
                {
                    process.StartInfo.Arguments =
                        ($"{CoreData.ElevatorArgs} \"{exePath}\" "
                        + Source.Manager.Status.ExecutableCallArgs
                        + " "
                        + string.Join(" ", sourceParameters)).TrimStart();
                }
            }
            else
            {
                process.StartInfo.FileName = exePath;
                if (callVector.Count > 0)
                {
                    SetArgumentVector([.. callVector, .. sourceParameters]);
                }
                else
                {
                    process.StartInfo.Arguments =
                        Source.Manager.Status.ExecutableCallArgs
                        + " "
                        + string.Join(" ", sourceParameters);
                }
            }

            ApplyCapabilities(admin, false, false, null);
        }

        protected static bool IsWinGetManager(IPackageManager manager)
        {
#if WINDOWS
            return manager.Name == "Winget";
#else
            return false;
#endif
        }
    }

    public class AddSourceOperation : SourceOperation
    {
        public AddSourceOperation(IManagerSource source)
            : base(source, []) { }

        protected override void PrepareProcessStartInfo()
        {
            PrepareSourceProcessStartInfo(
                Source.Manager.SourcesHelper.GetAddSourceParameters(Source)
            );
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
        public RemoveSourceOperation(IManagerSource source)
            : base(source, []) { }

        protected override void PrepareProcessStartInfo()
        {
            PrepareSourceProcessStartInfo(
                Source.Manager.SourcesHelper.GetRemoveSourceParameters(Source)
            );
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
