using System.Collections.Generic;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Logging;
using UniGetUI.Shared;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class StartupArgumentProcessor
{
    public static void WarnIfBundlesIgnored(IReadOnlyList<string> args, string reason)
    {
        if (args.Count == 0)
            return;

        List<string> bundles = StartupBundleArguments.Resolve(args, Environment.CurrentDirectory);
        foreach (string bundle in bundles)
        {
            Logger.Warn($"Ignoring the package bundle {bundle}: {reason}");
        }
    }

    public static async Task ProcessAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return;
        }

        List<string> bundles = StartupBundleArguments.Resolve(args, Environment.CurrentDirectory);
        if (bundles.Count == 0)
        {
            return;
        }

        if (bundles.Count > 1)
        {
            Logger.Warn(
                $"{bundles.Count} package bundles were passed on the command line; only {bundles[0]} will be loaded");
        }

        if (!await AvaloniaBootstrapper.Initialized)
        {
            Logger.Warn(
                $"Could not load the package bundle {bundles[0]}: UniGetUI failed to finish initializing");
            return;
        }

        if (MainWindow.Instance is not { } window)
        {
            Logger.Warn($"Could not load the package bundle {bundles[0]}: the main window is not available");
            return;
        }

        if (window.DataContext is not MainWindowViewModel viewModel)
        {
            Logger.Warn($"Could not load the package bundle {bundles[0]}: the main window has no view model");
            return;
        }

        if (!window.IsVisible)
        {
            window.ShowFromTray();
        }

        Logger.ImportantInfo($"Loading the package bundle {bundles[0]} requested on the command line");
        await viewModel.LoadBundleFromFileAsync(bundles[0]);
    }
}
