using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.PackageOperations;

public abstract partial class AbstractOperation : IDisposable
{
    public readonly OperationMetadata Metadata = new();

    public event EventHandler<OperationStatus>? StatusChanged;
    public event EventHandler<EventArgs>? CancelRequested;
    public event EventHandler<(string, LineType)>? LogLineAdded;
    public event EventHandler<EventArgs>? OperationStarting;
    public event EventHandler<EventArgs>? OperationFinished;
    public event EventHandler<EventArgs>? Enqueued;
    public event EventHandler<EventArgs>? OperationSucceeded;
    public event EventHandler<EventArgs>? OperationFailed;
    public event EventHandler<BadgeCollection>? BadgesChanged;

    public bool Started { get; private set; }
    protected bool QUEUE_ENABLED;
    protected bool FORCE_HOLD_QUEUE;
    private bool IsInnerOperation;
    private readonly object CancellationLock = new();
    private CancellationTokenSource? RunCancellationSource;
    private AbstractOperation? ActiveInnerOperation;
    private volatile bool IsExecutingOperation;
    private bool Disposed;

    private readonly List<(string, LineType)> LogList = [];
    private OperationStatus _status = OperationStatus.InQueue;
    public OperationStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            StatusChanged?.Invoke(this, value);
        }
    }

    public void ApplyCapabilities(bool admin, bool interactive, bool skiphash, string? scope)
    {
        BadgesChanged?.Invoke(this, new BadgeCollection(admin, interactive, skiphash, scope));
    }

    private readonly IReadOnlyList<InnerOperation> PreOperations = [];
    private readonly IReadOnlyList<InnerOperation> PostOperations = [];

    public AbstractOperation(
        bool queue_enabled,
        IReadOnlyList<InnerOperation>? preOps = null,
        IReadOnlyList<InnerOperation>? postOps = null
    )
    {
        QUEUE_ENABLED = queue_enabled;
        if (preOps is not null)
            PreOperations = preOps;
        if (postOps is not null)
            PostOperations = postOps;

        Status = OperationStatus.InQueue;
        Line("Please wait...", LineType.ProgressIndicator);

        if (int.TryParse(Settings.GetValue(Settings.K.ParallelOperationCount), out int _maxPps))
        {
            MAX_OPERATIONS = _maxPps;
            Logger.Debug($"Parallel operation limit set to {MAX_OPERATIONS}");
        }
        else
        {
            MAX_OPERATIONS = 1;
            Logger.Debug("Parallel operation limit not set, defaulting to 1");
        }
    }

    public void Cancel()
    {
        if (_status is OperationStatus.Canceled or OperationStatus.Failed or OperationStatus.Succeeded)
            return;

        bool wasRunning = _status is OperationStatus.Running;
        bool hasActiveWork = IsExecutingOperation;
        Status = OperationStatus.Canceled;
        CancellationTokenSource? cancellationSource;
        lock (CancellationLock)
            cancellationSource = RunCancellationSource;
        cancellationSource?.Cancel();
        AbstractOperation? activeInnerOperation;
        lock (CancellationLock)
            activeInnerOperation = ActiveInnerOperation;
        activeInnerOperation?.Cancel();

        if (wasRunning)
            CancelRequested?.Invoke(this, EventArgs.Empty);

        // A queued operation has no active work to clean up. A running operation stays on the
        // queue until MainThread has awaited its task and completed its cleanup.
        if (!hasActiveWork)
        {
            while (OperationQueue.Remove(this))
                ;
        }
    }

    protected CancellationToken CancellationToken
    {
        get
        {
            lock (CancellationLock)
                return RunCancellationSource?.Token ?? global::System.Threading.CancellationToken.None;
        }
    }

    private CancellationTokenSource StartRunCancellation()
    {
        var cancellationSource = new CancellationTokenSource();
        lock (CancellationLock)
            RunCancellationSource = cancellationSource;
        return cancellationSource;
    }

    private bool TrySetActiveInnerOperation(AbstractOperation operation)
    {
        bool cancellationRequested;
        lock (CancellationLock)
        {
            cancellationRequested =
                RunCancellationSource?.IsCancellationRequested is true
                || Status is OperationStatus.Canceled;
            if (!cancellationRequested)
                ActiveInnerOperation = operation;
        }

        if (cancellationRequested)
            operation.Cancel();

        return !cancellationRequested;
    }

    private void ClearActiveInnerOperation(AbstractOperation operation)
    {
        lock (CancellationLock)
            if (ReferenceEquals(ActiveInnerOperation, operation))
                ActiveInnerOperation = null;
    }

    private void EndRunCancellation(CancellationTokenSource cancellationSource)
    {
        lock (CancellationLock)
        {
            if (!ReferenceEquals(RunCancellationSource, cancellationSource))
                return;
            RunCancellationSource = null;
        }
        cancellationSource.Dispose();
    }

    protected void Line(string line, LineType type)
    {
        // LogList stays raw: it is the source of truth for result parsing. Only display redacts.
        if (type != LineType.ProgressIndicator)
            LogList.Add((line, type));
        LogLineAdded?.Invoke(this, (Logger.Redact(line), type));
    }

    protected IReadOnlyList<(string, LineType)> GetRawOutput() => LogList;

    public IReadOnlyList<(string, LineType)> GetOutput()
    {
        if (!Logger.RedactUsername)
            return LogList;
        return LogList.Select(l => (Logger.Redact(l.Item1), l.Item2)).ToList();
    }

    public async Task MainThread()
    {
        CancellationTokenSource runCancellation = StartRunCancellation();
        try
        {
            if (Metadata.Status == "")
                throw new InvalidDataException("Metadata.Status was not set!");
            if (Metadata.Title == "")
                throw new InvalidDataException("Metadata.Title was not set!");
            if (Metadata.OperationInformation == "")
                throw new InvalidDataException("Metadata.OperationInformation was not set!");
            if (Metadata.SuccessTitle == "")
                throw new InvalidDataException("Metadata.SuccessTitle was not set!");
            if (Metadata.SuccessMessage == "")
                throw new InvalidDataException("Metadata.SuccessMessage was not set!");
            if (Metadata.FailureTitle == "")
                throw new InvalidDataException("Metadata.FailureTitle was not set!");
            if (Metadata.FailureMessage == "")
                throw new InvalidDataException("Metadata.FailureMessage was not set!");

            Started = true;

            if (OperationQueue.Contains(this))
                throw new InvalidOperationException("This operation was already on the queue");

            Status = OperationStatus.InQueue;
            Line(Metadata.OperationInformation, LineType.VerboseDetails);
            Line(Metadata.Status, LineType.ProgressIndicator);

            Enqueued?.Invoke(this, EventArgs.Empty);

            if (QUEUE_ENABLED && !IsInnerOperation)
            {
                // QUEUE HANDLER
                SKIP_QUEUE = false;
                OperationQueue.Add(this);
                int lastPos = -2;

                while (
                    FORCE_HOLD_QUEUE
                    || (OperationQueue.IndexOf(this) >= MAX_OPERATIONS && !SKIP_QUEUE)
                )
                {
                    int pos = OperationQueue.IndexOf(this) - MAX_OPERATIONS + 1;

                    if (pos == -1)
                        return;
                    // In this case, operation was canceled;

                    if (pos != lastPos)
                    {
                        lastPos = pos;
                        Line(
                            CoreTools.Translate("Operation on queue (position {0})...", pos),
                            LineType.ProgressIndicator
                        );
                    }

                    await Task.Delay(100);
                }
            }
            // END QUEUE HANDLER

            IsExecutingOperation = true;
            OperationVeredict result;
            try
            {
                result = await _runOperation();
            }
            finally
            {
                IsExecutingOperation = false;
            }
            while (OperationQueue.Remove(this))
                ;

            if (result == OperationVeredict.Success)
            {
                Status = OperationStatus.Succeeded;
                OperationSucceeded?.Invoke(this, EventArgs.Empty);
                OperationFinished?.Invoke(this, EventArgs.Empty);
                Line(Metadata.SuccessMessage, LineType.Information);
            }
            else if (result == OperationVeredict.Failure)
            {
                Status = OperationStatus.Failed;
                OperationFailed?.Invoke(this, EventArgs.Empty);
                OperationFinished?.Invoke(this, EventArgs.Empty);
                Line(Metadata.FailureMessage, LineType.Error);
                Line(
                    Metadata.FailureMessage
                        + " - "
                        + CoreTools.Translate("Click here for more details"),
                    LineType.ProgressIndicator
                );
            }
            else if (result == OperationVeredict.Canceled)
            {
                Status = OperationStatus.Canceled;
                OperationFinished?.Invoke(this, EventArgs.Empty);
                Line(CoreTools.Translate("Operation canceled by user"), LineType.Error);
            }
            else
            {
                throw new InvalidCastException();
            }
        }
        catch (Exception ex)
        {
            Line("An internal error occurred:", LineType.Error);
            foreach (var line in ex.ToString().Split("\n"))
            {
                Line(line, LineType.Error);
            }

            while (OperationQueue.Remove(this))
                ;

            Status = OperationStatus.Failed;
            try
            {
                OperationFinished?.Invoke(this, EventArgs.Empty);
                OperationFailed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception e2)
            {
                Line(
                    "An internal error occurred while handling an internal error:",
                    LineType.Error
                );
                foreach (var line in e2.ToString().Split("\n"))
                {
                    Line(line, LineType.Error);
                }
            }

            Line(Metadata.FailureMessage, LineType.Error);
            Line(
                Metadata.FailureMessage
                    + " - "
                    + CoreTools.Translate("Click here for more details"),
                LineType.ProgressIndicator
            );
        }
        finally
        {
            try
            {
                OnRunCompleted();
            }
            finally
            {
                EndRunCancellation(runCancellation);
                if (OperationQueue.Count == 0)
                    QueueDrained?.Invoke(null, EventArgs.Empty);
            }
        }
    }

    private async Task<OperationVeredict> _runOperation()
    {
        OperationVeredict result;

        // Process preoperations
        int i = 0,
            count = PreOperations.Count;
        if (count > 0)
            Line("", LineType.VerboseDetails);
        foreach (var preReq in PreOperations)
        {
            if (Status is OperationStatus.Canceled || CancellationToken.IsCancellationRequested)
                return OperationVeredict.Canceled;

            i++;
            Line(
                    CoreTools.Translate("Running PreOperation ({0}/{1})...", i, count),
                LineType.Information
            );
            preReq.Operation.LogLineAdded += (_, line) => Line(line.Item1, line.Item2);
            if (!TrySetActiveInnerOperation(preReq.Operation))
                return OperationVeredict.Canceled;
            try
            {
                await preReq.Operation.MainThread();
            }
            finally
            {
                ClearActiveInnerOperation(preReq.Operation);
            }
            if (Status is OperationStatus.Canceled || CancellationToken.IsCancellationRequested)
                return OperationVeredict.Canceled;
            if (preReq.Operation.Status is not OperationStatus.Succeeded && preReq.MustSucceed)
            {
                Line(
                        CoreTools.Translate(
                            "PreOperation {0} out of {1} failed, and was tagged as necessary. Aborting...",
                            i,
                            count
                        ),
                    LineType.Error
                );
                return OperationVeredict.Failure;
            }
            Line(
                    CoreTools.Translate(
                        "PreOperation {0} out of {1} finished with result {2}",
                        i,
                        count,
                        preReq.Operation.Status
                    ),
                LineType.Information
            );
            Line("--------------------------------", LineType.Information);
            Line("", LineType.VerboseDetails);
        }

        // BEGIN ACTUAL OPERATION
        Line(CoreTools.Translate("Starting operation..."), LineType.Information);
        if (Status is OperationStatus.InQueue)
            Status = OperationStatus.Running;

        do
        {
            if (Status is OperationStatus.Canceled || CancellationToken.IsCancellationRequested)
            {
                result = OperationVeredict.Canceled;
                break;
            }

            OperationStarting?.Invoke(this, EventArgs.Empty);

            try
            {
                Task<OperationVeredict> op = PerformOperation();
                result = await op.ConfigureAwait(false);
                if (Status is OperationStatus.Canceled || CancellationToken.IsCancellationRequested)
                    result = OperationVeredict.Canceled;
            }
            catch (Exception e)
            {
                result = CancellationToken.IsCancellationRequested
                    ? OperationVeredict.Canceled
                    : OperationVeredict.Failure;
                Logger.Error(e);
                foreach (string l in e.ToString().Split("\n"))
                {
                    Line(l, LineType.Error);
                }
            }
        } while (result is OperationVeredict.AutoRetry);

        if (result is not OperationVeredict.Success)
            return result;

        // Process postoperations
        i = 0;
        count = PostOperations.Count;
        foreach (var postReq in PostOperations)
        {
            i++;
            Line("--------------------------------", LineType.Information);
            Line("", LineType.VerboseDetails);
            Line(
                CoreTools.Translate("Running PostOperation ({0}/{1})...", i, count),
                LineType.Information
            );
            if (Status is OperationStatus.Canceled || CancellationToken.IsCancellationRequested)
                return OperationVeredict.Canceled;

            postReq.Operation.LogLineAdded += (_, line) => Line(line.Item1, line.Item2);
            if (!TrySetActiveInnerOperation(postReq.Operation))
                return OperationVeredict.Canceled;
            try
            {
                await postReq.Operation.MainThread();
            }
            finally
            {
                ClearActiveInnerOperation(postReq.Operation);
            }
            if (Status is OperationStatus.Canceled || CancellationToken.IsCancellationRequested)
                return OperationVeredict.Canceled;
            if (postReq.Operation.Status is not OperationStatus.Succeeded && postReq.MustSucceed)
            {
                Line(
                    CoreTools.Translate(
                        "PostOperation {0} out of {1} failed, and was tagged as necessary. Aborting...",
                        i,
                        count
                    ),
                    LineType.Error
                );
                return OperationVeredict.Failure;
            }
            Line(
                CoreTools.Translate(
                    "PostOperation {0} out of {1} finished with result {2}",
                    i,
                    count,
                    postReq.Operation.Status
                ),
                LineType.Information
            );
        }

        return result;
    }

    private bool SKIP_QUEUE;

    public void SkipQueue()
    {
        if (Status != OperationStatus.InQueue)
            return;
        while (OperationQueue.Remove(this))
            ;
        SKIP_QUEUE = true;
    }

    public void RunNext()
    {
        if (Status != OperationStatus.InQueue)
            return;
        if (!OperationQueue.Contains(this))
            return;

        FORCE_HOLD_QUEUE = true;
        while (OperationQueue.Remove(this))
            ;
        OperationQueue.Insert(Math.Min(MAX_OPERATIONS, OperationQueue.Count), this);
        FORCE_HOLD_QUEUE = false;
    }

    public void BackOfTheQueue()
    {
        if (Status != OperationStatus.InQueue)
            return;
        if (!OperationQueue.Contains(this))
            return;

        FORCE_HOLD_QUEUE = true;
        while (OperationQueue.Remove(this))
            ;
        OperationQueue.Add(this);
        FORCE_HOLD_QUEUE = false;
    }

    public void Retry(string retryMode)
    {
        if (retryMode is RetryMode.NoRetry)
            throw new InvalidOperationException("We weren't supposed to reach this, weren't we?");

        ApplyRetryAction(retryMode);
        Line($"", LineType.VerboseDetails);
        Line($"-----------------------", LineType.VerboseDetails);
        Line($"Retrying operation with RetryMode={retryMode}", LineType.VerboseDetails);
        Line($"", LineType.VerboseDetails);
        if (Status is OperationStatus.Running or OperationStatus.InQueue)
            return;
        _ = MainThread();
    }

    protected abstract void ApplyRetryAction(string retryMode);
    protected abstract Task<OperationVeredict> PerformOperation();
    public abstract Task<Uri> GetOperationIcon();

    protected virtual void OnRunCompleted() { }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || Disposed)
            return;

        Disposed = true;
        Cancel();
        if (!IsExecutingOperation)
        {
            while (OperationQueue.Remove(this))
                ;
        }
    }
}
