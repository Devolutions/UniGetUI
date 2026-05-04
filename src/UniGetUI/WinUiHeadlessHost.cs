using UniGetUI.Core.Tools;
using UniGetUI.Interface;
using UniGetUI.PackageEngine;

namespace UniGetUI;

internal static class WinUiHeadlessHost
{
    public static Task<int> RunAsync(string[] args)
    {
        return HeadlessIpcHost.RunAsync(async () =>
        {
            CoreTools.ReloadLanguageEngineInstance();
            UniGetUI.Interface.MainWindow.ApplyProxyVariableToProcess();
            PEInterface.LoadLoaders();
            await Task.WhenAll(
                Task.Run(PEInterface.LoadManagers),
                MainApp.LoadGSudoAsync()
            );
        });
    }
}
