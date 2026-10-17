using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;
using UniGetUI.Avalonia;

namespace UniGetUI.Tests;

public class AppShutdownRequestTests
{
    [Fact]
    public void SessionManagerRequest_WhenTheQuitWouldNotAsk_IsLetThrough()
    {
        var e = new ShutdownRequestedEventArgs();
        int quits = 0;

        App.HandleShutdownRequest(e, isQuitting: false, fromSessionManager: true, canQuitWithoutAsking: true, () => quits++);

        Assert.False(e.Cancel);
        Assert.Equal(0, quits);
    }

    [Fact]
    public void SessionManagerRequest_WhenTheQuitWouldAsk_WaitsForThePrompt()
    {
        var e = new ShutdownRequestedEventArgs();
        int quits = 0;

        App.HandleShutdownRequest(e, isQuitting: false, fromSessionManager: true, canQuitWithoutAsking: false, () => quits++);

        Assert.True(e.Cancel);
        Assert.Equal(1, quits);
    }

    [Fact]
    public void AppQuitRequest_GoesThroughTheQuitPath()
    {
        var e = new ShutdownRequestedEventArgs();
        int quits = 0;

        App.HandleShutdownRequest(e, isQuitting: false, fromSessionManager: false, canQuitWithoutAsking: true, () => quits++);

        Assert.True(e.Cancel);
        Assert.Equal(1, quits);
    }

    [Fact]
    public void RequestWhileAlreadyQuitting_IsLetThrough()
    {
        var e = new ShutdownRequestedEventArgs();
        int quits = 0;

        App.HandleShutdownRequest(e, isQuitting: true, fromSessionManager: false, canQuitWithoutAsking: false, () => quits++);

        Assert.False(e.Cancel);
        Assert.Equal(0, quits);
    }

    [Theory]
    [InlineData(PosixSignal.SIGTERM)]
    [InlineData(PosixSignal.SIGINT)]
    public void TerminationSignal_QuitsInsteadOfEndingTheProcess(PosixSignal signal)
    {
        var context = new PosixSignalContext(signal);
        int quits = 0;

        App.HandleTerminationSignal(context, () => quits++);

        Assert.True(context.Cancel);
        Assert.Equal(1, quits);
    }
}
