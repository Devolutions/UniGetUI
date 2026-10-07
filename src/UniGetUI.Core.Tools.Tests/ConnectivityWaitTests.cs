using System.Diagnostics;
using System.Net;

namespace UniGetUI.Core.Tools.Tests;

public class ConnectivityWaitTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    // Runs the wait on its own thread and stops its probes once the test is over, so that a wait
    // which never gives up fails the test instead of hanging the run
    private static async Task<bool> WaitGuarded(
        Func<bool> systemReportsInternet,
        Func<TimeSpan, bool> httpCheckSucceeds,
        TimeSpan timeout
    )
    {
        using var stop = new CancellationTokenSource();

        var wait = Task.Run(() =>
            CoreTools.WaitForConnectivity(
                () =>
                {
                    stop.Token.ThrowIfCancellationRequested();
                    return systemReportsInternet();
                },
                httpCheckSucceeds,
                timeout,
                Poll
            )
        );

        try
        {
            return await wait.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await stop.CancelAsync();
        }
    }

    [Fact]
    public void ReturnsAtOnceWhenTheSystemReportsInternet()
    {
        int httpChecks = 0;

        bool connected = CoreTools.WaitForConnectivity(
            () => true,
            _ =>
            {
                httpChecks++;
                return false;
            },
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(1)
        );

        Assert.True(connected);
        Assert.Equal(0, httpChecks);
    }

    [Fact]
    public void AStaleSystemAnswerIsOverriddenByTheHttpCheck()
    {
        var stopwatch = Stopwatch.StartNew();

        bool connected = CoreTools.WaitForConnectivity(
            () => false,
            _ => true,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(1)
        );

        Assert.True(connected);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"waited {stopwatch.Elapsed}");
    }

    [Fact]
    public void AConnectionThatComesUpWhileWaitingIsPickedUp()
    {
        int systemChecks = 0;

        bool connected = CoreTools.WaitForConnectivity(
            () => ++systemChecks >= 4,
            _ => false,
            TimeSpan.FromSeconds(30),
            Poll
        );

        Assert.True(connected);
        Assert.Equal(4, systemChecks);
    }

    [Fact]
    public async Task GivesUpAfterTheTimeoutInsteadOfBlockingForever()
    {
        TimeSpan timeout = TimeSpan.FromMilliseconds(300);
        var stopwatch = Stopwatch.StartNew();

        bool connected = await WaitGuarded(() => false, _ => false, timeout);

        Assert.False(connected);
        Assert.True(stopwatch.Elapsed >= timeout, $"returned after {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task SlowHttpChecksDoNotStretchTheWaitPastTheTimeout()
    {
        int httpChecks = 0;
        var stopwatch = Stopwatch.StartNew();

        bool connected = await WaitGuarded(
            () => false,
            _ =>
            {
                httpChecks++;
                Thread.Sleep(200);
                return false;
            },
            TimeSpan.FromMilliseconds(300)
        );

        Assert.False(connected);
        Assert.InRange(httpChecks, 1, 3);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"waited {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task EachHttpCheckIsGivenOnlyTheTimeThatIsLeft()
    {
        var budgets = new List<TimeSpan>();
        TimeSpan timeout = TimeSpan.FromMilliseconds(1000);

        bool connected = await WaitGuarded(
            () => false,
            budget =>
            {
                budgets.Add(budget);
                Thread.Sleep(150);
                return false;
            },
            timeout
        );

        Assert.False(connected);
        Assert.True(budgets.Count >= 2, $"HTTP check ran {budgets.Count} times");
        Assert.All(budgets, b => Assert.InRange(b, TimeSpan.FromTicks(1), timeout));
        for (int i = 1; i < budgets.Count; i++)
        {
            Assert.True(
                budgets[i] <= budgets[i - 1] - TimeSpan.FromMilliseconds(100),
                $"budgets {budgets[i - 1]} then {budgets[i]}"
            );
        }
    }

    [Fact]
    public async Task AHttpCheckThatUsesItsWholeBudgetDoesNotPushTheWaitPastTheTimeout()
    {
        TimeSpan timeout = TimeSpan.FromMilliseconds(300);
        var stopwatch = Stopwatch.StartNew();

        bool connected = await WaitGuarded(
            () => false,
            budget =>
            {
                Thread.Sleep(budget);
                return false;
            },
            timeout
        );

        Assert.False(connected);
        Assert.InRange(stopwatch.Elapsed, timeout, timeout + TimeSpan.FromSeconds(2));
    }

    private static readonly Uri TestUrl = new("http://connectivity.test/connecttest.txt");

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public string? Method { get; private set; }
        public Uri? Url { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            Url = request.RequestUri;
            return respond(request, cancellationToken);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(Send(request, cancellationToken));
    }

    private static HttpResponseMessage Page(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    [Fact]
    public void TheExpectedTestPageCountsAsConnectivity()
    {
        var handler = new StubHandler((_, _) => Page(HttpStatusCode.OK, "Microsoft Connect Test"));

        Assert.True(CoreTools.HttpConnectivityCheck(handler, TestUrl, TimeSpan.FromSeconds(5)));
        Assert.Equal("GET", handler.Method);
        Assert.Equal(TestUrl, handler.Url);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "<html>Sign in to the Wi-Fi network</html>")]
    [InlineData(HttpStatusCode.OK, "")]
    [InlineData(HttpStatusCode.Found, "")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Microsoft Connect Test")]
    public void AnythingElseDoesNotCountAsConnectivity(HttpStatusCode status, string body)
    {
        var handler = new StubHandler((_, _) => Page(status, body));

        Assert.False(CoreTools.HttpConnectivityCheck(handler, TestUrl, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AFailedRequestDoesNotCountAsConnectivity()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("no route to host"));

        Assert.False(CoreTools.HttpConnectivityCheck(handler, TestUrl, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AnAnswerThatArrivesAfterTheBudgetDoesNotCountAsConnectivity()
    {
        var handler = new StubHandler(
            (_, token) =>
            {
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
                token.ThrowIfCancellationRequested();
                return Page(HttpStatusCode.OK, "Microsoft Connect Test");
            }
        );
        var stopwatch = Stopwatch.StartNew();

        bool connected = CoreTools.HttpConnectivityCheck(handler, TestUrl, TimeSpan.FromMilliseconds(150));

        Assert.False(connected);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"waited {stopwatch.Elapsed}");
    }
}
