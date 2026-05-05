using System.Text;
using UniGetUI.Core.Classes;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.AgentBroker;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageOperations;
using BrokerClient = Devolutions.Now.Policy.Client.BrokerClient;
using BrokerClientErrorKind = Devolutions.Now.Policy.Client.BrokerClientErrorKind;
using BrokerClientException = Devolutions.Now.Policy.Client.BrokerClientException;
using BrokerClientOptions = Devolutions.Now.Policy.Client.BrokerClientOptions;
using BrokerElevation = Devolutions.Now.Policy.Api.Elevation;
using BrokerOperationStatus = Devolutions.Now.Policy.Api.OperationStatus;
#if WINDOWS
using UniGetUI.PackageEngine.Managers.WingetManager;
#endif

namespace UniGetUI.PackageEngine.Operations
{
    public abstract class PackageOperation : AbstractProcessOperation
    {
        protected List<string> DesktopShortcutsBeforeStart = [];

        public readonly IPackage Package;
        public readonly InstallOptions Options;
        public readonly OperationType Role;

        protected abstract Task HandleSuccess();
        protected abstract Task HandleFailure();
        protected abstract void Initialize();

        public PackageOperation(
            IPackage package,
            InstallOptions options,
            OperationType role,
            bool IgnoreParallelInstalls = false,
            AbstractOperation? req = null
        )
            : base(
                !IgnoreParallelInstalls,
                _getPreInstallOps(package, options, role, req),
                _getPostInstallOps(package, options, role)
            )
        {
            Package = package;
            Options = options;
            Role = role;

            Initialize();

            Enqueued += (_, _) =>
            {
                ApplyCapabilities(
                    RequiresAdminRights(),
                    Options.InteractiveInstallation,
                    (Options.SkipHashCheck && Role is not OperationType.Uninstall),
                    Package.OverridenOptions.Scope ?? Options.InstallationScope
                );

                Package.SetTag(PackageTag.OnQueue);
            };
            CancelRequested += (_, _) => Package.SetTag(PackageTag.Default);
            OperationSucceeded += (_, _) => HandleSuccess();
            OperationFailed += (_, _) => HandleFailure();
        }

        private bool RequiresAdminRights() =>
            !Settings.Get(Settings.K.ProhibitElevation)
            && (Package.OverridenOptions.RunAsAdministrator is true || Options.RunAsAdministrator);

        protected override void ApplyRetryAction(string retryMode)
        {
            switch (retryMode)
            {
                case RetryMode.Retry_AsAdmin:
                    Options.RunAsAdministrator = true;
                    break;
                case RetryMode.Retry_Interactive:
                    Options.InteractiveInstallation = true;
                    break;
                case RetryMode.Retry_SkipIntegrity:
                    Options.SkipHashCheck = true;
                    break;
                case RetryMode.Retry:
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Retry mode {retryMode} is not supported in this context"
                    );
            }
            Metadata.OperationInformation =
                "Retried package operation for Package="
                + Package.Id
                + " with Manager="
                + Package.Manager.Name
                + "\nUpdated installation options: "
                + Options.ToString()
                + "\nOverriden options: "
                + Package.OverridenOptions.ToString();
        }

        protected sealed override void PrepareProcessStartInfo()
        {
            bool IsAdmin = CoreTools.IsAdministrator();
            Package.SetTag(PackageTag.OnQueue);
            string operation_args = string.Join(
                " ",
                Package.Manager.OperationHelper.GetParameters(Package, Options, Role)
            );
            string FileName,
                Arguments;

            if (RequiresAdminRights() && IsAdmin is false)
            {
                IsAdmin = true;
                if (
                    OperatingSystem.IsLinux()
                    || Settings.Get(Settings.K.DoCacheAdminRights)
                    || Settings.Get(Settings.K.DoCacheAdminRightsForBatches)
                )
                {
                    RequestCachingOfUACPrompt();
                }

                FileName = CoreData.ElevatorPath;
                Arguments =
                    $"{CoreData.ElevatorArgs} \"{Package.Manager.Status.ExecutablePath}\" {Package.Manager.Status.ExecutableCallArgs} {operation_args}".TrimStart();
            }
            else
            {
                FileName = Package.Manager.Status.ExecutablePath;
                Arguments = $"{Package.Manager.Status.ExecutableCallArgs} {operation_args}";
            }

            if (IsAdmin && IsWinGetManager(Package.Manager))
            {
                RedirectWinGetTempFolder();
            }

            process.StartInfo.FileName = FileName;
            process.StartInfo.Arguments = Arguments;
            process.StartInfo.StandardOutputEncoding = Package.Manager.OutputEncoding;
            process.StartInfo.StandardErrorEncoding = Package.Manager.OutputEncoding;

            ApplyCapabilities(
                IsAdmin,
                Options.InteractiveInstallation,
                (Options.SkipHashCheck && Role is not OperationType.Uninstall),
                Package.OverridenOptions.Scope ?? Options.InstallationScope
            );
        }

        /// <summary>
        /// Override to intercept operations and route through the Devolutions Agent broker
        /// when the UseAgentBroker setting is enabled and the manager supports it (WinGet only for now).
        /// Falls back to process-based execution otherwise.
        /// </summary>
        protected override async Task<OperationVeredict> PerformOperation()
        {
            if (!ShouldUseAgentBroker())
            {
                return await base.PerformOperation();
            }

            return await PerformBrokerOperation();
        }

        /// <summary>
        /// Determines whether this operation should be routed through the agent broker.
        /// </summary>
        private bool ShouldUseAgentBroker()
        {
            // NOTE: Change this condition to enable agent broker by default when ready.
            // Currently opt-in via settings.
            bool eligible = IsBrokerEligible(Package);
            Logger.Info($"[AgentBroker] ShouldUseAgentBroker check: eligible={eligible}, manager={Package.Manager.Name}, virtualSource={Package.Source.IsVirtualManager}");
            return eligible;
        }

        /// <summary>
        /// Whether a package operation is eligible for broker routing. Only WinGet is
        /// supported in this iteration, and virtual/local sources are excluded: the agent
        /// command builder always emits --source from the request, while the local WinGet
        /// path deliberately omits it for virtual sources (e.g. the Local PC source).
        /// </summary>
        private static bool IsBrokerEligible(IPackage package) =>
            Settings.Get(Settings.K.UseAgentBroker)
            && IsWinGetManager(package.Manager)
            && !package.Source.IsVirtualManager;

        /// <summary>
        /// Perform the package operation through the Devolutions Agent broker.
        /// Sends the request over named pipe and interprets the response.
        /// </summary>
        private async Task<OperationVeredict> PerformBrokerOperation()
        {
            Line("Routing operation through Devolutions Agent broker...", LineType.Information);

            using var client = CreateBrokerClient(RequiresAdminRights());

            // Check broker availability.
            if (!await client.IsAvailable(CancellationToken))
            {
                Line("Agent broker is not available, falling back to local execution.", LineType.Information);
                Line("Note: kill/pre/post operation actions were delegated to the broker and will not run for this fallback execution.", LineType.Information);
                Logger.Warn("[AgentBroker] Broker not available, falling back to process execution");
                return await base.PerformOperation();
            }

            // Resolve the install location the same way the local WinGet path does, so the
            // portable-install safeguard (registry-detected location) is not bypassed.
            string? effectiveInstallLocation = GetBrokerEffectiveInstallLocation();

            // Build the broker request.
            var request = BrokerRequestBuilder.Build(Package, Options, Role, effectiveInstallLocation);

            Line($"Sending request to broker: {request.RequestId}", LineType.VerboseDetails);
            Line($"  Package: {request.Package.Id} ({request.Operation})", LineType.VerboseDetails);
            Line($"  Manager: {request.Manager}", LineType.VerboseDetails);
            Line($"  User: {GetEffectiveUser()}", LineType.VerboseDetails);

            try
            {
                // Send to broker and poll until completion, honoring operation cancellation.
                var status = await client.ExecuteAndWait(request, CancellationToken);

                // Log status details.
                Line($"Broker status: {status.Status}, exitCode={status.ExitCode}", LineType.Information);
                if (!string.IsNullOrWhiteSpace(status.Message))
                {
                    Line($"  Message: {status.Message}", LineType.Information);
                }
                var output = DisplayBrokerOutput(status.Stdout);

                if (status.Status == BrokerOperationStatus.Completed)
                {
                    var veredict = await GetProcessVeredict(status.ExitCode ?? -1, output);
                    if (veredict is OperationVeredict.Success)
                    {
                        Line("Operation completed successfully via agent broker.", LineType.Information);
                    }
                    else if (!string.IsNullOrWhiteSpace(status.Message))
                    {
                        Metadata.FailureMessage = status.Message;
                    }

                    return veredict;
                }

                // Operation failed — surface a user-visible error.
                string reason = status.Message ?? $"Exit code: {status.ExitCode}";
                Line($"Operation failed via broker: {reason}", LineType.Error);
                Metadata.FailureTitle = CoreTools.Translate("Operation denied or failed via broker");
                Metadata.FailureMessage = reason;
                return OperationVeredict.Failure;
            }
            catch (OperationCanceledException)
            {
                Line("Broker operation was canceled.", LineType.Error);
                return OperationVeredict.Canceled;
            }
            catch (BrokerClientException ex)
            {
                Line($"Broker operation failed: {ex.Message}", LineType.Error);
                Logger.Error($"[AgentBroker] Broker operation failed: {ex}");
                Metadata.FailureTitle = CoreTools.Translate(GetBrokerFailureTitle(ex.Kind));
                Metadata.FailureMessage = ex.Message;
                return OperationVeredict.Failure;
            }
        }

        private List<string> DisplayBrokerOutput(string? encodedStdout)
        {
            List<string> output = [];
            if (string.IsNullOrWhiteSpace(encodedStdout))
            {
                return output;
            }

            string decoded;
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encodedStdout));
            }
            catch (FormatException ex)
            {
                Logger.Error($"[AgentBroker] Broker returned invalid base64 stdout: {ex}");
                Line("Broker returned captured output in an invalid format.", LineType.Error);
                return output;
            }

            foreach (var line in decoded.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                output.Add(line);
                Line(line, LineType.Information);
            }

            return output;
        }

        private static BrokerClient CreateBrokerClient(bool requestedElevation) =>
            new(
                new BrokerClientOptions
                {
                    RequestedElevation = requestedElevation
                        ? BrokerElevation.Elevated
                        : BrokerElevation.Standard,
                    EffectiveUser = GetEffectiveUser(),
                    ClientExecutablePath = Environment.ProcessPath,
                    ClientVersion =
                        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                        ?? "0.0.0",
                }
            )
            {
                Trace = message => Logger.Info($"[AgentBroker] {message}"),
            };

        private static string GetEffectiveUser()
        {
            if (string.IsNullOrWhiteSpace(Environment.UserDomainName))
            {
                return Environment.UserName;
            }

            return $"{Environment.UserDomainName}\\{Environment.UserName}";
        }

        private static string GetBrokerFailureTitle(BrokerClientErrorKind kind) =>
            kind switch
            {
                BrokerClientErrorKind.PolicyDenied => "Operation denied by policy",
                BrokerClientErrorKind.UnsupportedCapability => "Operation unsupported by broker",
                BrokerClientErrorKind.BrokerUnavailable or BrokerClientErrorKind.Timeout => "Broker communication error",
                _ => "Operation failed via broker",
            };

        protected sealed override Task<OperationVeredict> GetProcessVeredict(
            int ReturnCode,
            List<string> Output
        )
        {
            return Task.FromResult(
                Package.Manager.OperationHelper.GetResult(Package, Role, Output, ReturnCode)
            );
        }

        private static bool IsWinGetManager(IPackageManager manager)
        {
#if WINDOWS
            return manager is WinGet;
#else
            return false;
#endif
        }

        /// <summary>
        /// Resolves the install location to send in a broker request, matching the local
        /// execution path: for updates this uses the WinGet portable-install safeguard
        /// (registry-detected location, saved value only under WinGetForceLocationOnUpdate);
        /// for installs the configured custom location; for uninstalls nothing.
        /// </summary>
        private string? GetBrokerEffectiveInstallLocation()
        {
            switch (Role)
            {
                case OperationType.Update:
#if WINDOWS
                    return WinGetPkgOperationHelper.GetEffectiveUpdateLocation(Package, Options);
#else
                    return null;
#endif
                case OperationType.Install:
                    return string.IsNullOrWhiteSpace(Options.CustomInstallLocation)
                        ? null
                        : Options.CustomInstallLocation;
                default:
                    return null;
            }
        }

        protected async Task<IPackage> ResolveInstalledPackageSnapshotAsync(
            string fallbackVersion,
            bool preferFallbackVersionWhenMissing = false
        )
        {
            try
            {
                var installedMatches = await Task.Run(() =>
                    Package
                        .Manager.GetInstalledPackages()
                        .Where(candidate => candidate.IsEquivalentTo(Package))
                        .ToArray()
                );

                if (installedMatches.Length > 0)
                {
                    if (!string.IsNullOrWhiteSpace(fallbackVersion))
                    {
                        var exactMatch = installedMatches.FirstOrDefault(candidate =>
                            candidate.VersionString.Equals(
                                fallbackVersion,
                                StringComparison.OrdinalIgnoreCase
                            )
                        );
                        if (exactMatch is not null)
                        {
                            return exactMatch;
                        }

                        if (preferFallbackVersionWhenMissing)
                        {
                            return CreateSyntheticInstalledPackage(fallbackVersion);
                        }
                    }

                    return installedMatches
                        .OrderByDescending(candidate => candidate.NormalizedVersion)
                        .First();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(
                    $"Could not resolve the installed snapshot for package {Package.Id}; falling back to synthetic state"
                );
                Logger.Warn(ex);
            }

            return CreateSyntheticInstalledPackage(fallbackVersion);
        }

        private IPackage CreateSyntheticInstalledPackage(string version)
        {
            return new Package(
                Package.Name,
                Package.Id,
                version,
                Package.Source,
                Package.Manager,
                Package.OverridenOptions
            );
        }

        public override Task<Uri> GetOperationIcon()
        {
            return TaskRecycler<Uri>.RunOrAttachAsync(Package.GetIconUrl);
        }

        private static IReadOnlyList<InnerOperation> _getPreInstallOps(
            IPackage package,
            InstallOptions opts,
            OperationType role,
            AbstractOperation? preReq = null
        )
        {
            List<InnerOperation> l = new();
            if (preReq is not null)
                l.Add(new(preReq, true));

            // For brokered operations the kill/pre/post actions are owned by the broker:
            // they are carried in the broker request so that policy is evaluated before
            // anything runs, and must not also be executed locally.
            if (IsBrokerEligible(package))
                return l;

            foreach (var process in opts.KillBeforeOperation)
                l.Add(new InnerOperation(new KillProcessOperation(process), mustSucceed: false));

            if (role is OperationType.Install && opts.PreInstallCommand.Any())
                l.Add(
                    new(new PrePostOperation(opts.PreInstallCommand), opts.AbortOnPreInstallFail)
                );
            else if (role is OperationType.Update && opts.PreUpdateCommand.Any())
                l.Add(new(new PrePostOperation(opts.PreUpdateCommand), opts.AbortOnPreUpdateFail));
            else if (role is OperationType.Uninstall && opts.PreUninstallCommand.Any())
                l.Add(
                    new(
                        new PrePostOperation(opts.PreUninstallCommand),
                        opts.AbortOnPreUninstallFail
                    )
                );

            return l;
        }

        private static IReadOnlyList<InnerOperation> _getPostInstallOps(
            IPackage package,
            InstallOptions opts,
            OperationType role
        )
        {
            List<InnerOperation> l = new();

            // See _getPreInstallOps: brokered operations delegate post actions (including
            // uninstall-previous) to the broker via the request options.
            if (IsBrokerEligible(package))
                return l;

            if (role is OperationType.Install && opts.PostInstallCommand.Any())
                l.Add(new(new PrePostOperation(opts.PostInstallCommand), false));
            else if (role is OperationType.Update && opts.PostUpdateCommand.Any())
                l.Add(new(new PrePostOperation(opts.PostUpdateCommand), false));
            else if (role is OperationType.Uninstall && opts.PostUninstallCommand.Any())
                l.Add(new(new PrePostOperation(opts.PostUninstallCommand), false));

            if (role is OperationType.Update && opts.UninstallPreviousVersionsOnUpdate)
            {
                var matches = InstalledPackagesLoader.Instance.Packages.Where(p =>
                    p.IsEquivalentTo(package) && p.NormalizedVersion < package.NormalizedNewVersion
                );
                foreach (var match in matches)
                {
                    Logger.Info(
                        $"Queuing {match} version {match.VersionString} for automatic uninstall after update..."
                    );
                    l.Add(new(new UninstallPackageOperation(match, opts.Copy()), false));
                }
            }

            return l;
        }
    }

    /*
     *
     *
     *
     * PER-OPERATION PACKAGE OPERATIONS
     *
     *
     *
     */
    public class InstallPackageOperation : PackageOperation
    {
        public InstallPackageOperation(
            IPackage package,
            InstallOptions options,
            bool IgnoreParallelInstalls = false,
            AbstractOperation? req = null
        )
            : base(package, options, OperationType.Install, IgnoreParallelInstalls, req) { }

        protected override Task HandleFailure()
        {
            Package.SetTag(PackageTag.Failed);
            return Task.CompletedTask;
        }

        protected override async Task HandleSuccess()
        {
            Package.SetTag(PackageTag.AlreadyInstalled);
            bool explicitVersionRequested = !string.IsNullOrWhiteSpace(Options.Version);
            var installedPackage = await ResolveInstalledPackageSnapshotAsync(
                explicitVersionRequested ? Options.Version : Package.VersionString,
                preferFallbackVersionWhenMissing: explicitVersionRequested
            );
            await InstalledPackagesLoader.Instance.AddForeign(installedPackage);

            if (Settings.Get(Settings.K.AskToDeleteNewDesktopShortcuts))
            {
                DesktopShortcutsDatabase.HandleNewShortcuts(DesktopShortcutsBeforeStart);
            }
        }

        protected override void Initialize()
        {
            Metadata.OperationInformation =
                "Package install operation for Package="
                + Package.Id
                + " with Manager="
                + Package.Manager.Name
                + "\nInstallation options: "
                + Options.ToString()
                + "\nOverriden options: "
                + Package.OverridenOptions.ToString();

            Metadata.Title = CoreTools.Translate(
                "{package} Installation",
                new Dictionary<string, object?> { { "package", Package.Name } }
            );
            Metadata.Status = CoreTools.Translate("{0} is being installed", Package.Name);
            Metadata.SuccessTitle = CoreTools.Translate("Installation succeeded");
            Metadata.SuccessMessage = CoreTools.Translate(
                "{package} was installed successfully",
                new Dictionary<string, object?> { { "package", Package.Name } }
            );
            Metadata.FailureTitle = CoreTools.Translate(
                "Installation failed",
                new Dictionary<string, object?> { { "package", Package.Name } }
            );
            Metadata.FailureMessage = CoreTools.Translate(
                "{package} could not be installed",
                new Dictionary<string, object?> { { "package", Package.Name } }
            );

            if (Settings.Get(Settings.K.AskToDeleteNewDesktopShortcuts))
            {
                DesktopShortcutsBeforeStart = DesktopShortcutsDatabase.GetShortcutsOnDisk();
            }
        }
    }

    public class UpdatePackageOperation : PackageOperation
    {
        public UpdatePackageOperation(
            IPackage package,
            InstallOptions options,
            bool IgnoreParallelInstalls = false,
            AbstractOperation? req = null
        )
            : base(package, options, OperationType.Update, IgnoreParallelInstalls, req) { }

        protected override Task HandleFailure()
        {
            Package.SetTag(PackageTag.Failed);
            return Task.CompletedTask;
        }

        protected override async Task HandleSuccess()
        {
            Package.SetTag(PackageTag.Default);
            Package.GetAvailablePackage()?.SetTag(PackageTag.AlreadyInstalled);

            foreach (var p in Package.GetInstalledPackages())
                p.SetTag(PackageTag.Default);

            UpgradablePackagesLoader.Instance.Remove(Package);
            InstalledPackagesLoader.Instance.Remove(Package);

            bool explicitVersionRequested = !string.IsNullOrWhiteSpace(Options.Version);
            var installedPackage = await ResolveInstalledPackageSnapshotAsync(
                explicitVersionRequested
                    ? Options.Version
                    : string.IsNullOrWhiteSpace(Package.NewVersionString)
                        ? Package.VersionString
                        : Package.NewVersionString,
                preferFallbackVersionWhenMissing: explicitVersionRequested
            );
            await InstalledPackagesLoader.Instance.AddForeign(installedPackage);

            if (Settings.Get(Settings.K.AskToDeleteNewDesktopShortcuts))
            {
                DesktopShortcutsDatabase.HandleNewShortcuts(DesktopShortcutsBeforeStart);
            }

            if (
                await Package.HasUpdatesIgnoredAsync()
                && await Package.GetIgnoredUpdatesVersionAsync() != "*"
            )
                await Package.RemoveFromIgnoredUpdatesAsync();
        }

        protected override void Initialize()
        {
            Metadata.OperationInformation =
                "Package update operation for Package="
                + Package.Id
                + " with Manager="
                + Package.Manager.Name
                + "\nUpdate options: "
                + Options.ToString()
                + "\nOverriden options: "
                + Package.OverridenOptions.ToString()
                + "\nVersion: "
                + Package.VersionString
                + " -> "
                + Package.NewVersionString;

            Metadata.Title = CoreTools.Translate(
                "{package} Update",
                new Dictionary<string, object?> { { "package", Package.Name } }
            );
            Metadata.Status = CoreTools.Translate(
                "{0} is being updated to version {1}",
                Package.Name,
                Package.NewVersionString
            );
            Metadata.SuccessTitle = CoreTools.Translate("Update succeeded");
            Metadata.SuccessMessage = CoreTools.Translate(
                "{package} was updated successfully",
                new Dictionary<string, object?> { { "package", Package.Name } }
            );
            Metadata.FailureTitle = CoreTools.Translate(
                "Update failed",
                new Dictionary<string, object?> { { "package", Package.Name } }
            );
            Metadata.FailureMessage = CoreTools.Translate(
                "{package} could not be updated",
                new Dictionary<string, object?> { { "package", Package.Name } }
            );

            if (Settings.Get(Settings.K.AskToDeleteNewDesktopShortcuts))
            {
                DesktopShortcutsBeforeStart = DesktopShortcutsDatabase.GetShortcutsOnDisk();
            }
        }
    }

    public class UninstallPackageOperation : PackageOperation
    {
        public UninstallPackageOperation(
            IPackage package,
            InstallOptions options,
            bool IgnoreParallelInstalls = false,
            AbstractOperation? req = null
        )
            : base(package, options, OperationType.Uninstall, IgnoreParallelInstalls, req) { }

        protected override Task HandleFailure()
        {
            Package.SetTag(PackageTag.Failed);
            return Task.CompletedTask;
        }

        protected override Task HandleSuccess()
        {
            Package.SetTag(PackageTag.Default);
            Package.GetAvailablePackage()?.SetTag(PackageTag.Default);
            UpgradablePackagesLoader.Instance.Remove(Package);
            InstalledPackagesLoader.Instance.Remove(Package);

            return Task.CompletedTask;
        }

        protected override void Initialize()
        {
            Metadata.OperationInformation =
                "Package uninstall operation for Package="
                + Package.Id
                + " with Manager="
                + Package.Manager.Name
                + "\nUninstall options: "
                + Options.ToString()
                + "\nOverriden options: "
                + Package.OverridenOptions.ToString();

            Metadata.Title = CoreTools.Translate(
                "{package} Uninstall",
                new Dictionary<string, object?> { { "package", Package.Name } }
            );
            Metadata.Status = CoreTools.Translate("{0} is being uninstalled", Package.Name);
            Metadata.SuccessTitle = CoreTools.Translate("Uninstall succeeded");
            Metadata.SuccessMessage = CoreTools.Translate(
                "{package} was uninstalled successfully",
                new Dictionary<string, object?> { { "package", Package.Name } }
            );
            Metadata.FailureTitle = CoreTools.Translate(
                "Uninstall failed",
                new Dictionary<string, object?> { { "package", Package.Name } }
            );
            Metadata.FailureMessage = CoreTools.Translate(
                "{package} could not be uninstalled",
                new Dictionary<string, object?> { { "package", Package.Name } }
            );
        }
    }
}
