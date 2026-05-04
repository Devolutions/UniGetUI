using UniGetUI.Core.Data;
using UniGetUI.Interface;

namespace UniGetUI.Tests;

public sealed class IpcTransportTests : IDisposable
{
    private readonly string _dataDirectory = Path.Join(
        Path.GetTempPath(),
        "UniGetUI.Tests",
        Guid.NewGuid().ToString("N")
    );

    public IpcTransportTests()
    {
        CoreData.TEST_DataDirectoryOverride = _dataDirectory;
        Directory.CreateDirectory(_dataDirectory);
    }

    [Fact]
    public void DefaultTransportUsesNamedPipeOnAllPlatforms()
    {
        Assert.Equal(IpcTransportKind.NamedPipe, IpcTransportOptions.Default.TransportKind);
        Assert.Equal(IpcTransportOptions.DefaultTcpPort, IpcTransportOptions.Default.TcpPort);
        Assert.Equal(
            IpcTransportOptions.DefaultNamedPipeName,
            IpcTransportOptions.Default.NamedPipeName
        );
    }

    [Fact]
    public void LoadForServerParsesNamedPipeOverrides()
    {
        var options = IpcTransportOptions.LoadForServer(
            [
                "UniGetUI.exe",
                IpcTransportOptions.TransportArgument,
                "named-pipe",
                IpcTransportOptions.NamedPipeArgument,
                "Contoso.Pipe",
                IpcTransportOptions.TcpPortArgument,
                "7258",
            ]
        );

        Assert.Equal(IpcTransportKind.NamedPipe, options.TransportKind);
        Assert.Equal("Contoso.Pipe", options.NamedPipeName);
        Assert.Equal(7258, options.TcpPort);
    }

    [Fact]
    public void ResolveUnixSocketPathUsesTmpForRelativePipeNames()
    {
        string socketPath = IpcTransportOptions.ResolveUnixSocketPath(
            IpcTransportOptions.DefaultNamedPipeName
        );

        Assert.Equal("/tmp/UniGetUI.IPC", socketPath);
    }

    [Fact]
    public void ResolveUnixSocketPathPreservesAbsolutePaths()
    {
        const string socketPath = "/tmp/custom-unigetui.sock";

        Assert.Equal(socketPath, IpcTransportOptions.ResolveUnixSocketPath(socketPath));
    }

    [Fact]
    public void SameUserUnixSocketModeUsesOwnerOnlyPermissions()
    {
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            IpcTransportOptions.SameUserUnixSocketMode
        );
    }

    [Fact]
    public void LoadForServerRejectsAbsolutePipePathOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var options = IpcTransportOptions.LoadForServer(
            [
                "UniGetUI.exe",
                IpcTransportOptions.TransportArgument,
                "named-pipe",
                IpcTransportOptions.NamedPipeArgument,
                "/tmp/custom-unigetui.sock",
            ]
        );

        Assert.Equal(IpcTransportOptions.DefaultNamedPipeName, options.NamedPipeName);
    }

    [Fact]
    public void LoadForServerAcceptsAbsolutePipePathOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const string socketPath = "/tmp/custom-unigetui.sock";
        var options = IpcTransportOptions.LoadForServer(
            [
                "UniGetUI.exe",
                IpcTransportOptions.TransportArgument,
                "named-pipe",
                IpcTransportOptions.NamedPipeArgument,
                socketPath,
            ]
        );

        Assert.Equal(socketPath, options.NamedPipeName);
        Assert.Equal(socketPath, options.NamedPipePath);
    }

    [Fact]
    public void LoadForClientUsesPersistedEndpointMetadataWhenNoOverridesExist()
    {
        var persisted = new IpcTransportOptions(
            IpcTransportKind.NamedPipe,
            7058,
            "Persisted.Pipe"
        );
        persisted.Persist(
            sessionId: "gui-session",
            token: "gui-token",
            sessionKind: IpcTransportOptions.GuiSessionKind,
            processId: Environment.ProcessId
        );

        var options = IpcTransportOptions.LoadForClient(["UniGetUI.exe"]);

        Assert.Equal(IpcTransportKind.NamedPipe, options.TransportKind);
        Assert.Equal("Persisted.Pipe", options.NamedPipeName);
    }

    [Fact]
    public void LoadForClientPrefersHeadlessPersistedSessionWhenMultipleSessionsExist()
    {
        var guiOptions = new IpcTransportOptions(
            IpcTransportKind.Tcp,
            7058,
            IpcTransportOptions.DefaultNamedPipeName
        );
        guiOptions.Persist(
            sessionId: "gui-session",
            token: "gui-token",
            sessionKind: IpcTransportOptions.GuiSessionKind,
            processId: Environment.ProcessId
        );

        var headlessOptions = new IpcTransportOptions(
            IpcTransportKind.NamedPipe,
            7058,
            "Headless.Pipe"
        );
        headlessOptions.Persist(
            sessionId: "headless-session",
            token: "headless-token",
            sessionKind: IpcTransportOptions.HeadlessSessionKind,
            processId: Environment.ProcessId
        );

        var options = IpcTransportOptions.LoadForClient(["UniGetUI.exe"]);

        Assert.Equal(IpcTransportKind.NamedPipe, options.TransportKind);
        Assert.Equal("Headless.Pipe", options.NamedPipeName);
    }

    public void Dispose()
    {
        IpcTransportOptions.DeletePersistedMetadata();
        CoreData.TEST_DataDirectoryOverride = null;

        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }
}
