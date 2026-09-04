#if WINDOWS
using UniGetUI.AgentPolicy.ElevatedHelper;

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

public class PolicyElevationHelperStageTimeoutTests
{
    [Fact]
    public void SlowAuthenticatedRequestRead_DoesNotConsumeBrokerExchangeBudget()
    {
        using var timeouts = new PolicyElevationHelperStageTimeouts(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(100));
        CancellationToken connectToken = timeouts.Token;

        Thread.Sleep(TimeSpan.FromMilliseconds(200));

        Assert.False(connectToken.IsCancellationRequested);
        timeouts.BeginExchange();
        CancellationToken exchangeToken = timeouts.Token;
        Assert.NotEqual(connectToken, exchangeToken);
        Assert.False(exchangeToken.IsCancellationRequested);
        Assert.True(exchangeToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void PreExchangeStage_UsesConnectTimeout()
    {
        using var timeouts = new PolicyElevationHelperStageTimeouts(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(5));

        Assert.True(timeouts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ExchangeTimeoutCannotBeRestarted()
    {
        using var timeouts = new PolicyElevationHelperStageTimeouts(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));
        timeouts.BeginExchange();

        Assert.Throws<InvalidOperationException>(timeouts.BeginExchange);
    }
}
#endif
