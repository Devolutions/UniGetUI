using System;
using System.Threading.Tasks;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageOperations;

namespace UniGetUI.Tui.Infrastructure;

/// <summary>
/// A fake operation used when <c>UNIGETUI_TUI_SIMULATE=1</c> is set. It integrates with the real
/// <see cref="AbstractOperation"/> pipeline (status transitions, <see cref="AbstractOperation.MainThread"/>,
/// log streaming, <see cref="TuiOperationRegistry"/>) so the Operations page renders it exactly like a
/// real install/update/uninstall — but it spawns NO process and changes NOTHING on the system. It streams
/// a handful of fake log lines with small delays and then succeeds (or fails when
/// <c>UNIGETUI_TUI_DEBUG_OP=fail</c>). This is the only safe way to exercise the i/u/install paths in tests.
/// </summary>
internal sealed class SimulatedOperation : AbstractOperation
{
    private readonly bool _shouldFail;
    private readonly string _verb;
    private readonly string _packageName;
    private readonly Random _rng = new();

    /// <summary>True when package operations should be faked instead of really executed.</summary>
    public static bool IsEnabled
        => Environment.GetEnvironmentVariable("UNIGETUI_TUI_SIMULATE") == "1";

    /// <summary>True when a faked operation should report failure (for testing the failure path).</summary>
    public static bool ShouldFail
        => Environment.GetEnvironmentVariable("UNIGETUI_TUI_DEBUG_OP") == "fail";

    public SimulatedOperation(string verb, string packageName)
        : base(queue_enabled: false)
    {
        _verb = string.IsNullOrWhiteSpace(verb) ? "Process" : verb;
        _packageName = string.IsNullOrWhiteSpace(packageName) ? "package" : packageName;
        _shouldFail = ShouldFail;

        string lowerVerb = _verb.ToLowerInvariant();
        Metadata.Title = $"[SIMULATION] {_verb} {_packageName}";
        Metadata.Status = $"[SIMULATION] {_verb} of {_packageName} in progress…";
        Metadata.OperationInformation =
            $"Simulated {lowerVerb} operation — no process is spawned and nothing is changed on the system. "
            + $"Package={_packageName}";
        Metadata.SuccessTitle = $"[SIMULATION] {_packageName} {lowerVerb} complete";
        Metadata.SuccessMessage = $"[SIMULATION] {_verb} of {_packageName} completed successfully.";
        Metadata.FailureTitle = $"[SIMULATION] {_packageName} {lowerVerb} failed";
        Metadata.FailureMessage = $"[SIMULATION] {_verb} of {_packageName} failed.";
    }

    protected override async Task<OperationVeredict> PerformOperation()
    {
        string[] steps =
        {
            $"[SIMULATION] Resolving {_packageName}…",
            "[SIMULATION] Contacting source (no network call performed)…",
            "[SIMULATION] Verifying package metadata…",
            "[SIMULATION] Downloading package (simulated 0 bytes)…",
            "[SIMULATION] Verifying integrity hash (skipped)…",
            $"[SIMULATION] Running {_verb.ToLowerInvariant()} step 1/3…",
            $"[SIMULATION] Running {_verb.ToLowerInvariant()} step 2/3…",
            $"[SIMULATION] Running {_verb.ToLowerInvariant()} step 3/3…",
            "[SIMULATION] Updating local package index…",
            "[SIMULATION] Cleaning up temporary files…",
        };

        foreach (string step in steps)
        {
            if (Status is OperationStatus.Canceled)
                return OperationVeredict.Canceled;

            Line(step, LineType.Information);
            await Task.Delay(_rng.Next(200, 401));
        }

        if (Status is OperationStatus.Canceled)
            return OperationVeredict.Canceled;

        if (_shouldFail)
        {
            Line($"[SIMULATION] {_verb} of {_packageName} failed (forced by UNIGETUI_TUI_DEBUG_OP=fail).",
                LineType.Error);
            return OperationVeredict.Failure;
        }

        Line($"[SIMULATION] {_verb} of {_packageName} succeeded.", LineType.Information);
        return OperationVeredict.Success;
    }

    protected override void ApplyRetryAction(string retryMode)
    {
        // Nothing to reconfigure for a simulated operation.
    }

    public override Task<Uri> GetOperationIcon() => Task.FromResult(new Uri("about:blank"));
}
