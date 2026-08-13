namespace UniGetUI.PackageEngine.RemoteHosts;

public sealed class RemoteSshClient
{
    public const int ProtocolVersion = RemoteControlProtocol.Version;

    private readonly IRemotePosixTransport _transport;

    public RemoteSshClient(IRemoteProcessRunner? runner = null, string? sshExecutable = null)
        : this(new SshPosixTransport(runner, sshExecutable))
    {
    }

    public RemoteSshClient(IRemotePosixTransport transport)
    {
        _transport = transport;
    }

    public static RemoteSshClient ForHost(RemoteHost host, IRemoteProcessRunner? runner = null)
    {
        if (host.Kind == RemoteHostKind.Wsl)
            return new RemoteSshClient(new WslLaunchTransport());
        return new RemoteSshClient(runner);
    }

    public static string ResolveSshExecutable()
        => OperatingSystem.IsWindows() ? "ssh.exe" : "ssh";

    public static IReadOnlyList<string> BuildBaseArguments(string destination)
    {
        return
        [
            "-T",
            "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=yes",
            "-o", "ConnectTimeout=10",
            "-o", "ServerAliveInterval=5",
            "-o", "ServerAliveCountMax=3",
            "--",
            destination,
        ];
    }

    public static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    public IReadOnlyList<string> BuildArguments(RemoteHost host, string remoteCommand)
        => [.. BuildBaseArguments(host.Destination), remoteCommand];

    public static string BuildPosixDispatchCommand(IReadOnlyList<string> agentArguments, string linuxScript)
    {
        string agentCommand = string.Join(" ", agentArguments.Select(ShellQuote));
        return "if command -v unigetui >/dev/null 2>&1; then exec unigetui "
            + agentCommand
            + "; elif command -v UniGetUI >/dev/null 2>&1; then exec UniGetUI "
            + agentCommand
            + "; elif [ \"$(uname -s 2>/dev/null)\" = Linux ]; then /bin/sh -c "
            + ShellQuote(linuxScript)
            + "; else echo 'UniGetUI is not installed on this host.' >&2; exit 127; fi";
    }

    public static string BuildWindowsAgentCommand(IReadOnlyList<string> agentArguments)
        => "unigetui " + string.Join(" ", agentArguments.Select(QuoteForCmd));

    public static IReadOnlyList<string> AgentArguments(string verb, string? managerId = null, string? packageId = null)
    {
        List<string> args = ["remote", "--protocol", ProtocolVersion.ToString(), verb];
        if (!string.IsNullOrEmpty(managerId))
        {
            args.Add("--manager");
            args.Add(managerId);
        }
        if (!string.IsNullOrEmpty(packageId))
        {
            args.Add("--id");
            args.Add(packageId);
        }
        return args;
    }

    public async Task<RemoteControlResponse> ProbeAsync(
        RemoteHost host,
        CancellationToken cancellationToken = default
    )
    {
        RemoteProcessResult uname = await RunRemoteAsync(host, "uname -s", cancellationToken).ConfigureAwait(false);
        string osToken = uname.StdOut.Trim();
        if (uname.ExitCode == 0 && osToken.Equals("Linux", StringComparison.OrdinalIgnoreCase))
        {
            return await InventoryAsync(host, cancellationToken).ConfigureAwait(false);
        }

        if (uname.ExitCode == 0 && osToken.Equals("Darwin", StringComparison.OrdinalIgnoreCase))
        {
            return await RunAgentOrThrowAsync(host, AgentArguments("hello"), LinuxAgentless.InventoryScript, cancellationToken)
                .ConfigureAwait(false);
        }

        return await RunWindowsAgentOrThrowAsync(host, AgentArguments("hello"), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RemoteControlResponse> InventoryAsync(
        RemoteHost host,
        CancellationToken cancellationToken = default
    )
    {
        RemoteProcessResult uname = await RunRemoteAsync(host, "uname -s", cancellationToken).ConfigureAwait(false);
        if (uname.ExitCode == 0 && uname.StdOut.Trim().Equals("Linux", StringComparison.OrdinalIgnoreCase))
        {
            return await RunPosixAsync(host, AgentArguments("inventory"), LinuxAgentless.InventoryScript, cancellationToken)
                .ConfigureAwait(false);
        }

        if (uname.ExitCode == 0 && uname.StdOut.Trim().Equals("Darwin", StringComparison.OrdinalIgnoreCase))
        {
            return await RunAgentOrThrowAsync(host, AgentArguments("inventory"), LinuxAgentless.InventoryScript, cancellationToken)
                .ConfigureAwait(false);
        }

        return await RunWindowsAgentOrThrowAsync(host, AgentArguments("inventory"), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RemoteControlResponse> UpdateAsync(
        RemoteHost host,
        string managerId,
        string packageId,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default
    )
    {
        return await MutateAsync(
            host,
            AgentArguments("update", managerId, packageId),
            LinuxAgentless.ActionScript("update", managerId, packageId),
            onProgress,
            cancellationToken
        ).ConfigureAwait(false);
    }

    public async Task<RemoteControlResponse> UninstallAsync(
        RemoteHost host,
        string managerId,
        string packageId,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default
    )
    {
        return await MutateAsync(
            host,
            AgentArguments("uninstall", managerId, packageId),
            LinuxAgentless.ActionScript("uninstall", managerId, packageId),
            onProgress,
            cancellationToken
        ).ConfigureAwait(false);
    }

    public async Task<RemoteControlResponse> UpdateAllAsync(
        RemoteHost host,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default
    )
    {
        return await MutateAsync(
            host,
            AgentArguments("update-all"),
            LinuxAgentless.UpdateAllScript,
            onProgress,
            cancellationToken
        ).ConfigureAwait(false);
    }

    public async Task<RemoteControlResponse> SearchAsync(
        RemoteHost host,
        string query,
        CancellationToken cancellationToken = default
    )
    {
        IReadOnlyList<string> args = ["remote", "--protocol", ProtocolVersion.ToString(), "search", "--query", query];
        RemoteProcessResult uname = await RunRemoteAsync(host, "uname -s", cancellationToken).ConfigureAwait(false);
        if (uname.ExitCode == 0 && (
            uname.StdOut.Trim().Equals("Linux", StringComparison.OrdinalIgnoreCase)
            || uname.StdOut.Trim().Equals("Darwin", StringComparison.OrdinalIgnoreCase)))
        {
            return await RunAgentOrThrowAsync(host, args, "echo 'Remote search requires UniGetUI on this host.' >&2; exit 64", cancellationToken)
                .ConfigureAwait(false);
        }

        return await RunWindowsAgentOrThrowAsync(host, args, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RemoteControlResponse> MutateAsync(
        RemoteHost host,
        IReadOnlyList<string> agentArguments,
        string linuxScript,
        Action<string>? onProgress,
        CancellationToken cancellationToken
    )
    {
        RemoteProcessResult uname = await RunRemoteAsync(host, "uname -s", cancellationToken, onProgress).ConfigureAwait(false);
        RemoteControlResponse response;
        if (uname.ExitCode == 0 && uname.StdOut.Trim().Equals("Linux", StringComparison.OrdinalIgnoreCase))
        {
            response = await RunPosixAsync(host, agentArguments, linuxScript, cancellationToken, onProgress)
                .ConfigureAwait(false);
        }
        else if (uname.ExitCode == 0 && uname.StdOut.Trim().Equals("Darwin", StringComparison.OrdinalIgnoreCase))
        {
            response = await RunAgentOrThrowAsync(host, agentArguments, linuxScript, cancellationToken, onProgress)
                .ConfigureAwait(false);
        }
        else
        {
            response = await RunWindowsAgentOrThrowAsync(host, agentArguments, cancellationToken, onProgress)
                .ConfigureAwait(false);
        }

        if (response.Packages.Count == 0 && response.Ok)
            return await InventoryAsync(host, cancellationToken).ConfigureAwait(false);

        return response;
    }

    private async Task<RemoteControlResponse> RunPosixAsync(
        RemoteHost host,
        IReadOnlyList<string> agentArguments,
        string linuxScript,
        CancellationToken cancellationToken,
        Action<string>? onProgress = null
    )
    {
        string command = BuildPosixDispatchCommand(agentArguments, linuxScript);
        RemoteProcessResult result = await RunRemoteAsync(host, command, cancellationToken, onProgress).ConfigureAwait(false);
        return DecodeOrThrow(host, result);
    }

    private async Task<RemoteControlResponse> RunAgentOrThrowAsync(
        RemoteHost host,
        IReadOnlyList<string> agentArguments,
        string linuxScript,
        CancellationToken cancellationToken,
        Action<string>? onProgress = null
    )
    {
        string command = BuildPosixDispatchCommand(agentArguments, linuxScript);
        RemoteProcessResult result = await RunRemoteAsync(host, command, cancellationToken, onProgress).ConfigureAwait(false);
        RemoteControlResponse response = DecodeOrThrow(host, result);
        if (response.BackendKind == RemoteBackendKind.LinuxAgentless)
            throw new RemoteSshException(RemoteSshErrorKind.MissingRemoteAgent, host.Destination);
        return response;
    }

    private async Task<RemoteControlResponse> RunWindowsAgentOrThrowAsync(
        RemoteHost host,
        IReadOnlyList<string> agentArguments,
        CancellationToken cancellationToken,
        Action<string>? onProgress = null
    )
    {
        string command = BuildWindowsAgentCommand(agentArguments);
        RemoteProcessResult result = await RunRemoteAsync(host, command, cancellationToken, onProgress).ConfigureAwait(false);
        return DecodeOrThrow(host, result);
    }

    private Task<RemoteProcessResult> RunRemoteAsync(
        RemoteHost host,
        string remoteCommand,
        CancellationToken cancellationToken,
        Action<string>? onProgress = null
    )
        => _transport.RunAsync(host, remoteCommand, onProgress, cancellationToken);

    internal static RemoteControlResponse DecodeOrThrow(RemoteHost host, RemoteProcessResult result)
    {
        RemoteControlResponse? json = ExtractJsonResponse(result.StdOut);
        if (json is not null)
        {
            if (json.Protocol != ProtocolVersion)
                throw new RemoteSshException(RemoteSshErrorKind.IncompatibleProtocol, host.Destination);
            return json;
        }

        if (LinuxAgentless.TryParseInventory(result.StdOut, out RemoteControlResponse? linux) && linux is not null)
            return linux;

        if (result.ExitCode == 0 && result.StdOut.Contains(RemoteControlProtocol.LinuxActionOkMarker, StringComparison.Ordinal))
        {
            return new RemoteControlResponse { Ok = true, Backend = "linux-agentless" };
        }

        throw MapError(host, result);
    }

    internal static RemoteControlResponse? ExtractJsonResponse(string stdout)
    {
        string trimmed = stdout.Trim();
        int start = trimmed.IndexOf('{');
        int end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        return RemoteHostsJson.DeserializeResponse(trimmed[start..(end + 1)]);
    }

    internal static RemoteSshException MapError(RemoteHost host, RemoteProcessResult result)
    {
        string output = (result.StdErr + "\n" + result.StdOut).ToLowerInvariant();
        if (output.Contains("host key verification failed")
            || output.Contains("no host key is known")
            || output.Contains("remote host identification has changed"))
        {
            return new RemoteSshException(RemoteSshErrorKind.UntrustedHost, host.Destination);
        }

        if (result.ExitCode == 255 && (output.Contains("permission denied") || output.Contains("authentication failed")))
            return new RemoteSshException(RemoteSshErrorKind.AuthenticationFailed, host.Destination);

        if (output.Contains("unigetui is not installed") || result.ExitCode == 127)
            return new RemoteSshException(RemoteSshErrorKind.MissingRemoteAgent, host.Destination);

        string detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut.Trim() : result.StdErr.Trim();
        return result.ExitCode == 255
            ? new RemoteSshException(RemoteSshErrorKind.ConnectionFailed, host.Destination, detail)
            : new RemoteSshException(RemoteSshErrorKind.RemoteCommandFailed, host.Destination, detail);
    }

    private static string QuoteForCmd(string value)
    {
        if (value.Length == 0 || value.Any(char.IsWhiteSpace))
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        return value;
    }
}
