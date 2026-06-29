using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageOperations;

namespace UniGetUI.Tui.Infrastructure;

/// <summary>
/// Process-global registry of operations for the TUI shell. It owns the list of operations shown on
/// the Operations page, enforces the engine's "register before run" contract via <see cref="Start"/>,
/// and marshals the engine's background-thread events onto the UI thread.
///
/// Threading contract: the backing list is only ever mutated on the Avalonia UI thread. Public
/// mutators self-marshal if called from elsewhere. Background-thread operation events are coalesced
/// into a single <see cref="Changed"/> invocation on the UI thread.
/// </summary>
internal static class TuiOperationRegistry
{
    private static readonly List<AbstractOperation> _ops = new();

    /// <summary>Raised on the UI thread whenever the set of operations or any operation's status changes.</summary>
    public static event Action? Changed;

    /// <summary>A UI-thread snapshot of the tracked operations, newest last.</summary>
    public static IReadOnlyList<AbstractOperation> Snapshot() => _ops.ToArray();

    /// <summary>
    /// Registers an operation and starts it. This is the only supported entry point: it guarantees the
    /// registry has subscribed to the operation's events <em>before</em> <see cref="AbstractOperation.MainThread"/>
    /// runs, so no early status change or completion is missed even for operations that finish instantly.
    /// </summary>
    public static void Start(AbstractOperation op)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Start(op));
            return;
        }

        if (!_ops.Contains(op))
            _ops.Add(op);

        op.StatusChanged += OnOpStatusChanged;
        op.OperationFinished += OnOpFinished;
        RaiseChanged();

        // Run the operation off the UI thread: real operations do synchronous setup inside
        // MainThread() and their awaits must not resume on the UI dispatcher context.
        _ = Task.Run(() => RunObservedAsync(op));
    }

    public static void Remove(AbstractOperation op)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Remove(op));
            return;
        }

        op.StatusChanged -= OnOpStatusChanged;
        op.OperationFinished -= OnOpFinished;
        _ops.Remove(op);
        RaiseChanged();
    }

    /// <summary>Removes every finished (succeeded/failed/canceled) operation. Running/queued ops are kept.</summary>
    public static void ClearFinished()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ClearFinished);
            return;
        }

        foreach (var op in _ops
                     .Where(o => o.Status is OperationStatus.Succeeded
                         or OperationStatus.Failed
                         or OperationStatus.Canceled)
                     .ToList())
        {
            Remove(op);
        }
    }

    /// <summary>Requests cancellation of every running or queued operation.</summary>
    public static void CancelAll()
    {
        foreach (var op in Snapshot()
                     .Where(o => o.Status is OperationStatus.Running or OperationStatus.InQueue))
        {
            op.Cancel();
        }
    }

    private static void OnOpStatusChanged(object? sender, OperationStatus status) => RaiseChanged();
    private static void OnOpFinished(object? sender, EventArgs e) => RaiseChanged();

    private static async Task RunObservedAsync(AbstractOperation op)
    {
        // MainThread() normally funnels failures into OperationFailed, but observe the task anyway so
        // an unexpected engine-level exception surfaces in the log instead of vanishing.
        try
        {
            await op.MainThread();
        }
        catch (Exception ex)
        {
            Logger.Error("A TUI operation crashed unexpectedly:");
            Logger.Error(ex);
        }
    }

    // Coalesce bursts of background-thread events into a single UI-thread Changed invocation while
    // guaranteeing at least one trailing invocation (CompareExchange gate cleared inside the post).
    private static int _changePosted;

    private static void RaiseChanged()
    {
        if (Interlocked.CompareExchange(ref _changePosted, 1, 0) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _changePosted, 0);
            Changed?.Invoke();
        });
    }
}
