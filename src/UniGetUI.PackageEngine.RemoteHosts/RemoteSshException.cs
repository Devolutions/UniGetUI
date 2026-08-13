namespace UniGetUI.PackageEngine.RemoteHosts;

public enum RemoteSshErrorKind
{
    AuthenticationFailed,
    ConnectionFailed,
    IncompatibleProtocol,
    MissingRemoteAgent,
    RemoteCommandFailed,
    UntrustedHost,
    UnsupportedDistro,
    SshClientMissing,
    WslNotAvailable,
    WslDistroNotFound,
    WslLaunchFailed,
}

public sealed class RemoteSshException : Exception
{
    public RemoteSshErrorKind Kind { get; }
    public string Destination { get; }

    public RemoteSshException(RemoteSshErrorKind kind, string destination, string? detail = null)
        : base(BuildMessage(kind, destination, detail))
    {
        Kind = kind;
        Destination = destination;
    }

    private static string BuildMessage(RemoteSshErrorKind kind, string destination, string? detail)
    {
        return kind switch
        {
            RemoteSshErrorKind.AuthenticationFailed =>
                $"SSH authentication failed for {destination}. Configure key or agent authentication first.",
            RemoteSshErrorKind.UntrustedHost =>
                $"The SSH host key for {destination} is not trusted. Connect with ssh in a terminal once, verify the key, and try again.",
            RemoteSshErrorKind.MissingRemoteAgent =>
                $"UniGetUI was not found on {destination}. Install UniGetUI on Windows and macOS remotes, or use a Linux host for agentless inventory.",
            RemoteSshErrorKind.IncompatibleProtocol =>
                $"Update UniGetUI on {destination}. The remote agent protocol is incompatible.",
            RemoteSshErrorKind.UnsupportedDistro =>
                $"No supported Linux package manager was found on {destination}.",
            RemoteSshErrorKind.SshClientMissing =>
                "OpenSSH (ssh) was not found on this computer. Install the OpenSSH client and try again.",
            RemoteSshErrorKind.WslNotAvailable =>
                "Windows Subsystem for Linux is not available on this computer.",
            RemoteSshErrorKind.WslDistroNotFound =>
                $"The WSL distribution {destination} is not registered.",
            RemoteSshErrorKind.WslLaunchFailed =>
                string.IsNullOrWhiteSpace(detail)
                    ? $"Could not start a process in the WSL distribution {destination}."
                    : $"Could not start a process in the WSL distribution {destination}: {detail}",
            RemoteSshErrorKind.ConnectionFailed =>
                string.IsNullOrWhiteSpace(detail)
                    ? $"Could not connect to {destination} over SSH."
                    : $"Could not connect to {destination}: {detail}",
            _ =>
                string.IsNullOrWhiteSpace(detail)
                    ? $"The command failed on {destination}."
                    : $"The command failed on {destination}: {detail}",
        };
    }
}
