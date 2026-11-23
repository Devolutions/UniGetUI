using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using UniGetUI.PackageEngine.AgentBroker;

namespace UniGetUI.PackageEngine.Tests;

public class RemoteGrantRequestBuilderTests
{
    private const string RequestId = "0b3e6f0e-5d58-4a0e-9d2c-1f4f1c6c8a11";
    private const string Grant = "g-r4nt.secret_value~1:x";

    private static RemoteGrantOperation Op(
        Operation operation,
        ManagerName manager,
        Scope scope = Scope.User,
        string? reportedScope = null,
        string source = "",
        string version = "") =>
        new(RequestId, operation, manager, source, "Some.Pkg", version, scope,
            operation is Operation.Install ? null : reportedScope ?? "");

    [Theory]
    [InlineData("WinGet", ManagerName.Winget)]
    [InlineData("winget", ManagerName.Winget)]
    [InlineData("PowerShell", ManagerName.PowerShell)]
    [InlineData("winps", ManagerName.PowerShell)]
    [InlineData("PowerShell7", ManagerName.PowerShell7)]
    [InlineData("pwsh", ManagerName.PowerShell7)]
    [InlineData("Dotnet", ManagerName.Dotnet)]
    [InlineData("dotnet-tool", ManagerName.Dotnet)]
    [InlineData("Npm", ManagerName.Npm)]
    [InlineData("Pip", ManagerName.Pip)]
    [InlineData("Scoop", ManagerName.Scoop)]
    [InlineData("Cargo", ManagerName.Cargo)]
    [InlineData("Bun", ManagerName.Bun)]
    [InlineData("Vcpkg", ManagerName.Vcpkg)]
    public void ManagerNamesAndInventoryAliasesMapToBrokerManagers(string text, ManagerName expected)
    {
        Assert.True(RemoteGrantRequestBuilder.TryParseManager(text, out ManagerName manager));
        Assert.Equal(expected, manager);
    }

    [Theory]
    [InlineData("Chocolatey")]
    [InlineData("choco")]
    [InlineData(".NET Tool")]
    [InlineData("")]
    [InlineData("w\u0131nget")]
    public void ChocolateyAndUnknownManagersAreRefused(string text) =>
        Assert.False(RemoteGrantRequestBuilder.TryParseManager(text, out _));

    [Theory]
    [InlineData(Operation.Install, Scope.User, null, Scope.User, Elevation.Standard)]
    [InlineData(Operation.Install, Scope.Machine, null, Scope.Machine, Elevation.Elevated)]
    [InlineData(Operation.Update, Scope.User, "", Scope.User, Elevation.Standard)]
    [InlineData(Operation.Update, Scope.Machine, "user", Scope.Machine, Elevation.Elevated)]
    [InlineData(Operation.Uninstall, Scope.Machine, "", Scope.Machine, Elevation.Elevated)]
    public void WinGetRunsInTheRequestedScope(
        Operation operation, Scope scope, string? reported, Scope expected, Elevation elevation)
    {
        RemoteGrantPlan plan = RemoteGrantRequestBuilder.Plan(Op(operation, ManagerName.Winget, scope, reported, operation is Operation.Install ? "winget" : ""))!;

        Assert.Equal(expected, plan.Request.Options.Scope);
        Assert.Equal(elevation, plan.Elevation);
        Assert.False(plan.RequiresUniqueInstalledRow);
        Assert.Equal(operation is Operation.Install, plan.Request.Options.NoUpgrade);
    }

    [Theory]
    [InlineData(ManagerName.Npm, Operation.Install)]
    [InlineData(ManagerName.Pip, Operation.Update)]
    [InlineData(ManagerName.PowerShell, Operation.Uninstall)]
    [InlineData(ManagerName.PowerShell7, Operation.Update)]
    public void OtherManagersRefuseMachineScope(ManagerName manager, Operation operation) =>
        Assert.Null(RemoteGrantRequestBuilder.Plan(Op(operation, manager, Scope.Machine, "machine", "feed")));

    [Theory]
    [InlineData(ManagerName.PowerShell, Operation.Update, "", null, true)]
    [InlineData(ManagerName.PowerShell, Operation.Update, "User", Scope.User, false)]
    [InlineData(ManagerName.PowerShell, Operation.Update, "machine", Scope.Machine, false)]
    [InlineData(ManagerName.PowerShell7, Operation.Update, "machine", Scope.Machine, false)]
    [InlineData(ManagerName.PowerShell7, Operation.Uninstall, "", null, true)]
    [InlineData(ManagerName.PowerShell7, Operation.Uninstall, "user", Scope.User, false)]
    [InlineData(ManagerName.PowerShell, Operation.Uninstall, "machine", Scope.User, true)]
    [InlineData(ManagerName.Npm, Operation.Update, "global", Scope.User, true)]
    [InlineData(ManagerName.Pip, Operation.Uninstall, "", Scope.User, true)]
    [InlineData(ManagerName.Dotnet, Operation.Update, "", Scope.User, true)]
    public void UpdatesAndUninstallsTargetTheReportedScopeOrRequireAUniqueRow(
        ManagerName manager, Operation operation, string reported, Scope? expected, bool uniqueRow)
    {
        RemoteGrantPlan plan = RemoteGrantRequestBuilder.Plan(Op(operation, manager, Scope.User, reported))!;

        Assert.Equal(expected, plan.Request.Options.Scope);
        Assert.Equal(uniqueRow, plan.RequiresUniqueInstalledRow);
        Assert.Equal(Elevation.Standard, plan.Elevation);
        Assert.False(plan.Request.Options.NoUpgrade);
    }

    [Theory]
    [InlineData(ManagerName.PowerShell, Operation.Update)]
    [InlineData(ManagerName.PowerShell7, Operation.Uninstall)]
    public void UnrecognizedPowerShellScopeLabelsAreRefused(ManagerName manager, Operation operation) =>
        Assert.Null(RemoteGrantRequestBuilder.Plan(Op(operation, manager, Scope.User, "AllUsers")));

    [Fact]
    public void ReportedScopeMustBeAbsentForInstallAndPresentOtherwise()
    {
        Assert.Null(RemoteGrantRequestBuilder.Plan(
            new RemoteGrantOperation(RequestId, Operation.Install, ManagerName.Npm, "npm", "p", "", Scope.User, "")));
        Assert.Null(RemoteGrantRequestBuilder.Plan(
            new RemoteGrantOperation(RequestId, Operation.Update, ManagerName.Npm, "", "p", "", Scope.User, null)));
    }

    [Fact]
    public void RequestCarriesOnlyGrantBoundFields()
    {
        RemoteGrantPlan plan = RemoteGrantRequestBuilder.Plan(
            Op(Operation.Install, ManagerName.Npm, source: "npm", version: "1.2.3"))!;
        PackageOperationRequest request = plan.Request;

        Assert.Equal(RequestId, request.RequestId);
        Assert.Equal("npm", request.Source.Name);
        Assert.Null(request.Source.Url);
        Assert.Equal("1.2.3", request.Package.Version);
        Assert.Null(request.Package.Architecture);
        Assert.Null(request.Package.Channel);
        Assert.Null(request.Options.CustomInstallLocation);
        Assert.Empty(request.Options.CustomParameters!);
        Assert.Null(request.Options.PreOperationCommand);
        Assert.Null(request.Options.PostOperationCommand);
        Assert.Empty(request.Options.KillBeforeOperation!);
        Assert.False(request.Options.Interactive);
        Assert.False(request.Options.SkipHashCheck);
        Assert.False(request.Options.PreRelease);
        Assert.False(request.Options.UninstallPrevious);
        Assert.False(request.IncludeCommandPreview);
        Assert.Null(RemoteGrantRequestBuilder.Plan(Op(Operation.Install, ManagerName.Npm, source: "npm"))!.Request.Package.Version);
    }

    [Theory]
    [InlineData(true, BrokerClientErrorKind.InvalidRequest, RemoteGrantDelivery.NotSent)]
    [InlineData(true, BrokerClientErrorKind.BrokerUnavailable, RemoteGrantDelivery.NotSent)]
    [InlineData(true, BrokerClientErrorKind.ServerAuthenticationFailed, RemoteGrantDelivery.NotSent)]
    [InlineData(true, BrokerClientErrorKind.BrokerError, RemoteGrantDelivery.MaybeSent)]
    [InlineData(true, BrokerClientErrorKind.PolicyDenied, RemoteGrantDelivery.MaybeSent)]
    [InlineData(true, BrokerClientErrorKind.UnsupportedEndpoint, RemoteGrantDelivery.MaybeSent)]
    [InlineData(false, BrokerClientErrorKind.InvalidRequest, RemoteGrantDelivery.MaybeSent)]
    [InlineData(false, BrokerClientErrorKind.InvalidResponse, RemoteGrantDelivery.MaybeSent)]
    public void OnlyFailuresBeforeTheGrantLeftCountAsNotSent(
        bool noWorkStarted, BrokerClientErrorKind kind, RemoteGrantDelivery expected) =>
        Assert.Equal(expected, BrokerRemoteGrantSubmitter.ClassifyFailure(noWorkStarted, kind));

    private const string Capabilities = """
        {"ResponseKind":"CapabilitiesResponse","ResponseVersion":"1.0","Server":{"ServerVersion":"mock","Transport":"HttpNamedPipe"},"Transports":["HttpNamedPipe"],"Managers":[{"Manager":"Winget","Operations":["Install","Update","Uninstall"],"Scopes":["User","Machine"],"Architectures":["X86","X64","Arm64","Neutral"],"SupportsCustomParameters":true,"SupportsCustomInstallLocation":true,"SupportsCaptureOutput":true,"SupportsDetails":true}],"MaxRequestBodyBytes":1048576}
        """;

    private const string Accepted = $$$"""
        {"ResponseKind":"GrantExecutionResponse","ResponseVersion":"1.0","Server":{"ServerVersion":"1.2.3","Transport":"HttpNamedPipe"},"RequestId":"{{{RequestId}}}","ReceivedAt":"2026-11-15T05:07:42Z","CompletedAt":"2026-11-15T05:07:43Z","Request":{"Manager":"Winget","Source":"winget","PackageId":"Some.Pkg","Operation":"Install"},"Authorization":{"Kind":"RemoteGrant"},"Operation":{"OperationId":"op-1","Status":"Succeeded","SubmittedAt":"2026-11-15T05:07:42Z"}}
        """;

    private static readonly string AllManagersCapabilities =
        "{\"ResponseKind\":\"CapabilitiesResponse\",\"ResponseVersion\":\"1.0\",\"Server\":{\"ServerVersion\":\"mock\",\"Transport\":\"HttpNamedPipe\"},\"Transports\":[\"HttpNamedPipe\"],\"Managers\":["
        + string.Join(',', Enum.GetNames<ManagerName>().Select(name =>
            $"{{\"Manager\":\"{name}\",\"Operations\":[\"Install\",\"Update\",\"Uninstall\"],\"Scopes\":[\"User\",\"Machine\"],\"Architectures\":[\"X86\",\"X64\",\"Arm64\",\"Neutral\"],\"SupportsCustomParameters\":true,\"SupportsCustomInstallLocation\":true,\"SupportsCaptureOutput\":true,\"SupportsDetails\":true}}"))
        + "],\"MaxRequestBodyBytes\":1048576}";

    private sealed class FakeTransport(BrokerTransportResponse grantResponse, string capabilities = Capabilities) : IAuthenticatedBrokerTransport
    {
        public List<BrokerTransportRequest> Legacy { get; } = [];
        public List<BrokerTransportRequest> Granted { get; } = [];
        public IBrokerServerAuthenticator? Authenticator { get; private set; }

        public Transport Kind => Transport.HttpNamedPipe;

        public Task<BrokerTransportResponse> Send(BrokerTransportRequest request, CancellationToken cancellationToken = default)
        {
            Legacy.Add(request);
            return request.Path == "/v1/capabilities"
                ? Task.FromResult(new BrokerTransportResponse { StatusCode = 200, Body = capabilities })
                : throw new InvalidOperationException("Unexpected legacy request.");
        }

        public Task<AuthenticatedBrokerTransportResponse> SendAuthenticated(
            BrokerTransportRequest request,
            IBrokerServerAuthenticator authenticator,
            CancellationToken cancellationToken = default)
        {
            Granted.Add(request);
            Authenticator = authenticator;
            return Task.FromResult(new AuthenticatedBrokerTransportResponse
            {
                Response = grantResponse,
                Server = new BrokerPipeServerIdentity(Environment.ProcessId, 0),
            });
        }

        public void Dispose() { }
    }

    private static RemoteGrantPlan WinGetInstall() =>
        RemoteGrantRequestBuilder.Plan(Op(Operation.Install, ManagerName.Winget, Scope.Machine, source: "winget"))!;

    [Fact]
    public async Task AcceptedGrantIsSentOnceToTheGrantRouteAndStillCountsAsUnknown()
    {
        var transport = new FakeTransport(new BrokerTransportResponse { StatusCode = 200, Body = Accepted });
        var submitter = new BrokerRemoteGrantSubmitter(() => transport);

        Assert.Equal(RemoteGrantDelivery.MaybeSent, await submitter.SubmitAsync(WinGetInstall(), Grant, CancellationToken.None));

        BrokerTransportRequest sent = Assert.Single(transport.Granted);
        Assert.Equal(BrokerApi.ExecuteGrantPath, sent.Path);
        Assert.Equal(Grant, sent.Headers[BrokerApi.PackageGrantHeaderName]);
        Assert.IsType<ServiceBrokerServerAuthenticator>(transport.Authenticator);
        Assert.DoesNotContain(transport.Legacy, request => request.Path != "/v1/capabilities");

        using JsonDocument body = JsonDocument.Parse(sent.Body!);
        JsonElement root = body.RootElement;
        Assert.Equal("Winget", root.GetProperty("Manager").GetString());
        Assert.Equal("Machine", root.GetProperty("Options").GetProperty("Scope").GetString());
        Assert.True(root.GetProperty("Options").GetProperty("NoUpgrade").GetBoolean());
        Assert.Equal("Elevated", root.GetProperty("Client").GetProperty("RequestedElevation").GetString());
        Assert.DoesNotContain(Grant, sent.Body);
    }

    [Theory]
    [InlineData(403, """{"ResponseKind":"ErrorResponse","ResponseVersion":"1.0","Server":{"ServerVersion":"1","Transport":"HttpNamedPipe"},"Code":"Forbidden","Message":"no"}""")]
    [InlineData(500, "")]
    [InlineData(404, "")]
    public async Task RejectedOrFailedGrantResponsesAreNotRetriedAndStayUnknown(int status, string body)
    {
        var transport = new FakeTransport(new BrokerTransportResponse { StatusCode = status, Body = body });
        var submitter = new BrokerRemoteGrantSubmitter(() => transport);

        Assert.Equal(RemoteGrantDelivery.MaybeSent, await submitter.SubmitAsync(WinGetInstall(), Grant, CancellationToken.None));
        Assert.Single(transport.Granted);
        Assert.DoesNotContain(transport.Legacy, request => request.Path != "/v1/capabilities");
    }

    [Fact]
    public async Task CapabilitiesFailureSendsNothing()
    {
        var transport = new ThrowingTransport();
        var submitter = new BrokerRemoteGrantSubmitter(() => transport);

        Assert.Equal(RemoteGrantDelivery.NotSent, await submitter.SubmitAsync(WinGetInstall(), Grant, CancellationToken.None));
        Assert.False(transport.GrantSent);
    }

    // Agent-side arguments, in the spelling the Agent sends, for the interop fixture.
    private static readonly (Operation Operation, string Manager, Scope Scope, string Source, string? ReportedScope)[] InteropCases =
    [
        (Operation.Install, "WinGet", Scope.Machine, "winget", null),
        (Operation.Install, "WinGet", Scope.User, "winget", null),
        (Operation.Update, "WinGet", Scope.Machine, "", "machine"),
        (Operation.Uninstall, "WinGet", Scope.User, "", "user"),
        (Operation.Update, "winps", Scope.User, "", ""),
        (Operation.Update, "pwsh", Scope.User, "", "machine"),
        (Operation.Uninstall, "pwsh", Scope.User, "", "user"),
        (Operation.Install, "dotnet-tool", Scope.User, "nuget.org", null),
        (Operation.Update, "dotnet-tool", Scope.User, "", ""),
        (Operation.Uninstall, "Pip", Scope.User, "", "machine"),
    ];

    /// <summary>
    /// The Broker requests sent for representative Agent arguments, kept in
    /// Fixtures/AgentInterop so the Agent's interop tests can cite them. Fields that depend on the
    /// machine or the clock are replaced by fixed values. Set UNIGETUI_REGENERATE_FIXTURES=1 to
    /// rewrite the file after an intended change.
    /// </summary>
    [Fact]
    public async Task BrokerRequestsMatchTheInteropFixture()
    {
        var cases = new JsonArray();
        foreach (var c in InteropCases)
        {
            Assert.True(RemoteGrantRequestBuilder.TryParseManager(c.Manager, out ManagerName manager));
            var operation = new RemoteGrantOperation(
                RequestId, c.Operation, manager, c.Source, "Some.Pkg", "", c.Scope, c.ReportedScope);
            RemoteGrantPlan plan = RemoteGrantRequestBuilder.Plan(operation)!;
            var transport = new FakeTransport(
                new BrokerTransportResponse { StatusCode = 200, Body = Accepted }, AllManagersCapabilities);
            Assert.Equal(
                RemoteGrantDelivery.MaybeSent,
                await new BrokerRemoteGrantSubmitter(() => transport).SubmitAsync(plan, Grant, CancellationToken.None));
            BrokerTransportRequest sent = Assert.Single(transport.Granted);

            JsonNode body = JsonNode.Parse(sent.Body!)!;
            body["CreatedAt"] = "2026-11-15T00:00:00+00:00";
            JsonNode client = body["Client"]!;
            client["EffectiveUser"] = "CONTOSO\\user";
            client["ClientExecutablePath"] = "C:\\Program Files\\UniGetUI\\UniGetUI.exe";
            client["ClientVersion"] = "0.0.0.0";

            var agentArguments = new JsonObject
            {
                ["operation"] = c.Operation.ToString(),
                ["manager"] = c.Manager,
                ["scope"] = c.Scope.ToString(),
                ["sourceName"] = c.Source,
                ["packageId"] = "Some.Pkg",
                ["version"] = "",
            };
            if (c.ReportedScope is not null)
            {
                agentArguments["reportedScope"] = c.ReportedScope;
            }

            cases.Add(new JsonObject
            {
                ["agentArguments"] = agentArguments,
                ["method"] = sent.Method,
                ["path"] = sent.Path,
                ["grantHeader"] = BrokerApi.PackageGrantHeaderName,
                ["grantHeaderValue"] = sent.Headers[BrokerApi.PackageGrantHeaderName],
                ["uniGetUiChecksUniqueInstalledRow"] = plan.RequiresUniqueInstalledRow,
                ["body"] = body,
            });
        }

        AssertMatchesFixture("broker-request-fixtures.json", cases);
    }

    private static void AssertMatchesFixture(string name, JsonNode actual, [CallerFilePath] string here = "")
    {
        string path = Path.Combine(Path.GetDirectoryName(here)!, "Fixtures", "AgentInterop", name);
        if (Environment.GetEnvironmentVariable("UNIGETUI_REGENERATE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(path, actual.ToJsonString(options).ReplaceLineEndings("\n") + "\n");
        }

        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), actual),
            $"{name} no longer matches the requests UniGetUI sends.");
    }

    private sealed class ThrowingTransport : IAuthenticatedBrokerTransport
    {
        public bool GrantSent { get; private set; }

        public Transport Kind => Transport.HttpNamedPipe;

        public Task<BrokerTransportResponse> Send(BrokerTransportRequest request, CancellationToken cancellationToken = default) =>
            throw new IOException("down");

        public Task<AuthenticatedBrokerTransportResponse> SendAuthenticated(
            BrokerTransportRequest request,
            IBrokerServerAuthenticator authenticator,
            CancellationToken cancellationToken = default)
        {
            GrantSent = true;
            throw new InvalidOperationException();
        }

        public void Dispose() { }
    }
}
