namespace UniGetUI.PackageEngine.RemoteHosts;

public sealed class SshPosixTransport : IRemotePosixTransport
{
    private readonly IRemoteProcessRunner _runner;
    private readonly string _sshExecutable;

    public SshPosixTransport(IRemoteProcessRunner? runner = null, string? sshExecutable = null)
    {
        _runner = runner ?? new SystemRemoteProcessRunner();
        _sshExecutable = sshExecutable ?? RemoteSshClient.ResolveSshExecutable();
    }

    public async Task<RemoteProcessResult> RunAsync(
        RemoteHost host,
        string posixCommand,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            List<string> arguments = [.. RemoteSshClient.BuildBaseArguments(host.Destination), posixCommand];
            return await _runner.RunAsync(_sshExecutable, arguments, onProgress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new RemoteSshException(RemoteSshErrorKind.SshClientMissing, host.Destination);
        }
        catch (FileNotFoundException)
        {
            throw new RemoteSshException(RemoteSshErrorKind.SshClientMissing, host.Destination);
        }
    }
}
