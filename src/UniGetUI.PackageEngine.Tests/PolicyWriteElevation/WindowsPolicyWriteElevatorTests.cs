#if WINDOWS
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Model;
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

    private static JsonElement BuildReplacementPayload()
    {
        var policy = new PolicyDocument
        {
            Metadata = new PolicyMetadata
            {
                Id = "policy-id",
                Publisher = "publisher",
                Revision = 1,
                PublishedAt = DateTimeOffset.Parse("2026-08-29T00:00:00Z"),
            },
            Enforcement = new PolicyEnforcement
            {
                DefaultDecision = Devolutions.Now.Policy.Model.Decision.Deny,
                RulePrecedence = RulePrecedence.PriorityThenDeny,
            },
            Rules = [],
        };
        var canonicalDraft = new PolicyDraftDocument
        {
            Metadata = new PolicyDraftMetadata
            {
                Id = "policy-id",
                Publisher = "publisher",
            },
            Enforcement = new PolicyEnforcement
            {
                DefaultDecision = Devolutions.Now.Policy.Model.Decision.Deny,
                RulePrecedence = RulePrecedence.PriorityThenDeny,
            },
            Rules = [],
        };
        var response = new PolicyReplacementResponse
        {
            Server = new ServerContext
            {
                ServerVersion = "2026.8.29",
                Transport = Transport.HttpNamedPipe,
            },
            Policy = policy,
            Validation = new PolicyValidationResult
            {
                ValidatorVersion = "2026.8.29",
                IsValid = true,
                CanonicalDraft = canonicalDraft,
                ValidationReceipt = "new-receipt",
                Findings = [],
            },
            Management = new PolicyManagementSnapshot
            {
                State = PolicyManagementState.Active,
                ConfiguredPath = @"C:\ProgramData\Devolutions\PackageBroker\package-broker-policy.json",
                StoreToken = "new-token",
                Source = PolicyConfigurationSource.DefaultPath,
                WriteCapability = PolicyWriteCapability.Writable,
                ElevationRequired = true,
                Policy = policy,
            },
        };
        using JsonDocument document = JsonDocument.Parse(BrokerSerializer.Serialize(response));
        return document.RootElement.Clone();
    }

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
    public async Task SuccessfulExchange_ReturnsReplacedAndRelaysThePayload()
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
                    Outcome = PolicyElevationResponseStatus.Replaced,
                    BrokerStatusCode = 200,
                    Payload = BuildReplacementPayload(),
                },
                CancellationToken.None);
        });

        var authenticator = new FakePeerAuthenticator();
        PolicyElevationResult result = await Build(launcher, authenticator: authenticator)
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        await launcher.Completion;

        Assert.Equal(PolicyElevationOutcome.Replaced, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.Equal(200, result.BrokerStatusCode);
        Assert.Equal("new-token", result.Response!.Management.StoreToken);
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
                    Outcome = PolicyElevationResponseStatus.BrokerRejected,
                    BrokerStatusCode = 409,
                    BrokerErrorCode = "StoreTokenMismatch",
                    Message = "The policy store changed.",
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
    public async Task OversizedResponse_IsReportedAsPayloadTooLarge()
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

        Assert.Equal(PolicyElevationOutcome.PayloadTooLarge, result.Outcome);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task MalformedResponse_IsReportedAsMalformed()
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

        Assert.Equal(PolicyElevationOutcome.MalformedResponse, result.Outcome);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task ResponseForAnotherRequestId_IsReportedAsMalformed()
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
                    Outcome = PolicyElevationResponseStatus.Replaced,
                },
                CancellationToken.None);
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.MalformedResponse, result.Outcome);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task ClosedConnectionWithoutAnswer_IsReportedAsConnectionClosed()
    {
        FakeHelperLauncher launcher = FakeHelperLauncher.Running(async (arguments, _) =>
        {
            await using var client = await FakeHelperClient.ConnectAsync(arguments.PipeName);
            await PolicyElevationFrame.ReadRequestAsync(client, CancellationToken.None);
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.ConnectionClosed, result.Outcome);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task NonZeroExitAfterAnswering_IsReportedAsHelperCrashed()
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
                    Outcome = PolicyElevationResponseStatus.Replaced,
                    Payload = BuildReplacementPayload(),
                },
                CancellationToken.None);

            process.Exit(PolicyElevationProtocol.ExitUnexpectedFailure);
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyElevationOutcome.HelperCrashed, result.Outcome);
        Assert.Equal(PolicyElevationProtocol.ExitUnexpectedFailure, result.HelperExitCode);
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

        Assert.Equal(PolicyElevationOutcome.TimedOut, result.Outcome);
        AssertDraftPreserved(result);
    }

    [Fact]
    public async Task CallerCancellation_IsDistinctFromATimeout()
    {
        using var cancellation = new CancellationTokenSource();
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeHelperLauncher launcher = FakeHelperLauncher.Running(async (arguments, _) =>
        {
            await using var client = await FakeHelperClient.ConnectAsync(arguments.PipeName);
            connected.TrySetResult(true);
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        });

        Task<PolicyElevationResult> pending = Build(launcher)
            .ReplacePolicyAsync(BuildRequest(), cancellation.Token);

        await connected.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await cancellation.CancelAsync();

        PolicyElevationResult result = await pending;
        release.TrySetResult(true);

        Assert.Equal(PolicyElevationOutcome.Cancelled, result.Outcome);
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
                    Outcome = PolicyElevationResponseStatus.Replaced,
                    Payload = BuildReplacementPayload(),
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
