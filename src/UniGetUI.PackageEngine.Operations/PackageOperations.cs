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
using BrokerDecision = Devolutions.Now.Policy.Api.Decision;
using BrokerElevation = Devolutions.Now.Policy.Api.Elevation;
using BrokerEventFrame = Devolutions.Now.Policy.Api.EventFrame;
using BrokerEventFrameException = Devolutions.Now.Policy.Api.EventFrameException;
using BrokerExecutionResponse = Devolutions.Now.Policy.Api.ExecutionResponse;
using BrokerOperationEventChannel = Devolutions.Now.Policy.Client.OperationEventChannel;
using BrokerOperationStatus = Devolutions.Now.Policy.Api.OperationStatus;
using BrokerStatusResponse = Devolutions.Now.Policy.Api.StatusResponse;
using OperationCancelQuery = Devolutions.Now.Policy.Client.OperationCancelQuery;
using OperationStatusQuery = Devolutions.Now.Policy.Client.OperationStatusQuery;
#if WINDOWS
using UniGetUI.PackageEngine.Managers.WingetManager;
#endif

namespace UniGetUI.PackageEngine.Operations
{
    public abstract class PackageOperation : AbstractProcessOperation
    {
        /// <summary>
        /// Raised when an operation that must be routed through the Devolutions Agent broker
        /// cannot proceed because the broker is not available. The payload is a user-facing
        /// error message. The UI layer subscribes to this to show an error message box.
        /// </summary>
        public static event EventHandler<string>? BrokerUnavailable;

        /// <summary>
        /// Test seam: substitutes the transport used to reach the agent broker so tests can
        /// simulate broker outages without a real named pipe. Always null in production.
        /// </summary>
        internal static Func<Devolutions.Now.Policy.Client.IBrokerTransport>? BrokerTransportFactory;

        /// <summary>
        /// Interval between broker operation status polls. Internal so tests can shorten it.
        /// </summary>
        internal static int BrokerStatusPollIntervalMs = 500;

        /// <summary>
        /// Maximum time to wait for the broker to accept a cancel request.
        /// </summary>
        internal static TimeSpan BrokerCancelRequestTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Maximum time to wait for a canceled broker operation to reach a terminal status.
        /// </summary>
        internal static TimeSpan BrokerCancelConfirmTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Upper bound for a brokered operation to reach a terminal status before the
        /// operation is reported as failed. Protects against a broker that keeps
        /// reporting a non-terminal status indefinitely.
        /// </summary>
        internal static TimeSpan BrokerOperationTimeout = TimeSpan.FromHours(1);

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
            StatusChanged += (_, status) =>
            {
                if (status is OperationStatus.Canceled)
                    Package.SetTag(PackageTag.Default);
            };
            OperationSucceeded += (_, _) => HandleSuccess();
            OperationFailed += (_, _) => HandleFailure();
        }

        public static bool HasPendingOperation(IPackage package, OperationType role)
        {
            if (package.Tag is not (PackageTag.OnQueue or PackageTag.BeingProcessed))
                return false;

            Logger.Warn(
                $"Skipping {role} of {package.Id} because an operation for this package is already queued or running"
            );
            return true;
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
        /// when the UseAgentBroker setting is enabled and the manager is supported by the
        /// broker protocol. Falls back to process-based execution otherwise.
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
        /// Whether a package operation is eligible for broker routing. The manager must be
        /// mappable to a broker protocol manager, and virtual/local sources are excluded:
        /// the agent command builder always emits --source from the request, while the local
        /// path deliberately omits it for virtual sources (e.g. the Local PC source).
        /// </summary>
        private static bool IsBrokerEligible(IPackage package) =>
            Settings.Get(Settings.K.UseAgentBroker)
            && BrokerRequestBuilder.SupportsManager(package.Manager.Name)
            && !package.Source.IsVirtualManager;

        /// <summary>
        /// Raw process output streamed over the event channel of the current brokered
        /// run, in emission order. Null when no streamed output was captured (no event
        /// channel, or streaming failed); result parsers then receive an empty list.
        /// </summary>
        private List<string>? _brokerStreamedOutput;

        /// <summary>
        /// Perform the package operation through the Devolutions Agent broker.
        /// Sends the request over named pipe and interprets the response.
        /// </summary>
        private async Task<OperationVeredict> PerformBrokerOperation()
        {
            _brokerStreamedOutput = null;
            Line("Routing operation through Devolutions Agent broker...", LineType.Information);

            // Apply manager-specific elevation requirements (e.g. WinGet's detection of
            // machine-scope or elevation-requiring installers) before deciding the requested
            // elevation, mirroring the local execution path where this runs as part of
            // building the process parameters.
            Package.Manager.OperationHelper.ApplyElevationRequirements(Package, Options, Role);

            bool requestElevated = RequiresAdminRights();
            using var client = CreateBrokerClient(requestElevated);

            // Check broker availability. Brokered operations must not fall back to local
            // execution: policy evaluation and kill/pre/post actions are owned by the broker.
            if (!await client.IsAvailable(CancellationToken))
            {
                return HandleBrokerUnavailable();
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
            Line($"  Elevation: {(requestElevated ? "Elevated" : "Standard")}", LineType.VerboseDetails);

            try
            {
                // Submit the operation explicitly (instead of ExecuteAndWait) so the
                // operation id is available for broker-side cancellation.
                var execution = await client.Execute(request, CancellationToken);

                if (execution.Decision.Decision != BrokerDecision.Allow)
                {
                    string denialReason = execution.Decision.Reason ?? CoreTools.Translate("No reason provided");
                    Line($"Operation denied by policy: {denialReason}", LineType.Error);
                    Metadata.FailureTitle = CoreTools.Translate("Operation denied by policy");
                    Metadata.FailureMessage = denialReason;
                    return OperationVeredict.Failure;
                }

                if (execution.Operation is null)
                {
                    Line("Broker allowed the operation but did not return an operation submission.", LineType.Error);
                    Metadata.FailureTitle = CoreTools.Translate("Operation failed via broker");
                    Metadata.FailureMessage = CoreTools.Translate(
                        "The broker accepted the request but did not report an operation to track.");
                    return OperationVeredict.Failure;
                }

                string operationId = execution.Operation.OperationId;
                Line($"Broker accepted operation: {operationId}", LineType.VerboseDetails);

                // Bound the whole tracking phase (streaming or polling) so a broker that
                // never reports a terminal status cannot hang the operation forever.
                using var operationTimeout = new CancellationTokenSource(BrokerOperationTimeout);
                using var tracking = CancellationTokenSource.CreateLinkedTokenSource(
                    CancellationToken, operationTimeout.Token);
                try
                {
                    // Prefer live status/output streaming over the per-operation event channel
                    // when the broker advertises one; otherwise (or if streaming breaks) fall
                    // back to plain status polling without live output.
                    if (execution.Operation.EventChannel is not null)
                    {
                        OperationVeredict? streamed = await StreamBrokerOperationEvents(
                            client, execution, operationId, tracking.Token);
                        if (streamed is not null)
                        {
                            return streamed.Value;
                        }
                    }
                    else
                    {
                        Logger.Info("[AgentBroker] The broker did not advertise an event channel; using status polling without live output.");
                    }

                    BrokerStatusResponse status = await WaitForBrokerTerminalStatus(client, operationId, tracking.Token);
                    return await InterpretBrokerTerminalStatus(status);
                }
                catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
                {
                    return await CancelBrokerOperation(client, operationId);
                }
                catch (OperationCanceledException) when (operationTimeout.IsCancellationRequested)
                {
                    string timeoutMessage = CoreTools.Translate(
                        "The operation did not finish within the allotted time. It may still be running on the agent.");
                    Line($"Broker operation timed out after {BrokerOperationTimeout}.", LineType.Error);
                    Logger.Error($"[AgentBroker] Operation {operationId} did not reach a terminal status within {BrokerOperationTimeout}");
                    Metadata.FailureTitle = CoreTools.Translate("Operation failed via broker");
                    Metadata.FailureMessage = timeoutMessage;
                    return OperationVeredict.Failure;
                }
            }
            catch (OperationCanceledException)
            {
                Line("Broker operation was canceled.", LineType.Information);
                return OperationVeredict.Canceled;
            }
            catch (BrokerClientException ex) when (ex.Kind is BrokerClientErrorKind.BrokerUnavailable)
            {
                // The broker can stop between the availability probe and the request itself;
                // route this through the same unavailable handling as a failed probe.
                Logger.Error($"[AgentBroker] Broker became unavailable during the operation: {ex}");
                return HandleBrokerUnavailable();
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

        /// <summary>
        /// Consumes the per-operation event channel advertised by the broker, emitting
        /// live stdout/stderr output and reacting to status-change hints. Returns the
        /// final operation veredict, or <c>null</c> when streaming could not be used and
        /// the caller should fall back to plain status polling. The supplied token combines
        /// user cancellation with the overall operation timeout; timeout-induced
        /// cancellations propagate to the caller as <see cref="OperationCanceledException"/>.
        /// </summary>
        private async Task<OperationVeredict?> StreamBrokerOperationEvents(
            BrokerClient client,
            BrokerExecutionResponse execution,
            string operationId,
            CancellationToken trackingToken)
        {
            BrokerOperationEventChannel channel;
            try
            {
                channel = await client.OpenEventChannel(execution, trackingToken);
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                return await CancelBrokerOperation(client, operationId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warn($"[AgentBroker] Could not open the operation event channel; falling back to status polling: {ex}");
                Line("Live output is not available for this operation; progress will be tracked via status polling.", LineType.Information);
                return null;
            }

            Line("Live output streaming enabled via broker event channel.", LineType.VerboseDetails);
            var capturedOutput = new List<string>();
            _brokerStreamedOutput = capturedOutput;
            var stdout = new StreamedOutputLineBuffer(this, LineType.Information, capturedOutput);
            var stderr = new StreamedOutputLineBuffer(this, LineType.Error, capturedOutput);

            await using (channel.ConfigureAwait(false))
            {
                try
                {
                    BrokerOperationStatus? lastReportedStatus = null;
                    await foreach (var frame in channel.ReadEvents(trackingToken))
                    {
                        switch (frame)
                        {
                            case BrokerEventFrame.Stdout frameData:
                                stdout.Append(frameData.Data);
                                break;
                            case BrokerEventFrame.Stderr frameData:
                                stderr.Append(frameData.Data);
                                break;
                            case BrokerEventFrame.StdoutOverflow overflow:
                                Line($"Warning: {overflow.BytesSkipped} bytes of process output were skipped by the broker.", LineType.Information);
                                break;
                            case BrokerEventFrame.StderrOverflow overflow:
                                Line($"Warning: {overflow.BytesSkipped} bytes of process error output were skipped by the broker.", LineType.Information);
                                break;
                            case BrokerEventFrame.StatusUpdated:
                                var updated = await client.QueryStatus(
                                    new OperationStatusQuery { OperationId = operationId },
                                    trackingToken);
                                if (updated.Status != lastReportedStatus)
                                {
                                    lastReportedStatus = updated.Status;
                                    Line($"Broker operation status: {updated.Status}", LineType.VerboseDetails);
                                }
                                break;
                                // Hello and Finish frames need no handling here: the channel
                                // validates the handshake, and Finish ends the enumeration.
                        }
                    }
                }
                catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
                {
                    return await CancelBrokerOperationWhileStreaming(client, channel, operationId, stdout, stderr);
                }
                catch (OperationCanceledException)
                {
                    // Operation-timeout cancellation: surface any buffered output before
                    // letting the caller report the timeout failure.
                    stdout.Flush();
                    stderr.Flush();
                    throw;
                }
                catch (Exception ex) when (ex is BrokerEventFrameException or IOException)
                {
                    // The frame stream is corrupt or the transport failed; the operation
                    // itself is still running on the broker. Fall back to status polling.
                    stdout.Flush();
                    stderr.Flush();
                    Logger.Warn($"[AgentBroker] The operation event channel failed mid-stream; falling back to status polling: {ex}");
                    Line("Live output streaming was interrupted; progress will be tracked via status polling.", LineType.Information);
                    return null;
                }

                stdout.Flush();
                stderr.Flush();
            }

            BrokerStatusResponse status;
            try
            {
                status = await QueryBrokerTerminalStatus(client, operationId, trackingToken);
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                return await CancelBrokerOperation(client, operationId);
            }

            return await InterpretBrokerTerminalStatus(status);
        }

        /// <summary>
        /// Queries the broker for the operation status, and keeps polling until a
        /// terminal status is reported. Unlike <see cref="WaitForBrokerTerminalStatus"/>,
        /// the first query happens immediately (no initial delay).
        /// </summary>
        private static async Task<BrokerStatusResponse> QueryBrokerTerminalStatus(
            BrokerClient client,
            string operationId,
            CancellationToken cancellationToken)
        {
            var status = await client.QueryStatus(
                new OperationStatusQuery { OperationId = operationId },
                cancellationToken);

            if (status.Status is BrokerOperationStatus.Completed
                or BrokerOperationStatus.Failed
                or BrokerOperationStatus.Canceled)
            {
                return status;
            }

            return await WaitForBrokerTerminalStatus(client, operationId, cancellationToken);
        }

        /// <summary>
        /// Polls the broker until the operation reaches a terminal status
        /// (Completed, Failed or Canceled).
        /// </summary>
        private static async Task<BrokerStatusResponse> WaitForBrokerTerminalStatus(
            BrokerClient client,
            string operationId,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                await Task.Delay(BrokerStatusPollIntervalMs, cancellationToken);

                var status = await client.QueryStatus(
                    new OperationStatusQuery { OperationId = operationId },
                    cancellationToken);

                if (status.Status is BrokerOperationStatus.Completed
                    or BrokerOperationStatus.Failed
                    or BrokerOperationStatus.Canceled)
                {
                    return status;
                }
            }
        }

        /// <summary>
        /// Requests broker-side cancellation of a running operation, then waits (bounded)
        /// for the operation to reach a terminal status. The remote process may win the
        /// race and complete or fail before the cancel takes effect; in that case the
        /// terminal status is honored instead of reporting a cancellation.
        /// </summary>
        private async Task<OperationVeredict> CancelBrokerOperation(BrokerClient client, string operationId)
        {
            await RequestBrokerCancel(client, operationId);
            return await ConfirmBrokerCancellation(client, operationId);
        }

        /// <summary>
        /// Cancellation flow used while consuming the event channel: after asking the
        /// broker to cancel, keeps draining the channel (bounded) so the tail of the
        /// process output and the Finish frame are honored, then confirms the terminal
        /// status over the regular status endpoint. If draining fails, falls back to
        /// the plain poll-based confirmation.
        /// </summary>
        private async Task<OperationVeredict> CancelBrokerOperationWhileStreaming(
            BrokerClient client,
            BrokerOperationEventChannel channel,
            string operationId,
            StreamedOutputLineBuffer stdout,
            StreamedOutputLineBuffer stderr)
        {
            await RequestBrokerCancel(client, operationId);

            try
            {
                using var drainTimeout = new CancellationTokenSource(BrokerCancelConfirmTimeout);
                await foreach (var frame in channel.ReadEvents(drainTimeout.Token))
                {
                    switch (frame)
                    {
                        case BrokerEventFrame.Stdout frameData:
                            stdout.Append(frameData.Data);
                            break;
                        case BrokerEventFrame.Stderr frameData:
                            stderr.Append(frameData.Data);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[AgentBroker] Could not drain the event channel of canceled operation {operationId}: {ex}");
            }
            finally
            {
                stdout.Flush();
                stderr.Flush();
            }

            return await ConfirmBrokerCancellation(client, operationId);
        }

        /// <summary>
        /// Best-effort broker-side cancel request, bounded by
        /// <see cref="BrokerCancelRequestTimeout"/>. Failures are logged but not
        /// surfaced: the cancel request is idempotent, and the operation may already
        /// have reached a terminal state.
        /// </summary>
        private async Task RequestBrokerCancel(BrokerClient client, string operationId)
        {
            Line("Cancellation requested; asking broker to cancel the remote operation...", LineType.Information);

            try
            {
                using var cancelTimeout = new CancellationTokenSource(BrokerCancelRequestTimeout);
                var cancelResponse = await client.Cancel(
                    new OperationCancelQuery { OperationId = operationId },
                    cancelTimeout.Token);
                Line($"Broker acknowledged cancel request: {cancelResponse.Status}", LineType.VerboseDetails);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[AgentBroker] Cancel request for operation {operationId} failed: {ex}");
                Line("Broker cancel request failed; checking final operation status...", LineType.Information);
            }
        }

        /// <summary>
        /// Waits (bounded) for a canceled operation to reach a terminal status. The
        /// remote process may win the race and complete or fail before the cancel takes
        /// effect; in that case the terminal status is honored instead of reporting a
        /// cancellation.
        /// </summary>
        private async Task<OperationVeredict> ConfirmBrokerCancellation(BrokerClient client, string operationId)
        {
            try
            {
                using var confirmTimeout = new CancellationTokenSource(BrokerCancelConfirmTimeout);
                var status = await QueryBrokerTerminalStatus(client, operationId, confirmTimeout.Token);

                if (status.Status is not BrokerOperationStatus.Canceled)
                {
                    // The remote process finished before the cancel took effect.
                    Line($"Broker operation finished before cancellation took effect: {status.Status}", LineType.Information);
                    return await InterpretBrokerTerminalStatus(status);
                }
            }
            catch (Exception ex)
            {
                // The user asked for cancellation; do not surface polling failures as errors.
                Logger.Warn($"[AgentBroker] Could not confirm terminal status of canceled operation {operationId}: {ex}");
            }

            Line("Broker operation was canceled.", LineType.Information);
            return OperationVeredict.Canceled;
        }

        /// <summary>
        /// Maps a terminal broker status response to an operation veredict, setting
        /// failure metadata where appropriate.
        /// </summary>
        private async Task<OperationVeredict> InterpretBrokerTerminalStatus(BrokerStatusResponse status)
        {
            Line($"Broker status: {status.Status}, exitCode={status.ExitCode}", LineType.Information);
            if (!string.IsNullOrWhiteSpace(status.Message))
            {
                Line($"  Message: {status.Message}", LineType.Information);
            }

            if (status.Status is BrokerOperationStatus.Canceled)
            {
                Line("Broker operation was canceled.", LineType.Information);
                return OperationVeredict.Canceled;
            }

            if (status.Status is BrokerOperationStatus.Completed)
            {
                // Feed the process output streamed over the event channel (if any) to
                // the manager's result parser, like the local process path does. Only
                // real process output is passed; internal informational lines are not.
                var veredict = await GetProcessVeredict(status.ExitCode ?? -1, _brokerStreamedOutput ?? []);
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

        /// <summary>
        /// Fails the operation because the agent broker is unreachable: brokered operations
        /// must not fall back to local execution, since policy evaluation and kill/pre/post
        /// actions are owned by the broker. Sets the failure metadata and raises
        /// <see cref="BrokerUnavailable"/> so the UI can notify the user.
        /// </summary>
        private OperationVeredict HandleBrokerUnavailable()
        {
            Line("Agent broker is not available. The operation cannot continue.", LineType.Error);
            Logger.Error("[AgentBroker] Broker not available, aborting operation");
            string message = CoreTools.Translate(
                "The Devolutions Agent broker is not available. The operation cannot be performed. Please ensure the Devolutions Agent is installed and running.");
            Metadata.FailureTitle = CoreTools.Translate("Agent broker unavailable");
            Metadata.FailureMessage = message;
            BrokerUnavailable?.Invoke(this, message);
            return OperationVeredict.Failure;
        }

        /// <summary>
        /// Buffers streamed process output and emits it line by line through
        /// <see cref="AbstractOperation.Line"/>, mirroring the local process reader:
        /// LF-terminated text is emitted with the configured line type, while
        /// CR-terminated text (progress bars) is emitted as a progress indicator and
        /// promoted to a regular line when followed by a bare LF. Regular (non-progress)
        /// lines are also recorded in <paramref name="capturedOutput"/> so the manager's
        /// result parser receives only real process output.
        /// </summary>
        private sealed class StreamedOutputLineBuffer(
            PackageOperation owner,
            LineType lineType,
            List<string> capturedOutput)
        {
            private readonly StringBuilder _pending = new();
            private string? _lastLineBeforeLF;

            public void Append(string data)
            {
                ReadOnlySpan<char> remaining = data;
                while (!remaining.IsEmpty)
                {
                    int terminatorIndex = remaining.IndexOfAny('\r', '\n');
                    if (terminatorIndex < 0)
                    {
                        _pending.Append(remaining);
                        break;
                    }

                    _pending.Append(remaining[..terminatorIndex]);
                    char terminator = remaining[terminatorIndex];
                    remaining = remaining[(terminatorIndex + 1)..];

                    if (terminator == '\n')
                    {
                        if (_pending.Length == 0)
                        {
                            // A bare LF after a CR-terminated line (CRLF): promote the
                            // progress line to a regular line.
                            if (_lastLineBeforeLF is not null)
                            {
                                EmitLine(_lastLineBeforeLF);
                                _lastLineBeforeLF = null;
                            }

                            continue;
                        }

                        EmitLine(_pending.ToString());
                        _pending.Clear();
                        // New text arrived after the CR: the progress line was
                        // superseded and must not be promoted by a later bare LF.
                        _lastLineBeforeLF = null;
                    }
                    else
                    {
                        if (_pending.Length == 0)
                        {
                            continue;
                        }

                        _lastLineBeforeLF = _pending.ToString();
                        owner.Line(_lastLineBeforeLF, LineType.ProgressIndicator);
                        _pending.Clear();
                    }
                }
            }

            /// <summary>
            /// Emits any remaining partial line (e.g. output not terminated by a newline
            /// when the channel finished).
            /// </summary>
            public void Flush()
            {
                if (_pending.Length > 0)
                {
                    EmitLine(_pending.ToString());
                    _pending.Clear();
                }

                _lastLineBeforeLF = null;
            }

            private void EmitLine(string line)
            {
                owner.Line(line, lineType);
                capturedOutput.Add(line);
            }
        }

        private static BrokerClient CreateBrokerClient(bool requestedElevation) =>
            new(
                new BrokerClientOptions
                {
                    Transport = BrokerTransportFactory?.Invoke(),
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
                BrokerClientErrorKind.Timeout => "Broker communication error",
                _ => "Operation failed via broker",
            };

        protected sealed override Task<OperationVeredict> GetProcessVeredict(
            int ReturnCode,
            List<string> Output
        )
        {
            var veredict = Package.Manager.OperationHelper.GetResult(
                Package,
                Role,
                Output,
                ReturnCode
            );

            if (veredict is OperationVeredict.Failure && Role is OperationType.Update)
                ExplainNotApplicableUpdate(Output, ReturnCode);

            return Task.FromResult(veredict);
        }

        private void ExplainNotApplicableUpdate(List<string> output, int returnCode)
        {
#if WINDOWS
            if (Package.Manager is not WinGet winget)
                return;

            if (!winget.ReportedUpdateNotApplicable(output, returnCode))
                return;

            Metadata.FailureMessage = CoreTools.Translate(
                "{package} may already be up to date, or no installer matches this system",
                new Dictionary<string, object?> { { "package", Package.Name } }
            );
#endif
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
        /// execution path: for WinGet updates this uses the portable-install safeguard
        /// (registry-detected location, saved value only under WinGetForceLocationOnUpdate);
        /// for installs (and non-WinGet updates) the configured custom location; for
        /// uninstalls nothing.
        /// </summary>
        private string? GetBrokerEffectiveInstallLocation()
        {
            switch (Role)
            {
                case OperationType.Update:
#if WINDOWS
                    if (IsWinGetManager(Package.Manager))
                    {
                        return WinGetPkgOperationHelper.GetEffectiveUpdateLocation(Package, Options);
                    }
#endif
                    goto case OperationType.Install;
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

            if (Settings.Get(Settings.K.AskToDeleteNewDesktopShortcuts))
            {
                DesktopShortcutsDatabase.HandleNewShortcuts(DesktopShortcutsBeforeStart);
            }

            bool explicitVersionRequested = !string.IsNullOrWhiteSpace(Options.Version);
            var installedPackage = await ResolveInstalledPackageSnapshotAsync(
                explicitVersionRequested ? Options.Version : Package.VersionString,
                preferFallbackVersionWhenMissing: explicitVersionRequested
            );
            await InstalledPackagesLoader.Instance.AddForeign(installedPackage);
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

            if (Settings.Get(Settings.K.AskToDeleteNewDesktopShortcuts))
            {
                DesktopShortcutsDatabase.HandleNewShortcuts(DesktopShortcutsBeforeStart);
            }

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
