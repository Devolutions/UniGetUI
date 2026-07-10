using System.Diagnostics;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.PackageOperations;

public class KillProcessOperation : AbstractOperation
{
    private readonly string ProcessName;

    public KillProcessOperation(string procName)
        : base(false)
    {
        ProcessName = CoreTools.MakeValidFileName(procName);
        Metadata.Status = $"Closing process(es) {procName}";
        Metadata.Title = $"Closing process(es) {procName}";
        Metadata.OperationInformation = " ";
        Metadata.SuccessTitle = "Done!";
        Metadata.SuccessMessage = "Done!";
        Metadata.FailureTitle = "Failed to close process";
        Metadata.FailureMessage = $"The process(es) {procName} could not be closed";
    }

    protected override void ApplyRetryAction(string retryMode) { }

    protected override async Task<OperationVeredict> PerformOperation()
    {
        try
        {
            Line(
                $"Attempting to close all processes with name {ProcessName}...",
                LineType.Information
            );
            foreach (var proc in Process.GetProcessesByName(ProcessName.Replace(".exe", "")))
            {
                using (proc)
                {
                    CancellationToken.ThrowIfCancellationRequested();
                    if (proc.HasExited)
                        continue;
                    Line(
                        $"Attempting to close process {ProcessName} with pid={proc.Id}...",
                        LineType.VerboseDetails
                    );
                    proc.CloseMainWindow();
                    await Task.WhenAny(
                        proc.WaitForExitAsync(CancellationToken),
                        Task.Delay(1000, CancellationToken)
                    );
                    CancellationToken.ThrowIfCancellationRequested();
                    if (!proc.HasExited)
                    {
                        if (Settings.Get(Settings.K.KillProcessesThatRefuseToDie))
                        {
                            Line(
                                $"Timeout for process {ProcessName}, attempting to kill...",
                                LineType.Information
                            );
                            proc.Kill(entireProcessTree: true);
                            await proc.WaitForExitAsync(CancellationToken);
                        }
                        else
                        {
                            Line(
                                $"{ProcessName} with pid={proc.Id} did not exit and will not be killed. You can change this from UniGetUI settings.",
                                LineType.Error
                            );
                        }
                    }
                }
            }

            return OperationVeredict.Success;
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            return OperationVeredict.Canceled;
        }
        catch (Exception ex)
        {
            Line(ex.ToString(), LineType.Error);
            return OperationVeredict.Failure;
        }
    }

    public override Task<Uri> GetOperationIcon() => Task.FromResult(new Uri("about:blank"));
}
