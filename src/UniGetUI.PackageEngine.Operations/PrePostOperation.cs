using System.Diagnostics;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageOperations;

namespace UniGetUI.PackageEngine.Operations;

public class PrePostOperation : AbstractOperation
{
    private readonly string Payload;
    private readonly object ProcessLock = new();
    private Process? ActiveProcess;

    public PrePostOperation(string payload)
        : base(true)
    {
        Payload = payload.Replace("\r", "\n").Replace("\n\n", "\n").Replace("\n", "&");
        Metadata.Status = $"Running custom operation {Payload}";
        Metadata.Title = "Custom operation";
        Metadata.OperationInformation = " ";
        Metadata.SuccessTitle = "Done!";
        Metadata.SuccessMessage = "Done!";
        Metadata.FailureTitle = "Custom operation failed";
        Metadata.FailureMessage = $"The custom operation {Payload} failed to run";
        CancelRequested += (_, _) => StopActiveProcess();
    }

    protected override void ApplyRetryAction(string retryMode) { }

    protected override async Task<OperationVeredict> PerformOperation()
    {
        Line($"Running command {Payload}", LineType.Information);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/C {Payload}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        DataReceivedEventHandler outputHandler = (_, e) =>
        {
            if (e.Data is not null)
                Line(e.Data, LineType.Information);
        };
        DataReceivedEventHandler errorHandler = (_, e) =>
        {
            if (e.Data is not null)
                Line(e.Data, LineType.Error);
        };
        process.OutputDataReceived += outputHandler;
        process.ErrorDataReceived += errorHandler;
        lock (ProcessLock)
            ActiveProcess = process;
        bool processStarted = false;

        try
        {
            CancellationToken.ThrowIfCancellationRequested();
            process.Start();
            processStarted = true;
            if (CancellationToken.IsCancellationRequested)
                StopActiveProcess();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            try
            {
                await process.WaitForExitAsync(CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                return OperationVeredict.Canceled;
            }

            if (CancellationToken.IsCancellationRequested)
                return OperationVeredict.Canceled;

            int exitCode = process.ExitCode;
            Line($"Exit code is {exitCode}", LineType.Information);
            return exitCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;
        }
        finally
        {
            if (processStarted && !process.HasExited)
            {
                StopActiveProcess();
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            process.OutputDataReceived -= outputHandler;
            process.ErrorDataReceived -= errorHandler;
            lock (ProcessLock)
            {
                if (ReferenceEquals(ActiveProcess, process))
                    ActiveProcess = null;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            StopActiveProcess();
    }

    private void StopActiveProcess()
    {
        Process? process;
        lock (ProcessLock)
            process = ActiveProcess;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }

    public override Task<Uri> GetOperationIcon() => Task.FromResult(new Uri("about:blank"));
}
