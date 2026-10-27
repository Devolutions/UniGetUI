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

    /// <summary>
    /// Returns the first <c>unigetui://package</c> link in <paramref name="args"/>, logging and
    /// skipping malformed ones.
    /// </summary>
    internal static PackageLink? FindPackageLink(IReadOnlyList<string> args)
    {
        foreach (string arg in args)
        {
            if (!PackageLink.IsPackageLink(arg))
                continue;

            if (PackageLink.TryParse(arg) is { } link)
                return link;

            Logger.Warn($"Ignoring a malformed package link: {arg}");
        }

        return null;
    }

    public static async Task ProcessAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return;
        }

        // A protocol launch carries nothing but its link, so a link and a bundle never come together.
        PackageLink? link = FindPackageLink(args);
        List<string> bundles = link is null
            ? StartupBundleArguments.Resolve(args, Environment.CurrentDirectory)
            : [];
        if (link is null && bundles.Count == 0)
        {
            return;
        }

        string request = link is not null
            ? $"the package link {link.ToUri()}"
            : $"the package bundle {bundles[0]}";

        if (bundles.Count > 1)
        {
            Logger.Warn(
                $"{bundles.Count} package bundles were passed on the command line; only {bundles[0]} will be loaded");
        }

        if (!await AvaloniaBootstrapper.Initialized)
        {
            Logger.Warn($"Could not open {request}: UniGetUI failed to finish initializing");
            return;
        }

        if (MainWindow.Instance is not { } window)
        {
            Logger.Warn($"Could not open {request}: the main window is not available");
            return;
        }

        if (window.DataContext is not MainWindowViewModel viewModel)
        {
            Logger.Warn($"Could not open {request}: the main window has no view model");
            return;
        }

        if (!window.IsVisible)
        {
            window.ShowFromTray();
        }

        if (link is not null)
        {
            await PackageLinkHandler.OpenAsync(link, window);
            return;
        }

        Logger.ImportantInfo($"Loading the package bundle {bundles[0]} requested on the command line");
        await viewModel.LoadBundleFromFileAsync(bundles[0]);
    }
}
