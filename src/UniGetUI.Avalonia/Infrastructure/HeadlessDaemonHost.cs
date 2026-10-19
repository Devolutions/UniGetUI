using UniGetUI.Interface;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Classes.Packages.Classes;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class HeadlessDaemonHost
{
    public static async Task<int> RunAsync()
    {
        return await HeadlessIpcHost.RunAsync(async () =>
        {
            ProcessEnvironmentConfigurator.PrepareForCurrentPlatform();
            PEInterface.LoadLoaders();
            await Task.WhenAll(
                Task.Run(ElevatorLoader.LoadAsync),
                Task.Run(PEInterface.LoadManagers));
            await Task.Run(() => AutoUpdatesMigration.RunOnce(PEInterface.Managers));
            MaintenanceScheduler.StartHeadless();
        });
    }
}
