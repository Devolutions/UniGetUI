using UniGetUI.Interface;
using UniGetUI.PackageEngine;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class HeadlessDaemonHost
{
    public static async Task<int> RunAsync()
    {
        return await HeadlessIpcHost.RunAsync(async () =>
        {
            ProcessEnvironmentConfigurator.PrepareForCurrentPlatform();
            PEInterface.LoadLoaders();
            await Task.Run(PEInterface.LoadManagers);
        });
    }
}
