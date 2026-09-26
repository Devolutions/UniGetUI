using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
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

        /// <summary>
        /// The manager's in-process sources helper when it adds and removes sources inside UniGetUI
        /// instead of launching an executable, or null for executable-based managers.
        /// </summary>
        protected IInProcessSourceHelper? InProcessHelper =>
            Source.Manager.SourcesHelper as IInProcessSourceHelper;

        /// <summary>
        /// The command-line parameters that perform this operation with the manager's executable.
        /// </summary>
        protected abstract IReadOnlyList<string> GetSourceParameters();

        /// <summary>
        /// Performs this operation through the manager's in-process sources helper.
        /// </summary>
        protected abstract Task<OperationVeredict> PerformInProcessAsync(
            IInProcessSourceHelper helper,
            IOperationOutput output,
            CancellationToken cancellationToken
        );

        protected sealed override void PrepareProcessStartInfo()
        {
            if (InProcessHelper is not null)
            {
                // The manager performs this operation inside UniGetUI: there is no process to prepare.
                ApplyCapabilities(CoreTools.IsAdministrator(), false, false, null);
                return;
            }

            PrepareSourceProcessStartInfo(GetSourceParameters());
        }

        protected override async Task<OperationVeredict> PerformOperation()
        {
            if (InProcessHelper is not { } helper)
            {
                return await base.PerformOperation();
            }

            Line(
                $"Performing the operation inside UniGetUI with {Source.Manager.DisplayName}",
                LineType.VerboseDetails
            );
            try
            {
                OperationVeredict veredict = await PerformInProcessAsync(
                    helper,
                    new InProcessOperationOutput(Line, Metadata),
                    CancellationToken
                );
                return CancellationToken.IsCancellationRequested
                    ? OperationVeredict.Canceled
                    : veredict;
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                return OperationVeredict.Canceled;
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"{Source.Manager.Name} could not perform the operation on source {Source.Name}"
                );
                Logger.Error(ex);
                Line(ex.Message, LineType.Error);
                return OperationVeredict.Failure;
            }
        }

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

        /// <summary>
        /// Validates the source-derived values this operation places on a command line. The
        /// manager's own flags are not inspected, because several are legitimately written as
        /// options (Register-PSRepository -Default). The URL is only checked when it is actually
        /// passed, so a source whose URL contains query delimiters can still be removed, and
        /// metacharacters are refused only on the concatenated path where a shell would
        /// reinterpret them; an argument vector carries them as data.
        /// </summary>
        protected void RequireSafeSourceParameters(
            IReadOnlyList<string> sourceParameters,
            bool usingArgumentVector
        )
        {
            RequireSafeSourceValue(Source.Name, "source name", usingArgumentVector);

            string url = Source.Url?.ToString() ?? "";
            if (url.Length > 0 && sourceParameters.Contains(url))
                RequireSafeSourceValue(url, "source URL", usingArgumentVector);
        }

        private void RequireSafeSourceValue(
            string value,
            string description,
            bool usingArgumentVector
        )
        {
            if (!CoreTools.IsOptionSafeValue(value))
                throw new InvalidOperationException(
                    $"Refusing to build a {Source.Manager.Name} command line for the {description} \"{value}\": it would be read as a command-line option."
                );

            if (!usingArgumentVector && !CoreTools.IsCommandLineInertValue(value))
                throw new InvalidOperationException(
                    $"Refusing to build a {Source.Manager.Name} command line for the {description} \"{value}\": it contains characters that would alter the command line."
                );
        }
        protected void PrepareSourceProcessStartInfo(IReadOnlyList<string> sourceParameters)
        {
            var exePath = Source.Manager.Status.ExecutablePath;
            var callVector = Source.Manager.Status.OperationCallArgs;
            bool admin = false;

            RequireSafeSourceParameters(sourceParameters, callVector.Count > 0);

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

        protected override IReadOnlyList<string> GetSourceParameters() =>
            Source.Manager.SourcesHelper.GetAddSourceParameters(Source);

        protected override Task<OperationVeredict> PerformInProcessAsync(
            IInProcessSourceHelper helper,
            IOperationOutput output,
            CancellationToken cancellationToken
        ) => helper.AddSourceAsync(Source, output, cancellationToken);

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

        protected override IReadOnlyList<string> GetSourceParameters() =>
            Source.Manager.SourcesHelper.GetRemoveSourceParameters(Source);

        protected override Task<OperationVeredict> PerformInProcessAsync(
            IInProcessSourceHelper helper,
            IOperationOutput output,
            CancellationToken cancellationToken
        ) => helper.RemoveSourceAsync(Source, output, cancellationToken);

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
