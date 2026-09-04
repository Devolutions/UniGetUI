#if WINDOWS
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Devolutions.Now.Policy.Api;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

/// <summary>
/// End-to-end behaviour of the elevation orchestrator over a real, non-elevated named pipe.
/// Every case asserts both the distinct outcome and that the caller's draft survives the attempt.
/// </summary>
public class WindowsPolicyWriteElevatorTests
{
    private const string DraftJson = """{"policy":{"rules":["allow"]},"version":3}""";

    private static readonly PolicyElevationTimeouts FastTimeouts =
        new(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5));
    private static readonly TimeSpan TestCloseGuardBound = TimeSpan.FromSeconds(2);

    private static PolicyElevationWriteRequest BuildRequest() => new(
        JsonDocument.Parse(DraftJson).RootElement)
    {
        Operation = PolicyElevationOperation.ReplaceIdentity,
        ConflictHandling = PolicyElevationConflictHandling.ConfirmOverwrite,
        ExpectedStoreToken = "store-token",
        ValidationReceipt = "validation-receipt",
        WarningsAcknowledged = true,
    };

    private static WindowsPolicyWriteElevator Build(
        IElevatedHelperLauncher launcher,
        IPolicyElevationHelperLocator? locator = null,
        IPolicyElevationTrustVerifier? trustVerifier = null,
        IPolicyElevationPipePeerAuthenticator? authenticator = null,
        PolicyElevationTimeouts? timeouts = null,
        Func<string?>? selfImagePathProvider = null)
        => new(
            locator ?? FakeHelperLocator.Found(),
            launcher,
            trustVerifier ?? FakeTrustVerifier.SameSigner(),
            authenticator ?? new FakePeerAuthenticator(),
            PolicyElevationPipeServer.Create,
            timeouts ?? FastTimeouts,
            selfImagePathProvider ?? (() => FakeHelperLocator.PackagedHostPath));

    private static void AssertDraftPreserved(PolicyElevationResult result)
    {
        Assert.Equal(
            JsonDocument.Parse(DraftJson).RootElement.GetRawText(),
            result.Draft.GetRawText());

        Assert.Equal("store-token", result.Request.ExpectedStoreToken);
        Assert.Equal("validation-receipt", result.Request.ValidationReceipt);
        Assert.True(result.Request.WarningsAcknowledged);
    }

    [Fact]
    public async Task SuccessfulExchange_ReturnsBoundedCommittedToken()
    {
        PolicyElevationRequestMessage? observed = null;

        FakeHelperLauncher launcher = FakeHelperLauncher.Running(async (arguments, process) =>
        {
            await using var client = await FakeHelperClient.ConnectAsync(arguments.PipeName);
            observed = await PolicyElevationFrame.ReadRequestAsync(client, CancellationToken.None);

            await PolicyElevationFrame.WriteResponseAsync(
                client,
                new PolicyElevationResponseMessage
                {
                    RequestId = observed.RequestId,
                    Disposition = PolicyElevationDisposition.Committed,
                    CommittedStoreToken = "new-token",
                },
                CancellationToken.None);
        });

        var authenticator = new FakePeerAuthenticator();
        PolicyElevationResult result = await Build(launcher, authenticator: authenticator)
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        await launcher.Completion;

        Assert.Equal(PolicyElevationOutcome.Replaced, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.Equal("new-token", result.CommittedStoreToken);
        Assert.Null(result.Response);
        Assert.Null(result.Payload);
        Assert.True(authenticator.ObservedConnectedPipe);
        AssertDraftPreserved(result);

        // The draft and every secret-bearing field travelled over the pipe, never on the argv.
        Assert.NotNull(observed);
        Assert.Equal(DraftJson, observed.Draft.GetRawText());
        Assert.Equal("store-token", observed.ExpectedStoreToken);
        Assert.DoesNotContain("store-token", launcher.LaunchedArguments);
        Assert.DoesNotContain("validation-receipt", launcher.LaunchedArguments);
        Assert.DoesNotContain("policy", launcher.LaunchedArguments);
    }

    [Fact]
    public async Task OversizedBrokerRequest_IsRejectedBeforeHelperLaunch()
    {
        PolicyElevationRequestMessage empty = new()
        {
            Draft = JsonDocument.Parse("""{"padding":""}""").RootElement.Clone(),
            ExpectedStoreToken = "token",
            ValidationReceipt = "receipt",
        };
        int paddingLength =
            BrokerApi.MaxPolicyManagementBodyBytes
            - PolicyElevationReplacementDispatcher.GetBrokerRequestBodyByteCount(empty)
            + 1;
        PolicyElevationWriteRequest overLimit = new(
            JsonDocument.Parse(
                $$"""{"padding":"{{new string('a', paddingLength)}}"}""").RootElement)
        {
            ExpectedStoreToken = "token",
            ValidationReceipt = "receipt",
        };
        FakeHelperLauncher launcher = FakeHelperLauncher.Running((_, _) => Task.CompletedTask);

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(overLimit, CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.PayloadTooLarge, result.Outcome);
        Assert.Null(launcher.LaunchedPath);
        Assert.Equal(overLimit.Draft.GetRawText(), result.Draft.GetRawText());
    }

    [Fact]
    public async Task BrokerRejection_IsSurfacedDistinctly()
    {
        FakeHelperLauncher launcher = FakeHelperLauncher.Running(async (arguments, _) =>
        {
            await using var client = await FakeHelperClient.ConnectAsync(arguments.PipeName);
            PolicyElevationRequestMessage request =
                await PolicyElevationFrame.ReadRequestAsync(client, CancellationToken.None);

            await PolicyElevationFrame.WriteResponseAsync(
                client,
                new PolicyElevationResponseMessage
                {
                    RequestId = request.RequestId,
                    Disposition = PolicyElevationDisposition.Rejected,
                    BrokerStatusCode = 409,
                    BrokerErrorCode = "StoreTokenMismatch",
                },
                CancellationToken.None);
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.BrokerRejected, result.Outcome);
        Assert.Equal("StoreTokenMismatch", result.BrokerErrorCode);
        Assert.Equal(409, result.BrokerStatusCode);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task DeclinedConsentPrompt_IsDistinctFromEveryOtherLaunchFailure()
    {
        FakeHelperLauncher launcher = FakeHelperLauncher.Failing(ElevatedHelperLaunchResult.Failed(
            PolicyElevationOutcome.UserDeclinedElevation,
            "The elevation prompt was dismissed.",
            PolicyElevationProtocol.ErrorCancelled));

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.UserDeclinedElevation, result.Outcome);
        Assert.Equal(1223, result.Win32ErrorCode);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task LaunchFailure_IsReportedAsLaunchFailed()
    {
        FakeHelperLauncher launcher = FakeHelperLauncher.Failing(ElevatedHelperLaunchResult.Failed(
            PolicyElevationOutcome.LaunchFailed,
            "The elevated policy helper could not be started.",
            2));

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.LaunchFailed, result.Outcome);
        Assert.Equal(2, result.Win32ErrorCode);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task MissingHelper_IsReportedAsUnavailableAndNeverLaunches()
    {
        FakeHelperLauncher launcher = FakeHelperLauncher.Running((_, _) => Task.CompletedTask);

        PolicyElevationResult result = await Build(
                launcher,
                locator: new FakeHelperLocator(PolicyElevationHelperLocation.NotFound("not packaged")))
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.HelperUnavailable, result.Outcome);
        Assert.Null(launcher.LaunchedPath);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task UntrustedHelper_IsReportedAsUntrustedAndNeverLaunches()
    {
        FakeHelperLauncher launcher = FakeHelperLauncher.Running((_, _) => Task.CompletedTask);

        PolicyElevationResult result = await Build(
                launcher,
                trustVerifier: FakeTrustVerifier.UnsignedFor(FakeHelperLocator.PackagedHelperPath))
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.HelperUntrusted, result.Outcome);
        Assert.Null(launcher.LaunchedPath);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task HelperSignedByADifferentPublisher_IsRejectedAndNeverLaunches()
    {
        FakeHelperLauncher launcher = FakeHelperLauncher.Running((_, _) => Task.CompletedTask);

        PolicyElevationResult result = await Build(
                launcher,
                trustVerifier: FakeTrustVerifier.DifferentSignerFor(FakeHelperLocator.PackagedHelperPath))
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.HelperUntrusted, result.Outcome);
        Assert.Null(launcher.LaunchedPath);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task UnsignedHost_CannotRequestAnElevatedWrite()
    {
        FakeHelperLauncher launcher = FakeHelperLauncher.Running((_, _) => Task.CompletedTask);

        PolicyElevationResult result = await Build(
                launcher,
                trustVerifier: FakeTrustVerifier.UnsignedFor(FakeHelperLocator.PackagedHostPath))
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.HelperUntrusted, result.Outcome);
        Assert.Null(launcher.LaunchedPath);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task RunningFromOutsideThePackagedLayout_IsRejectedBeforeAnySignerCheck()
    {
        FakeHelperLauncher launcher = FakeHelperLauncher.Running((_, _) => Task.CompletedTask);
        var verifier = FakeTrustVerifier.SameSigner();

        PolicyElevationResult result = await Build(
                launcher,
                trustVerifier: verifier,
                selfImagePathProvider: () => Path.Combine(Path.GetTempPath(), "Rogue", "UniGetUI.exe"))
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.HelperUntrusted, result.Outcome);
        Assert.Empty(verifier.VerifiedPaths);
        Assert.Null(launcher.LaunchedPath);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task TrustFailures_DoNotDiscloseProtectedPathsToTheCaller()
    {
        FakeHelperLauncher launcher = FakeHelperLauncher.Running((_, _) => Task.CompletedTask);

        PolicyElevationResult result = await Build(
                launcher,
                trustVerifier: FakeTrustVerifier.DifferentSignerFor(FakeHelperLocator.PackagedHelperPath))
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        string message = result.ErrorMessage ?? string.Empty;
        Assert.NotEmpty(message);
        Assert.DoesNotContain(FakeHelperLocator.PackagedRoot, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\", message, StringComparison.Ordinal);
        Assert.DoesNotContain("certificate", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("thumbprint", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedPeerAuthentication_StopsBeforeAnyPayloadIsWritten()
    {
        var wroteRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeHelperLauncher launcher = FakeHelperLauncher.Running(async (arguments, _) =>
        {
            await using var client = await FakeHelperClient.ConnectAsync(arguments.PipeName);

            try
            {
                await PolicyElevationFrame.ReadRequestAsync(client, CancellationToken.None);
                wroteRequest.TrySetResult(true);
            }
            catch (Exception)
            {
                wroteRequest.TrySetResult(false);
            }
        });

        PolicyElevationResult result = await Build(
                launcher,
                authenticator: new FakePeerAuthenticator(
                    PolicyElevationPeerAuthenticationResult.Rejected("the pipe peer is not the expected process")))
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.PeerAuthenticationFailed, result.Outcome);
        Assert.False(await wroteRequest.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task OversizedResponseAfterDispatch_IsReportedAsUnknown()
    {
        FakeHelperLauncher launcher = FakeHelperLauncher.Running(async (arguments, _) =>
        {
            await using var client = await FakeHelperClient.ConnectAsync(arguments.PipeName);
            await PolicyElevationFrame.ReadRequestAsync(client, CancellationToken.None);

            byte[] header = new byte[PolicyElevationProtocol.FrameLengthPrefixBytes];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)PolicyElevationProtocol.MaxResponseFrameBytes + 1);
            await client.WriteAsync(header, CancellationToken.None);
            await client.FlushAsync(CancellationToken.None);
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.WriteResultUnknown, result.Outcome);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task MalformedResponseAfterDispatch_IsReportedAsUnknown()
    {
        FakeHelperLauncher launcher = FakeHelperLauncher.Running(async (arguments, _) =>
        {
            await using var client = await FakeHelperClient.ConnectAsync(arguments.PipeName);
            await PolicyElevationFrame.ReadRequestAsync(client, CancellationToken.None);

            byte[] body = Encoding.UTF8.GetBytes("{\"protocolVersion\":\"1.0\"");
            byte[] frame = new byte[PolicyElevationProtocol.FrameLengthPrefixBytes + body.Length];
            BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
            body.CopyTo(frame, PolicyElevationProtocol.FrameLengthPrefixBytes);

            await client.WriteAsync(frame, CancellationToken.None);
            await client.FlushAsync(CancellationToken.None);
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.WriteResultUnknown, result.Outcome);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task ResponseForAnotherRequestId_IsReportedAsUnknown()
    {
        FakeHelperLauncher launcher = FakeHelperLauncher.Running(async (arguments, _) =>
        {
            await using var client = await FakeHelperClient.ConnectAsync(arguments.PipeName);
            await PolicyElevationFrame.ReadRequestAsync(client, CancellationToken.None);

            await PolicyElevationFrame.WriteResponseAsync(
                client,
                new PolicyElevationResponseMessage
                {
                    RequestId = new string('b', PolicyElevationProtocol.RequestIdCharacters),
                    Disposition = PolicyElevationDisposition.Committed,
                    CommittedStoreToken = "new-token",
                },
                CancellationToken.None);
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.WriteResultUnknown, result.Outcome);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task ClosedConnectionAfterDispatch_IsReportedAsUnknown()
    {
        FakeHelperLauncher launcher = FakeHelperLauncher.Running(async (arguments, _) =>
        {
            await using var client = await FakeHelperClient.ConnectAsync(arguments.PipeName);
            await PolicyElevationFrame.ReadRequestAsync(client, CancellationToken.None);
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.WriteResultUnknown, result.Outcome);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task NonZeroExitAfterCommittedAnswer_DoesNotOverrideCommit()
    {
        FakeHelperLauncher launcher = FakeHelperLauncher.Running(async (arguments, process) =>
        {
            await using var client = await FakeHelperClient.ConnectAsync(arguments.PipeName);
            PolicyElevationRequestMessage request =
                await PolicyElevationFrame.ReadRequestAsync(client, CancellationToken.None);

            await PolicyElevationFrame.WriteResponseAsync(
                client,
                new PolicyElevationResponseMessage
                {
                    RequestId = request.RequestId,
                    Disposition = PolicyElevationDisposition.Committed,
                    CommittedStoreToken = "new-token",
                },
                CancellationToken.None);

            process.Exit(PolicyElevationProtocol.ExitUnexpectedFailure);
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.Replaced, result.Outcome);
        Assert.Equal("new-token", result.CommittedStoreToken);
        Assert.Equal(PolicyElevationProtocol.ExitUnexpectedFailure, result.HelperExitCode);
        AssertDraftPreserved(result);
    }

    [Theory]
    [InlineData(PolicyElevationDisposition.Committed, PolicyElevationOutcome.Replaced)]
    [InlineData(PolicyElevationDisposition.Rejected, PolicyElevationOutcome.BrokerRejected)]
    public async Task CallerCancellationAfterAuthenticatedAcknowledgement_SettlesAndPreservesResult(
        PolicyElevationDisposition disposition,
        PolicyElevationOutcome expectedOutcome)
    {
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseWritten = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHelperLauncher launcher = FakeHelperLauncher.Running(async (arguments, _) =>
        {
            await using var client = await FakeHelperClient.ConnectAsync(arguments.PipeName);
            PolicyElevationRequestMessage request =
                await PolicyElevationFrame.ReadRequestAsync(client, CancellationToken.None);
            await PolicyElevationFrame.WriteResponseAsync(
                client,
                new PolicyElevationResponseMessage
                {
                    RequestId = request.RequestId,
                    Disposition = disposition,
                    CommittedStoreToken = disposition == PolicyElevationDisposition.Committed
                        ? "new-token"
                        : null,
                    BrokerStatusCode = disposition == PolicyElevationDisposition.Rejected
                        ? 409
                        : null,
                    BrokerErrorCode = disposition == PolicyElevationDisposition.Rejected
                        ? ErrorCode.WarningConfirmationRequired.ToString()
                        : null,
                },
                CancellationToken.None);
            responseWritten.TrySetResult();
            await release.Task;
        });

        Task<PolicyElevationResult> pending = Build(launcher)
            .ReplacePolicyAsync(BuildRequest(), cancellation.Token);
        await responseWritten.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await launcher.LastProcess!.SecondExitWaitStarted.WaitAsync(TimeSpan.FromSeconds(20));

        PolicyElevationResult result;
        try
        {
            await cancellation.CancelAsync();
            result = await pending.WaitAsync(TestCloseGuardBound);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(PolicyElevationProtocol.ExitSuccess, result.HelperExitCode);
        if (disposition == PolicyElevationDisposition.Committed)
        {
            Assert.Equal("new-token", result.CommittedStoreToken);
        }
        else
        {
            Assert.Equal(409, result.BrokerStatusCode);
            Assert.Equal(ErrorCode.WarningConfirmationRequired.ToString(), result.BrokerErrorCode);
        }
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task ExitBeforeConnecting_IsMappedFromTheHelperExitCode()
    {
        FakeHelperLauncher launcher = FakeHelperLauncher.Running((_, process) =>
        {
            process.Exit(PolicyElevationProtocol.ExitPeerAuthenticationFailed);
            return Task.CompletedTask;
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.PeerAuthenticationFailed, result.Outcome);
        Assert.Equal(PolicyElevationProtocol.ExitPeerAuthenticationFailed, result.HelperExitCode);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task CrashBeforeConnecting_IsReportedAsHelperCrashed()
    {
        FakeHelperLauncher launcher = FakeHelperLauncher.Running((_, process) =>
        {
            process.Exit(PolicyElevationProtocol.ExitUnexpectedFailure);
            return Task.CompletedTask;
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.HelperCrashed, result.Outcome);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task SilentHelper_IsReportedAsTimedOut()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeHelperLauncher launcher = FakeHelperLauncher.Running(async (arguments, _) =>
        {
            await using var client = await FakeHelperClient.ConnectAsync(arguments.PipeName);
            await PolicyElevationFrame.ReadRequestAsync(client, CancellationToken.None);
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        });

        var elevator = Build(
            launcher,
            timeouts: new PolicyElevationTimeouts(
                TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2)));

        PolicyElevationResult result = await elevator.ReplacePolicyAsync(BuildRequest(), CancellationToken.None);
        release.TrySetResult(true);

        Assert.Equal(PolicyElevationOutcome.WriteResultUnknown, result.Outcome);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task CallerCancellationBeforeAcknowledgement_RemainsWriteResultUnknown()
    {
        using var cancellation = new CancellationTokenSource();
        var requestReceived = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeHelperLauncher launcher = FakeHelperLauncher.Running(async (arguments, _) =>
        {
            await using var client = await FakeHelperClient.ConnectAsync(arguments.PipeName);
            await PolicyElevationFrame.ReadRequestAsync(client, CancellationToken.None);
            requestReceived.TrySetResult(true);
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        });

        Task<PolicyElevationResult> pending = Build(launcher)
            .ReplacePolicyAsync(BuildRequest(), cancellation.Token);

        await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(20));

        PolicyElevationResult result;
        try
        {
            await cancellation.CancelAsync();
            result = await pending.WaitAsync(TestCloseGuardBound);
        }
        finally
        {
            release.TrySetResult(true);
        }

        Assert.Equal(PolicyElevationOutcome.WriteResultUnknown, result.Outcome);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task OnlyOneConnectionIsEverAccepted()
    {
        var firstConnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? secondConnectionError = null;

        FakeHelperLauncher launcher = FakeHelperLauncher.Running(async (arguments, _) =>
        {
            await using var client = await FakeHelperClient.ConnectAsync(arguments.PipeName);
            firstConnected.TrySetResult(true);

            try
            {
                await using var intruder = await FakeHelperClient.ConnectAsync(arguments.PipeName, 2_000);
            }
            catch (Exception ex)
            {
                secondConnectionError = ex;
            }

            PolicyElevationRequestMessage request =
                await PolicyElevationFrame.ReadRequestAsync(client, CancellationToken.None);

            await PolicyElevationFrame.WriteResponseAsync(
                client,
                new PolicyElevationResponseMessage
                {
                    RequestId = request.RequestId,
                    Disposition = PolicyElevationDisposition.Committed,
                    CommittedStoreToken = "new-token",
                },
                CancellationToken.None);
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.Replaced, result.Outcome);
        Assert.NotNull(secondConnectionError);
    }

    [Fact]
    public async Task PipeNameIsSingleUseAndUnpredictable()
    {
        var names = new List<string>();

        for (int i = 0; i < 8; i++)
        {
            FakeHelperLauncher launcher = FakeHelperLauncher.Running((arguments, _) =>
            {
                names.Add(arguments.PipeName);
                return Task.CompletedTask;
            });

            await Build(
                    launcher,
                    timeouts: new PolicyElevationTimeouts(
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromSeconds(2)))
                .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);
        }

        Assert.Equal(8, names.Count);
        Assert.Equal(8, names.Distinct().Count());
        Assert.All(names, name => Assert.True(PolicyElevationLaunchArguments.IsValidPipeName(name)));
    }
}
#endif
