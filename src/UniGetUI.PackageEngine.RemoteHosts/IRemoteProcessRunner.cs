namespace UniGetUI.PackageEngine.RemoteHosts;

public sealed record RemoteProcessResult(int ExitCode, string StdOut, string StdErr);

public interface IRemoteProcessRunner
{
    Task<RemoteProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default
    );
}

public sealed class SystemRemoteProcessRunner : IRemoteProcessRunner
{
    public async Task<RemoteProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default
    )
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = fileName;
        foreach (string argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        var stdoutTcs = new TaskCompletionSource();
        var stderrTcs = new TaskCompletionSource();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutTcs.TrySetResult();
                return;
            }
            stdout.AppendLine(e.Data);
            onOutput?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrTcs.TrySetResult();
                return;
            }
            stderr.AppendLine(e.Data);
            onOutput?.Invoke(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using (cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* process already exited */ }
        }))
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTcs.Task, stderrTcs.Task).ConfigureAwait(false);
        }

        return new RemoteProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
