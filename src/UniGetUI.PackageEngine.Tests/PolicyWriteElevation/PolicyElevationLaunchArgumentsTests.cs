using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

/// <summary>
/// The helper command line is routing-only. These tests pin that contract so a future change
/// cannot quietly start passing policy data, tokens, receipts or file paths through argv.
/// </summary>
public class PolicyElevationLaunchArgumentsTests
{
    private static PolicyElevationLaunchArguments Valid() => new(
        PolicyElevationProtocol.Version,
        PolicyElevationProtocol.PipeNamePrefix + new string('0', PolicyElevationProtocol.PipeNameEntropyCharacters),
        4242,
        638_000_000_000_000_000L,
        1);

    [Fact]
    public void Format_RoundTripsThroughTryParse()
    {
        PolicyElevationLaunchArguments original = Valid();

        Assert.True(PolicyElevationLaunchArguments.TryParse(
            original.Format().Split(' '),
            out PolicyElevationLaunchArguments? parsed,
            out string? error));

        Assert.Null(error);
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void Format_CarriesNothingButRoutingInformation()
    {
        string[] tokens = Valid().Format().Split(' ');

        Assert.Equal(10, tokens.Length);
        Assert.Equal(
            ["--protocol", "--pipe", "--parent-pid", "--parent-created", "--session"],
            tokens.Where((_, index) => index % 2 == 0).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(11)]
    public void WrongArgumentCount_IsRejected(int count)
    {
        Assert.False(PolicyElevationLaunchArguments.TryParse(
            Enumerable.Repeat("--protocol", count).ToArray(),
            out _,
            out string? error));

        Assert.NotNull(error);
    }

    [Fact]
    public void DuplicatedArgument_IsRejected()
        => Assert.False(PolicyElevationLaunchArguments.TryParse(
            ["--protocol", "1.0", "--protocol", "1.0", "--pipe", "x", "--parent-pid", "1", "--session", "1"],
            out _,
            out _));

    [Fact]
    public void UnknownArgument_IsRejected()
        => Assert.False(PolicyElevationLaunchArguments.TryParse(
            ["--protocol", "1.0", "--pipe", "x", "--parent-pid", "1", "--parent-created", "1", "--draft", "{}"],
            out _,
            out _));

    [Fact]
    public void ForeignProtocolVersion_IsRejected()
    {
        PolicyElevationLaunchArguments arguments = Valid() with { ProtocolVersion = "9.9" };

        Assert.False(PolicyElevationLaunchArguments.TryParse(
            BuildTokens(arguments),
            out _,
            out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("UniGetUI.PolicyElevation.")]
    [InlineData("SomethingElse.0011223344556677889900aabbccddeeff")]
    [InlineData("UniGetUI.PolicyElevation.0011223344556677889900AABBCCDDEEFF")]
    [InlineData("UniGetUI.PolicyElevation.0011223344556677889900aabbccdde")]
    [InlineData("UniGetUI.PolicyElevation.0011223344556677889900aabbccddeeff00")]
    [InlineData("UniGetUI.PolicyElevation.0011223344556677889900aabbccdde-")]
    public void MalformedPipeName_IsRejected(string pipeName)
        => Assert.False(PolicyElevationLaunchArguments.IsValidPipeName(pipeName));

    [Fact]
    public void WellFormedPipeName_IsAccepted()
        => Assert.True(PolicyElevationLaunchArguments.IsValidPipeName(
            PolicyElevationProtocol.PipeNamePrefix + "0011223344556677889900aabbccddee"));

    [Fact]
    public void NegativeParentProcessId_IsRejected()
        => Assert.False(PolicyElevationLaunchArguments.TryParse(
            Replace(BuildTokens(Valid()), "--parent-pid", "-1"),
            out _,
            out _));

    [Fact]
    public void ZeroCreationTime_IsRejected()
        => Assert.False(PolicyElevationLaunchArguments.TryParse(
            Replace(BuildTokens(Valid()), "--parent-created", "0"),
            out _,
            out _));

    [Fact]
    public void FormattingAnInvalidContractThrows()
    {
        PolicyElevationLaunchArguments invalid = Valid() with { PipeName = "not-a-pipe" };
        Assert.Throws<ArgumentException>(invalid.Format);
    }

    private static string[] BuildTokens(PolicyElevationLaunchArguments arguments) =>
    [
        "--protocol", arguments.ProtocolVersion,
        "--pipe", arguments.PipeName,
        "--parent-pid", arguments.ParentProcessId.ToString(),
        "--parent-created", arguments.ParentCreationTimeUtcTicks.ToString(),
        "--session", arguments.SessionId.ToString(),
    ];

    private static string[] Replace(string[] tokens, string name, string value)
    {
        string[] copy = [.. tokens];
        for (int i = 0; i < copy.Length; i += 2)
        {
            if (copy[i] == name)
            {
                copy[i + 1] = value;
            }
        }

        return copy;
    }
}
