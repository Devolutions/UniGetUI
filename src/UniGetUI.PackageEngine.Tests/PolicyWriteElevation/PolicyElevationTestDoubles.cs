#if WINDOWS
using System.IO.Pipes;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

/// <summary>
/// Test doubles for the elevation pipeline.
/// </summary>
/// <remarks>
/// Only the parts that genuinely require an administrator token are faked: raising the consent
/// prompt, holding a real elevated process handle, and the kernel/Authenticode peer checks. The
/// pipe itself is real — the tests run a non-elevated client against the very same
/// <see cref="PolicyElevationPipeServer"/> the product uses, so the framing, the single-connection
/// rule and every failure classification are exercised for real. None of these doubles exist in
/// the shipping composition root.
/// </remarks>
internal sealed class FakeTrustVerifier : IPolicyElevationTrustVerifier
{
    /// <summary>An arbitrary, well-formed signer digest standing in for "the release signer".</summary>
    public const string DefaultSigner =
        "1111111111111111111111111111111111111111111111111111111111111111";

    /// <summary>A second well-formed digest standing in for "some other publisher".</summary>
    public const string OtherSigner =
        "2222222222222222222222222222222222222222222222222222222222222222";

    private readonly Func<string, PolicyElevationTrustResult> _resolve;

    public FakeTrustVerifier(Func<string, PolicyElevationTrustResult>? resolve = null)
        => _resolve = resolve ?? (_ => PolicyElevationTrustResult.Signed(DefaultSigner));

    public string? LastVerifiedPath { get; private set; }

    public List<string> VerifiedPaths { get; } = [];

    /// <summary>Every binary is signed by the same publisher: the normal, healthy release.</summary>
    public static FakeTrustVerifier SameSigner() => new();

    /// <summary>Everything is signed, but <paramref name="oddOneOut"/> by a different publisher.</summary>
    public static FakeTrustVerifier DifferentSignerFor(string oddOneOut)
        => new(path => PolicyElevationTrustResult.Signed(
            string.Equals(path, oddOneOut, StringComparison.OrdinalIgnoreCase)
                ? OtherSigner
                : DefaultSigner));

    /// <summary><paramref name="unsigned"/> carries no usable signature at all.</summary>
    public static FakeTrustVerifier UnsignedFor(string unsigned)
        => new(path => string.Equals(path, unsigned, StringComparison.OrdinalIgnoreCase)
            ? PolicyElevationTrustResult.Rejected(
                "The binary is not validly signed.",
                $"No signature on '{path}'.")
            : PolicyElevationTrustResult.Signed(DefaultSigner));

    /// <summary>Verification succeeds but yields no signer, which must still fail closed.</summary>
    public static FakeTrustVerifier TrustedWithoutSigner()
        => new(_ => new PolicyElevationTrustResult(true));

    public PolicyElevationTrustResult VerifyExecutable(string executablePath)
    {
        LastVerifiedPath = executablePath;
        VerifiedPaths.Add(executablePath);
        return _resolve(executablePath);
    }
}

internal sealed class FakeHelperLocator : IPolicyElevationHelperLocator
{    private readonly PolicyElevationHelperLocation _location;

    public FakeHelperLocator(PolicyElevationHelperLocation location) => _location = location;

    /// <summary>The install root the fake packaged layout pretends to live in.</summary>
    public static string PackagedRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "UniGetUI");

    public static string PackagedHostPath => PolicyElevationPaths.GetHostPath(PackagedRoot);

    public static string PackagedHelperPath => PolicyElevationPaths.GetHelperPath(PackagedRoot);

    public static FakeHelperLocator Found()
        => new(new PolicyElevationHelperLocation(
            true,
            PackagedHelperPath,
            PackagedHostPath,
            PackagedRoot,
            Verification: PolicyElevationLocationVerification.Verified(
                PackagedRoot,
                PackagedHelperPath,
                PackagedHostPath)));

    public PolicyElevationHelperLocation Locate() => _location;
}

/// <summary>
/// Stands in for the handle-based protected-location check so the surrounding discovery and
/// authentication logic can be exercised against paths that do not exist on the test machine.
/// The real rules themselves are covered by <c>PolicyElevationAccessPolicyTests</c> and by the
/// live <c>WindowsProtectedLocationVerifier</c> tests.
/// </summary>
internal sealed class FakeLocationVerifier : IPolicyElevationLocationVerifier
{
    private readonly Func<string, string, string, PolicyElevationLocationVerification> _resolve;

    private FakeLocationVerifier(Func<string, string, string, PolicyElevationLocationVerification> resolve)
        => _resolve = resolve;

    public int Invocations { get; private set; }

    /// <summary>The location verifies, and reports exactly the paths it was asked about.</summary>
    public static FakeLocationVerifier Accepting()
        => new(PolicyElevationLocationVerification.Verified);

    /// <summary>The location fails the handle-based check.</summary>
    public static FakeLocationVerifier Rejecting(string? detail = null)
        => new((_, _, _) => PolicyElevationLocationVerification.Rejected(
            "Elevated policy writes require UniGetUI to be installed in an administrator-protected location.",
            detail ?? @"'C:\Program Files\UniGetUI' is not administrator-protected."));

    /// <summary>
    /// The location verifies, but the kernel resolved the helper somewhere else — the path-swap
    /// case the handle-resolved comparison exists to catch.
    /// </summary>
    public static FakeLocationVerifier ResolvingHelperElsewhere(string actualHelperPath)
        => new((root, _, host) => PolicyElevationLocationVerification.Verified(root, actualHelperPath, host));

    public PolicyElevationLocationVerification Verify(string installRoot, string helperPath, string hostPath)
    {
        Invocations++;
        return _resolve(installRoot, helperPath, hostPath);
    }
}

internal sealed class FakePeerAuthenticator : IPolicyElevationPipePeerAuthenticator
{
    private readonly PolicyElevationPeerAuthenticationResult _result;

    public FakePeerAuthenticator(PolicyElevationPeerAuthenticationResult? result = null)
        => _result = result ?? PolicyElevationPeerAuthenticationResult.Authenticated;

    public bool ObservedConnectedPipe { get; private set; }

    public PolicyElevationPeerAuthenticationResult Authenticate(
        NamedPipeServerStream pipe,
        IElevatedHelperProcess helper,
        PolicyElevationHelperLocation location)
    {
        ObservedConnectedPipe = pipe.IsConnected;
        return _result;
    }
}

internal sealed class FakeHelperProcess : IElevatedHelperProcess
{
    private readonly TaskCompletionSource<int> _exited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondExitWaitStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _exitWaitCount;

    public uint ProcessId => 4242;

    public long CreationTimeUtcTicks => 638_000_000_000_000_000L;

    public uint SessionId => 1;

    public nint Handle => 1;

    public bool HasExited => _exited.Task.IsCompleted;

    public bool Disposed { get; private set; }

    public Task SecondExitWaitStarted => _secondExitWaitStarted.Task;

    public void Exit(int exitCode) => _exited.TrySetResult(exitCode);

    public async Task<int?> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _exitWaitCount) == 2)
            _secondExitWaitStarted.TrySetResult();

        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task delay = Task.Delay(timeout, delayCancellation.Token);

        Task completed = await Task.WhenAny(_exited.Task, delay).ConfigureAwait(false);
        if (completed == delay)
        {
            await delay.ConfigureAwait(false);
            return null;
        }

        await delayCancellation.CancelAsync().ConfigureAwait(false);
        return await _exited.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        Disposed = true;
        _exited.TrySetResult(0);
    }
}

internal sealed class FakeHelperLauncher : IElevatedHelperLauncher
{
    private readonly Func<PolicyElevationLaunchArguments, FakeHelperProcess, Task>? _script;
    private readonly ElevatedHelperLaunchResult? _failure;

    private FakeHelperLauncher(
        Func<PolicyElevationLaunchArguments, FakeHelperProcess, Task>? script,
        ElevatedHelperLaunchResult? failure)
    {
        _script = script;
        _failure = failure;
    }

    public string? LaunchedPath { get; private set; }

    public string? LaunchedArguments { get; private set; }

    public Task Completion { get; private set; } = Task.CompletedTask;

    public FakeHelperProcess? LastProcess { get; private set; }

    public static FakeHelperLauncher Running(Func<PolicyElevationLaunchArguments, FakeHelperProcess, Task> script)
        => new(script, null);

    public static FakeHelperLauncher Failing(ElevatedHelperLaunchResult failure)
        => new(null, failure);

    public Task<ElevatedHelperLaunchResult> LaunchAsync(
        string helperPath,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        LaunchedPath = helperPath;
        LaunchedArguments = arguments;

        if (_failure is not null)
        {
            return Task.FromResult(_failure);
        }

        if (!PolicyElevationLaunchArguments.TryParse(
                arguments.Split(' '),
                out PolicyElevationLaunchArguments? parsed,
                out string? error))
        {
            throw new InvalidOperationException($"The host produced an invalid command line: {error}");
        }

        var process = new FakeHelperProcess();
        LastProcess = process;

        Completion = Task.Run(
            async () =>
            {
                try
                {
                    await _script!(parsed, process).ConfigureAwait(false);
                }
                finally
                {
                    process.Exit(0);
                }
            },
            CancellationToken.None);

        return Task.FromResult(new ElevatedHelperLaunchResult(process));
    }
}

internal static class FakeHelperClient
{
    public static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, int timeoutMilliseconds = 15_000)
    {
        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            System.Security.Principal.TokenImpersonationLevel.Anonymous);

        try
        {
            await client.ConnectAsync(timeoutMilliseconds).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return client;
    }
}
#endif
