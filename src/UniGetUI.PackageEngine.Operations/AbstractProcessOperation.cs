using System.Diagnostics;
using System.Text;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.PackageOperations;

public abstract class AbstractProcessOperation : AbstractOperation
{
    protected Process process { get; private set; }
    private bool ProcessKilled;

    protected AbstractProcessOperation(
        bool queue_enabled,
        IReadOnlyList<InnerOperation>? preOps = null,
        IReadOnlyList<InnerOperation>? postOps = null
    )
        : base(queue_enabled, preOps, postOps)
    {
        process = new();
        CancelRequested += (_, _) => StopProcess();
        OperationStarting += (_, _) =>
        {
            DisposeProcess();
            ProcessKilled = false;
            process = new();
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            process.StartInfo.StandardInputEncoding = Encoding.UTF8;
            process.StartInfo.WorkingDirectory = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile
            );
            process.StartInfo.FileName = "lol";
            process.StartInfo.Arguments = "lol";
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;
                string line = e.Data.Trim();
                var lineType = LineType.Error;
                if (line.Length < 6 || line.Contains("Waiting for another install..."))
                    lineType = LineType.ProgressIndicator;

                Line(line, lineType);
            };
            PrepareProcessStartInfo();
        };
    }

    private bool _requiresUACCache;

    protected void RequestCachingOfUACPrompt()
    {
        _requiresUACCache = true;
    }

    protected void RedirectWinGetTempFolder()
    {
        string WinGetTemp = Path.Join(Path.GetTempPath(), "UniGetUI", "ElevatedWinGetTemp");
        process.StartInfo.Environment["TEMP"] = WinGetTemp;
        process.StartInfo.Environment["TMP"] = WinGetTemp;
    }

    protected override async Task<OperationVeredict> PerformOperation()
    {
        if (process.StartInfo.UseShellExecute)
            throw new InvalidOperationException("UseShellExecute must be set to false");
        if (!process.StartInfo.RedirectStandardOutput)
            throw new InvalidOperationException("RedirectStandardOutput must be set to true");
        if (!process.StartInfo.RedirectStandardInput)
            throw new InvalidOperationException("RedirectStandardInput must be set to true");
        if (!process.StartInfo.RedirectStandardError)
            throw new InvalidOperationException("RedirectStandardError must be set to true");
        if (process.StartInfo.FileName == "lol")
            throw new InvalidOperationException("StartInfo.FileName has not been set");
        if (process.StartInfo.Arguments == "lol")
            throw new InvalidOperationException("StartInfo.Arguments has not been set");

        Line("Executing process with StartInfo:", LineType.VerboseDetails);
        Line($" - FileName: \"{process.StartInfo.FileName.Trim()}\"", LineType.VerboseDetails);
        Line($" - Arguments: \"{process.StartInfo.Arguments.Trim()}\"", LineType.VerboseDetails);
        Line($"Start Time: \"{DateTime.Now}\"", LineType.VerboseDetails);

        if (string.IsNullOrWhiteSpace(process.StartInfo.FileName))
        {
            Line(
                CoreTools.Translate(
                    "This operation requires administrator privileges, but the elevation tool could not be found. The operation cannot continue."
                ),
                LineType.Error
            );
            return OperationVeredict.Failure;
        }

        if (_requiresUACCache)
        {
            _requiresUACCache = false;
            await CoreTools.CacheUACForCurrentProcess();
        }

        CancellationToken.ThrowIfCancellationRequested();
        process.Start();
        if (CancellationToken.IsCancellationRequested)
        {
            StopProcess();
            await process.WaitForExitAsync().ConfigureAwait(false);
            return OperationVeredict.Canceled;
        }

        if (!Settings.Get(Settings.K.DisableNewProcessLineHandler))
        {
            await process.StandardInput.WriteLineAsync("\r\n\r\n\r\n\r\n".AsMemory(), CancellationToken);
            process.StandardInput.Close();
        }
        try
        {
            process.BeginErrorReadLine();
        }
        catch (InvalidOperationException ex)
        {
            Logger.Error(ex);
        }

        StringBuilder currentLine = new();
        char[] buffer = new char[1];
        string? lastStringBeforeLF = null;
        try
        {
            while ((await process.StandardOutput.ReadAsync(buffer.AsMemory(), CancellationToken)) > 0)
            {
                char c = buffer[0];
                if (c == 10)
                {
                    if (currentLine.Length == 0)
                    {
                        if (lastStringBeforeLF is not null)
                        {
                            Line(lastStringBeforeLF, LineType.Information);
                            lastStringBeforeLF = null;
                        }
                        continue;
                    }

                    string line = currentLine.ToString();
                    Line(line, LineType.Information);
                    currentLine.Clear();
                }
                else if (c == 13)
                {
                    if (currentLine.Length == 0)
                        continue;
                    lastStringBeforeLF = currentLine.ToString();
                    Line(lastStringBeforeLF, LineType.ProgressIndicator);
                    currentLine.Clear();
                }
                else
                {
                    currentLine.Append(c);
                }
            }
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return OperationVeredict.Canceled;
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return OperationVeredict.Canceled;
        }

        Line($"End Time: \"{DateTime.Now}\"", LineType.VerboseDetails);
        Line(
            $"Process return value: \"{process.ExitCode}\" (0x{process.ExitCode:X})",
            LineType.VerboseDetails
        );

        if (ProcessKilled || CancellationToken.IsCancellationRequested)
            return OperationVeredict.Canceled;

        List<string> output = new();
        foreach (var line in GetRawOutput())
        {
            if (line.Item2 is LineType.VerboseDetails && line.Item1 == "-----------------------")
                output.Clear();
            if (line.Item2 is LineType.Error or LineType.Information)
                output.Add(line.Item1);
        }

        return await GetProcessVeredict(process.ExitCode, output);
    }

    protected override void OnRunCompleted()
    {
        DisposeProcess();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            DisposeProcess();
    }

    private void StopProcess()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                ProcessKilled = true;
            }
        }
        catch (InvalidOperationException) { }
    }

    private void DisposeProcess()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                ProcessKilled = true;
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException) { }
        finally
        {
            process.Dispose();
        }
    }

    protected abstract Task<OperationVeredict> GetProcessVeredict(
        int ReturnCode,
        List<string> Output
    );
    protected abstract void PrepareProcessStartInfo();
}
