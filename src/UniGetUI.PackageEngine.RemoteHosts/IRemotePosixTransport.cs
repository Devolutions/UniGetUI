namespace UniGetUI.PackageEngine.RemoteHosts;

public interface IRemotePosixTransport
{
    Task<RemoteProcessResult> RunAsync(
        RemoteHost host,
        string posixCommand,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default
    );
}
