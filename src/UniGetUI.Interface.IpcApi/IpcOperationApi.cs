using System.Collections.Concurrent;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageOperations;

namespace UniGetUI.Interface;

public sealed class IpcOperationOutputLine
{
    public string Text { get; set; } = "";
    public string Type { get; set; } = "";
}

public class IpcOperationInfo
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Started { get; set; }
    public string LiveLine { get; set; } = "";
    public string LiveLineType { get; set; } = "";
    public int? QueuePosition { get; set; }
    public int OutputLineCount { get; set; }
    public bool CanCancel { get; set; }
    public bool CanForget { get; set; }
    public IReadOnlyList<string> AvailableQueueActions { get; set; } = [];
    public IReadOnlyList<string> AvailableRetryModes { get; set; } = [];
    public IpcPackageInfo? Package { get; set; }
    public string ManagerName { get; set; } = "";
    public string SourceName { get; set; } = "";
}

public sealed class IpcOperationDetails : IpcOperationInfo
{
    public IReadOnlyList<IpcOperationOutputLine> Output { get; set; } = [];
}

public sealed class IpcOperationOutputResult
{
    public string OperationId { get; set; } = "";
    public int LineCount { get; set; }
    public IReadOnlyList<IpcOperationOutputLine> Output { get; set; } = [];
}

public static class IpcOperationApi
{
    private sealed class TrackedOperation
    {
        private readonly List<IpcOperationOutputLine> _output = [];
        private readonly object _syncRoot = new();

        public AbstractOperation Operation { get; }
        public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; private set; } = DateTime.UtcNow;
        public string LiveLine { get; private set; } = CoreTools.Translate("Please wait...");
        public string LiveLineType { get; private set; } = ToLineTypeName(
            AbstractOperation.LineType.ProgressIndicator
        );

        public TrackedOperation(AbstractOperation operation)
        {
            Operation = operation;

            foreach (var (text, type) in operation.GetOutput())
            {
                AddLine(text, type);
            }
        }

        public void AddLine(string text, AbstractOperation.LineType type)
        {
            lock (_syncRoot)
            {
                var line = new IpcOperationOutputLine
                {
                    Text = text,
                    Type = ToLineTypeName(type),
                };
                _output.Add(line);
                LiveLine = text;
                LiveLineType = line.Type;
                UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        public void Touch()
        {
            lock (_syncRoot)
            {
                UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        public int GetOutputCount()
        {
            lock (_syncRoot)
            {
                return _output.Count;
            }
        }

        public IReadOnlyList<IpcOperationOutputLine> GetOutputSnapshot(int? tailLines = null)
        {
            lock (_syncRoot)
            {
                if (!tailLines.HasValue || tailLines.Value <= 0 || tailLines.Value >= _output.Count)
                {
                    return _output.ToArray();
                }

                return _output.Skip(_output.Count - tailLines.Value).ToArray();
            }
        }
    }

    private static readonly ConcurrentDictionary<string, TrackedOperation> Operations = new();
    private const int MaxTrackedOperations = 200;

    public static string Track(AbstractOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        string operationId = operation.Metadata.Identifier;
        Operations.GetOrAdd(
            operationId,
            _ =>
            {
                var tracked = new TrackedOperation(operation);
                operation.LogLineAdded += (_, line) => tracked.AddLine(line.Item1, line.Item2);
                operation.StatusChanged += (_, _) => tracked.Touch();
                operation.OperationFinished += (_, _) => tracked.Touch();
                return tracked;
            }
        );

        PruneCompletedOperations();
        return operationId;
    }

    public static IReadOnlyList<IpcOperationInfo> ListOperations()
    {
        return Operations
            .Values.OrderBy(entry => IsActive(entry.Operation.Status) ? 0 : 1)
            .ThenBy(entry => entry.CreatedAtUtc)
            .Select(CreateOperationInfo)
            .ToArray();
    }

    public static IpcOperationDetails GetOperation(string operationId)
    {
        var tracked = GetTrackedOperation(operationId);
        var info = CreateOperationInfo(tracked);
        return new IpcOperationDetails
        {
            Id = info.Id,
            Kind = info.Kind,
            Title = info.Title,
            Status = info.Status,
            Started = info.Started,
            LiveLine = info.LiveLine,
            LiveLineType = info.LiveLineType,
            QueuePosition = info.QueuePosition,
            OutputLineCount = info.OutputLineCount,
            CanCancel = info.CanCancel,
            CanForget = info.CanForget,
            AvailableQueueActions = info.AvailableQueueActions,
            AvailableRetryModes = info.AvailableRetryModes,
            Package = info.Package,
            ManagerName = info.ManagerName,
            SourceName = info.SourceName,
            Output = tracked.GetOutputSnapshot(),
        };
    }

    public static IpcOperationOutputResult GetOperationOutput(
        string operationId,
        int? tailLines = null
    )
    {
        if (tailLines.HasValue && tailLines.Value < 0)
        {
            throw new InvalidOperationException("tailLines must be greater than or equal to zero.");
        }

        var tracked = GetTrackedOperation(operationId);
        return new IpcOperationOutputResult
        {
            OperationId = tracked.Operation.Metadata.Identifier,
            LineCount = tracked.GetOutputCount(),
            Output = tracked.GetOutputSnapshot(tailLines),
        };
    }

    public static IpcCommandResult CancelOperation(string operationId)
    {
        var tracked = GetTrackedOperation(operationId);
        if (!IsActive(tracked.Operation.Status))
        {
            throw new InvalidOperationException(
                "Only queued or running operations can be canceled."
            );
        }

        tracked.Operation.Cancel();
        return IpcCommandResult.Success("cancel-operation");
    }

    public static IpcCommandResult RetryOperation(string operationId, string? retryMode)
    {
        var tracked = GetTrackedOperation(operationId);
        if (IsActive(tracked.Operation.Status))
        {
            throw new InvalidOperationException(
                "Running or queued operations cannot be retried."
            );
        }

        string normalizedRetryMode = NormalizeRetryMode(retryMode);
        var availableRetryModes = GetRetryModes(tracked.Operation);
        if (!availableRetryModes.Contains(ToRetryModeName(normalizedRetryMode)))
        {
            throw new InvalidOperationException(
                $"Retry mode \"{retryMode}\" is not supported for operation {operationId}."
            );
        }

        tracked.Operation.Retry(normalizedRetryMode);
        return IpcCommandResult.Success("retry-operation");
    }

    public static IpcCommandResult ReorderOperation(string operationId, string action)
    {
        var tracked = GetTrackedOperation(operationId);
        if (tracked.Operation.Status != OperationStatus.InQueue)
        {
            throw new InvalidOperationException(
                "Only queued operations can be reordered."
            );
        }

        switch (NormalizeQueueAction(action))
        {
            case "run-now":
                tracked.Operation.SkipQueue();
                break;
            case "run-next":
                tracked.Operation.RunNext();
                break;
            case "run-last":
                tracked.Operation.BackOfTheQueue();
                break;
            default:
                throw new InvalidOperationException($"Unsupported queue action \"{action}\".");
        }

        return IpcCommandResult.Success("reorder-operation");
    }

    public static IpcCommandResult ForgetOperation(string operationId)
    {
        var tracked = GetTrackedOperation(operationId);
        if (IsActive(tracked.Operation.Status))
        {
            throw new InvalidOperationException(
                "Running or queued operations cannot be forgotten."
            );
        }

        ForgetTracking(operationId);
        return IpcCommandResult.Success("forget-operation");
    }

    public static void ForgetTracking(string operationId)
    {
        Operations.TryRemove(operationId, out _);
    }

    private static TrackedOperation GetTrackedOperation(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        return Operations.TryGetValue(operationId.Trim(), out var tracked)
            ? tracked
            : throw new InvalidOperationException(
                $"No tracked operation with id \"{operationId}\" was found."
            );
    }

    private static IpcOperationInfo CreateOperationInfo(TrackedOperation tracked)
    {
        var operation = tracked.Operation;
        return new IpcOperationInfo
        {
            Id = operation.Metadata.Identifier,
            Kind = GetOperationKind(operation),
            Title = operation.Metadata.Title,
            Status = operation.Status.ToString().ToLowerInvariant(),
            Started = operation.Started,
            LiveLine = tracked.LiveLine,
            LiveLineType = tracked.LiveLineType,
            QueuePosition = GetQueuePosition(operation),
            OutputLineCount = tracked.GetOutputCount(),
            CanCancel = IsActive(operation.Status),
            CanForget = !IsActive(operation.Status),
            AvailableQueueActions = operation.Status == OperationStatus.InQueue
                ? ["run-now", "run-next", "run-last"]
                : [],
            AvailableRetryModes = !IsActive(operation.Status) ? GetRetryModes(operation) : [],
            Package = GetOperationPackage(operation),
            ManagerName = GetManagerName(operation),
            SourceName = GetSourceName(operation),
        };
    }

    private static IpcPackageInfo? GetOperationPackage(AbstractOperation operation)
    {
        return operation switch
        {
            PackageOperation packageOperation => IpcPackageApi.CreateIpcPackageInfo(
                packageOperation.Package
            ),
            DownloadOperation downloadOperation => IpcPackageApi.CreateIpcPackageInfo(
                downloadOperation.Package
            ),
            _ => null,
        };
    }

    private static string GetManagerName(AbstractOperation operation)
    {
        return operation switch
        {
            PackageOperation packageOperation => IpcManagerSettingsApi.GetPublicManagerId(
                packageOperation.Package.Manager
            ),
            DownloadOperation downloadOperation => IpcManagerSettingsApi.GetPublicManagerId(
                downloadOperation.Package.Manager
            ),
            SourceOperation sourceOperation => IpcManagerSettingsApi.GetPublicManagerId(
                sourceOperation.ManagerSource.Manager
            ),
            _ => "",
        };
    }

    private static string GetSourceName(AbstractOperation operation)
    {
        return operation is SourceOperation sourceOperation ? sourceOperation.ManagerSource.Name : "";
    }

    private static string GetOperationKind(AbstractOperation operation)
    {
        return operation switch
        {
            InstallPackageOperation => "install-package",
            UpdatePackageOperation => "update-package",
            UninstallPackageOperation => "uninstall-package",
            DownloadOperation => "download-package",
            AddSourceOperation => "add-source",
            RemoveSourceOperation => "remove-source",
            _ => operation.GetType().Name,
        };
    }

    private static int? GetQueuePosition(AbstractOperation operation)
    {
        if (operation.Status != OperationStatus.InQueue)
        {
            return null;
        }

        int index = AbstractOperation.OperationQueue.IndexOf(operation);
        if (index < 0)
        {
            return null;
        }

        return Math.Max(index - AbstractOperation.MAX_OPERATIONS + 1, 0);
    }

    private static IReadOnlyList<string> GetRetryModes(AbstractOperation operation)
    {
        List<string> retryModes = ["retry"];

        switch (operation)
        {
            case PackageOperation packageOperation:
                if (
                    !packageOperation.Options.RunAsAdministrator
                    && packageOperation.Package.Manager.Capabilities.CanRunAsAdmin
                )
                {
                    retryModes.Add("retry-as-admin");
                }

                if (
                    !packageOperation.Options.InteractiveInstallation
                    && packageOperation.Package.Manager.Capabilities.CanRunInteractively
                )
                {
                    retryModes.Add("retry-interactive");
                }

                if (
                    !packageOperation.Options.SkipHashCheck
                    && packageOperation.Package.Manager.Capabilities.CanSkipIntegrityChecks
                )
                {
                    retryModes.Add("retry-no-hash-check");
                }

                break;
            case SourceOperation sourceOperation when !sourceOperation.ForceAsAdministrator:
                retryModes.Add("retry-as-admin");
                break;
        }

        return retryModes;
    }

    private static string NormalizeRetryMode(string? retryMode)
    {
        if (string.IsNullOrWhiteSpace(retryMode))
        {
            return AbstractOperation.RetryMode.Retry;
        }

        return retryMode.Trim().ToLowerInvariant() switch
        {
            "retry" => AbstractOperation.RetryMode.Retry,
            "retry-as-admin" => AbstractOperation.RetryMode.Retry_AsAdmin,
            "retry-interactive" => AbstractOperation.RetryMode.Retry_Interactive,
            "retry-no-hash-check" => AbstractOperation.RetryMode.Retry_SkipIntegrity,
            _ => throw new InvalidOperationException(
                $"Unsupported retry mode \"{retryMode}\"."
            ),
        };
    }

    private static string ToRetryModeName(string retryMode)
    {
        return retryMode switch
        {
            var mode when mode == AbstractOperation.RetryMode.Retry => "retry",
            var mode when mode == AbstractOperation.RetryMode.Retry_AsAdmin => "retry-as-admin",
            var mode when mode == AbstractOperation.RetryMode.Retry_Interactive
                => "retry-interactive",
            var mode when mode == AbstractOperation.RetryMode.Retry_SkipIntegrity
                => "retry-no-hash-check",
            _ => retryMode,
        };
    }

    private static string NormalizeQueueAction(string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        return action.Trim().ToLowerInvariant() switch
        {
            "run-now" => "run-now",
            "run-next" => "run-next",
            "run-last" => "run-last",
            _ => throw new InvalidOperationException($"Unsupported queue action \"{action}\"."),
        };
    }

    private static bool IsActive(OperationStatus status)
    {
        return status is OperationStatus.InQueue or OperationStatus.Running;
    }

    private static string ToLineTypeName(AbstractOperation.LineType type)
    {
        return type switch
        {
            AbstractOperation.LineType.VerboseDetails => "verbose",
            AbstractOperation.LineType.ProgressIndicator => "progress",
            AbstractOperation.LineType.Information => "information",
            AbstractOperation.LineType.Error => "error",
            _ => type.ToString().ToLowerInvariant(),
        };
    }

    private static void PruneCompletedOperations()
    {
        if (Operations.Count <= MaxTrackedOperations)
        {
            return;
        }

        foreach (
            var completedOperation in Operations
                .Values.Where(entry => !IsActive(entry.Operation.Status))
                .OrderBy(entry => entry.UpdatedAtUtc)
                .Take(Operations.Count - MaxTrackedOperations)
                .ToArray()
        )
        {
            ForgetTracking(completedOperation.Operation.Metadata.Identifier);
        }
    }
}
