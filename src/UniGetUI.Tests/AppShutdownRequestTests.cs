using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using UniGetUI.Avalonia;
using UniGetUI.Interface;

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
        int signalsReceived = 0;
        int quits = 0;

        App.HandleTerminationSignal(context, ref signalsReceived, () => quits++);

        Assert.True(context.Cancel);
        Assert.Equal(1, quits);
    }

    [Theory]
    [InlineData(PosixSignal.SIGTERM)]
    [InlineData(PosixSignal.SIGINT)]
    public void SecondTerminationSignal_EndsTheProcess(PosixSignal signal)
    {
        var first = new PosixSignalContext(signal);
        var second = new PosixSignalContext(signal);
        int signalsReceived = 0;
        int quits = 0;

        App.HandleTerminationSignal(first, ref signalsReceived, () => quits++);
        App.HandleTerminationSignal(second, ref signalsReceived, () => quits++);

        Assert.True(first.Cancel);
        Assert.False(second.Cancel);
        Assert.Equal(1, quits);
    }

    [Fact]
    public void IpcHostThatLeavesSignalsToTheApp_DoesNotUseTheConsoleLifetime()
    {
        var builder = WebApplication.CreateSlimBuilder();
        IpcServer.LeaveTerminationSignalsToTheApp(builder.Services);
        using var app = builder.Build();

        Assert.IsNotType<ConsoleLifetime>(app.Services.GetRequiredService<IHostLifetime>());
    }
}
