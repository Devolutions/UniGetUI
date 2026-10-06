using NUnit.Framework;
using UniGetUI.Interface;
using UniGetUI.Tui.Infrastructure;
using NAssert = NUnit.Framework.Assert;

namespace UniGetUI.Tui.Tests;

[TestFixture, NonParallelizable]
public sealed class TuiCloudBackupTests
{
    private static GitHubDeviceFlow Flow(int interval = 5, int expires = 180) =>
        new() { DeviceCode = "device-5447", Interval = interval, ExpiresIn = expires };

    [TestCase(7, false)]
    [TestCase(1, true)]
    public async Task PendingAndSlowDown_UseCumulativeIntervalsUntilSuccess(int initial, bool twice)
    {
        var clock = new RegressionTimeProvider();
        var issued = new GitHubOAuthToken { AccessToken = "issued-5447" };
        var responses = new Queue<GitHubOAuthToken>(twice
            ? [new() { Error = "slow_down" }, new() { Error = "slow_down" }, new() { Error = "authorization_pending" }, issued]
            : [new() { Error = "slow_down" }, new() { Error = "authorization_pending" }, new() { Error = "authorization_pending" }, issued]);
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        Task<GitHubOAuthToken> polling = TuiCloudBackup.PollAccessTokenAsync(Flow(initial), ct =>
        {
            NAssert.That(ct, Is.EqualTo(cancellation.Token));
            calls++;
            return Task.FromResult(responses.Dequeue());
        }, cancellation.Token, clock);
        int[] expected = twice ? [5, 10, 15, 15] : [7, 12, 12, 12];
        for (int i = 0; i < expected.Length; i++)
        {
            var timer = await clock.NextTimerAsync();
            NAssert.That(timer.DueTime, Is.EqualTo(TimeSpan.FromSeconds(expected[i])));
            NAssert.That(calls, Is.EqualTo(i), "No request before its delay");
            timer.Fire();
        }
        NAssert.That(await polling, Is.SameAs(issued));
        NAssert.That(calls, Is.EqualTo(4));
        NAssert.That(clock.Timers.Count, Is.EqualTo(4), "Success must not schedule another poll");
    }

    [Test]
    public async Task AuthorizationPending_ContinuesAtTheSameInterval()
    {
        var clock = new RegressionTimeProvider();
        int calls = 0;
        var polling = TuiCloudBackup.PollAccessTokenAsync(Flow(8), _ =>
            Task.FromResult(++calls == 1 ? new GitHubOAuthToken { Error = "authorization_pending" }
                : new GitHubOAuthToken { AccessToken = "pending-success" }), CancellationToken.None, clock);
        (await clock.NextTimerAsync()).Fire();
        var second = await clock.NextTimerAsync();
        NAssert.That(second.DueTime, Is.EqualTo(TimeSpan.FromSeconds(8)));
        NAssert.That(calls, Is.EqualTo(1));
        second.Fire();
        NAssert.That((await polling).AccessToken, Is.EqualTo("pending-success"));
        NAssert.That(calls, Is.EqualTo(2));
    }

    [TestCase("access_denied", "The user refused this request.")]
    [TestCase("expired_token", "The device code expired.")]
    [TestCase("unexpected_5447", "Unknown failure detail.")]
    [TestCase("access_denied", "")]
    public async Task FatalResponse_TerminatesImmediatelyWithCodeAndDescription(string error, string description)
    {
        var clock = new RegressionTimeProvider();
        int calls = 0;
        var polling = TuiCloudBackup.PollAccessTokenAsync(Flow(), _ =>
        {
            calls++;
            return Task.FromResult(new GitHubOAuthToken { Error = error, ErrorDescription = description });
        }, CancellationToken.None, clock);
        (await clock.NextTimerAsync()).Fire();
        var ex = NAssert.ThrowsAsync<InvalidOperationException>(async () => await polling)!;
        NAssert.That(ex.Message, Does.Contain(error));
        NAssert.That(ex.Message, Does.Contain(description.Length == 0 ? error : description));
        NAssert.That(calls, Is.EqualTo(1));
        NAssert.That(clock.Timers.Count, Is.EqualTo(1));
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("\t")]
    public async Task EmptyTokenResponse_FailsWithoutAnotherPoll(string accessToken)
    {
        var clock = new RegressionTimeProvider();
        int calls = 0;
        var polling = TuiCloudBackup.PollAccessTokenAsync(Flow(), _ =>
        {
            calls++;
            return Task.FromResult(new GitHubOAuthToken { AccessToken = accessToken });
        }, CancellationToken.None, clock);
        (await clock.NextTimerAsync()).Fire();
        var ex = NAssert.ThrowsAsync<InvalidOperationException>(async () => await polling)!;
        NAssert.That(ex.Message, Does.Contain("empty access token"));
        NAssert.That(calls, Is.EqualTo(1));
        NAssert.That(clock.Timers.Count, Is.EqualTo(1));
    }

    [TestCase(-1, 5)]
    [TestCase(0, 5)]
    [TestCase(5, 5)]
    [TestCase(6, 6)]
    public async Task Success_ReturnsIssuedTokenWithoutAnotherPoll(int interval, int expectedDelay)
    {
        var clock = new RegressionTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var issued = new GitHubOAuthToken { AccessToken = "success-5447" };
        int calls = 0;
        var polling = TuiCloudBackup.PollAccessTokenAsync(Flow(interval), ct =>
        {
            NAssert.That(ct, Is.EqualTo(cancellation.Token));
            calls++;
            return Task.FromResult(issued);
        }, cancellation.Token, clock);
        var timer = await clock.NextTimerAsync();
        NAssert.That(timer.DueTime, Is.EqualTo(TimeSpan.FromSeconds(expectedDelay)));
        NAssert.That(calls, Is.Zero);
        timer.Fire();
        NAssert.That(await polling, Is.SameAs(issued));
        NAssert.That(calls, Is.EqualTo(1));
        NAssert.That(clock.Timers.Count, Is.EqualTo(1));
        NAssert.That(timer.IsDisposed, Is.True);
    }

    [TestCase(0, 0)]
    [TestCase(3, 0)]
    [TestCase(12, 2)]
    public async Task ActualExpiry_IsNotExtendedToSixtySeconds(int expires, int expectedCalls)
    {
        var clock = new RegressionTimeProvider();
        int calls = 0;
        var polling = TuiCloudBackup.PollAccessTokenAsync(Flow(expires: expires), _ =>
        {
            calls++;
            return Task.FromResult(new GitHubOAuthToken { Error = "authorization_pending" });
        }, CancellationToken.None, clock);
        for (int i = 0; i <= expectedCalls && expires > 0; i++)
        {
            var timer = await clock.NextTimerAsync();
            NAssert.That(timer.DueTime, Is.EqualTo(TimeSpan.FromSeconds(i < expectedCalls ? 5 : expires - 5 * expectedCalls)));
            if (i == expectedCalls)
            {
                clock.Advance(timer.DueTime - TimeSpan.FromTicks(1));
                NAssert.That(polling.IsCompleted, Is.False, "Immediately before actual expiry");
                NAssert.That(calls, Is.EqualTo(expectedCalls));
                clock.Advance(TimeSpan.FromTicks(1));
                timer.Fire(advance: false);
            }
            else timer.Fire();
        }
        NAssert.ThrowsAsync<TimeoutException>(async () => await polling);
        NAssert.That(calls, Is.EqualTo(expectedCalls), "No request at the expiry deadline");
        NAssert.That(clock.GetElapsedTime(0), Is.EqualTo(TimeSpan.FromSeconds(expires)));
    }

    [Test]
    public async Task ExpiryDuringDelay_DoesNotSendAnotherPoll()
    {
        var clock = new RegressionTimeProvider();
        int calls = 0;
        var polling = TuiCloudBackup.PollAccessTokenAsync(Flow(expires: 12), _ =>
        {
            calls++;
            return Task.FromResult(new GitHubOAuthToken { Error = "authorization_pending" });
        }, CancellationToken.None, clock);
        var timer = await clock.NextTimerAsync();
        clock.Advance(TimeSpan.FromSeconds(13));
        timer.Fire(advance: false);
        NAssert.ThrowsAsync<TimeoutException>(async () => await polling);
        NAssert.That(calls, Is.Zero);
        NAssert.That(clock.Timers.Count, Is.EqualTo(1));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Cancellation_BeforeOrDuringDelay_DoesNotPoll(bool before)
    {
        var clock = new RegressionTimeProvider();
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        if (before) cancellation.Cancel();
        var polling = TuiCloudBackup.PollAccessTokenAsync(Flow(), _ =>
        {
            calls++;
            return Task.FromResult(new GitHubOAuthToken { AccessToken = "must-not-return" });
        }, cancellation.Token, clock);
        if (!before)
        {
            var timer = await clock.NextTimerAsync();
            cancellation.Cancel();
            NAssert.That(timer.IsDisposed, Is.True);
            timer.Fire();
        }
        NAssert.CatchAsync<OperationCanceledException>(async () => await polling);
        NAssert.That(calls, Is.Zero);
        NAssert.That(clock.Timers.Count, Is.EqualTo(before ? 0 : 1));
    }

    [Test]
    public async Task Cancellation_DuringRequest_IsForwardedAndStopsPolling()
    {
        var clock = new RegressionTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<GitHubOAuthToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var polling = TuiCloudBackup.PollAccessTokenAsync(Flow(), ct =>
        {
            calls++;
            NAssert.That(ct, Is.EqualTo(cancellation.Token));
            ct.Register(() => response.TrySetCanceled(ct));
            entered.SetResult();
            return response.Task;
        }, cancellation.Token, clock);
        (await clock.NextTimerAsync()).Fire();
        await entered.Task;
        cancellation.Cancel();
        NAssert.CatchAsync<OperationCanceledException>(async () => await polling);
        NAssert.That(calls, Is.EqualTo(1));
        NAssert.That(clock.Timers.Count, Is.EqualTo(1));
    }

    [TestCase("access_denied", "The user refused this request.", "GitHub sign-in failed (access_denied): The user refused this request.")]
    [TestCase("expired_token", "The device code expired.", "GitHub sign-in failed (expired_token): The device code expired.")]
    [TestCase("unexpected_5447", "Unknown failure detail.", "GitHub sign-in failed (unexpected_5447): Unknown failure detail.")]
    [TestCase("access_denied", "", "GitHub sign-in failed (access_denied): access_denied")]
    [TestCase("access_denied", null, "GitHub sign-in failed (access_denied): access_denied")]
    [TestCase("access_denied", "   ", "GitHub sign-in failed (access_denied): access_denied")]
    public async Task FatalResponse_UsesExactCodeAndMeaningfulDescription(string error, string? description, string expectedMessage)
    {
        var clock = new RegressionTimeProvider();
        int calls = 0;
        var polling = TuiCloudBackup.PollAccessTokenAsync(Flow(), _ =>
        {
            calls++;
            return Task.FromResult(new GitHubOAuthToken { Error = error, ErrorDescription = description });
        }, CancellationToken.None, clock);
        var timer = await clock.NextTimerAsync();
        timer.Fire();
        var ex = NAssert.ThrowsAsync<InvalidOperationException>(async () => await polling)!;
        NAssert.That(ex.Message, Is.EqualTo(expectedMessage));
        NAssert.That(calls, Is.EqualTo(1));
        NAssert.That(clock.Timers.Count, Is.EqualTo(1), "Fatal responses must not schedule a later poll");
        NAssert.That(timer.IsDisposed, Is.True);
    }
}
