using Avalonia.Controls;
using Avalonia.Media;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class UninstallConfirmationDialog
{
    public static Task<bool> ConfirmAsync(Window owner, IPackage package)
    {
        return ConfirmAsync(owner, [package]);
    }

    public static Task<bool> ConfirmAsync(Window owner, IReadOnlyList<IPackage> packages)
    {
        if (packages.Count == 0)
        {
            return Task.FromResult(false);
        }

        return PackageListConfirmationDialog.ConfirmAsync(
            owner,
            packages,
            packages.Count == 1
                ? CoreTools.Translate("Do you really want to uninstall {0}?", packages[0].Name)
                : CoreTools.Translate(
                    "Do you really want to uninstall the following {0} packages?",
                    packages.Count));
    }
}

internal static class IgnoreUpdatesConfirmationDialog
{
    /// <summary>
    /// Asks before ignoring the updates of several packages at once. Every row of the Updates page is checked by
    /// default, so one click on "Ignore selected packages" would otherwise ignore every update without a word.
    /// </summary>
    public static Task<bool> ConfirmAsync(Window owner, IReadOnlyList<IPackage> packages)
    {
        if (packages.Count == 0)
        {
            return Task.FromResult(false);
        }

        if (packages.Count == 1)
        {
            return Task.FromResult(true);
        }

        return PackageListConfirmationDialog.ConfirmAsync(
            owner,
            packages,
            CoreTools.Translate(
                "Do you really want to ignore updates for the following {0} packages?",
                packages.Count));
    }
}

internal static class PackageListConfirmationDialog
{
    public static async Task<bool> ConfirmAsync(
        Window owner,
        IReadOnlyList<IPackage> packages,
        string message)
    {
        var messageBlock = new TextBlock
        {
            Text = message,
            Opacity = 0.82,
            TextWrapping = TextWrapping.Wrap,
        };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(messageBlock);

        if (packages.Count > 1)
        {
            string packageListText = string.Join(
                Environment.NewLine,
                packages
                    .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(package => "* " + package.Name));

            var packageListBlock = new TextBlock
            {
                Text = packageListText,
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
            };

            var packageListViewer = new ScrollViewer
            {
                MaxHeight = 220,
                Content = packageListBlock,
            };
            body.Children.Add(packageListViewer);
        }

        var dialog = new ImmersiveConfirmationDialog(
            CoreTools.Translate("Are you sure?"),
            body,
            CoreTools.Translate("Yes"),
            CoreTools.Translate("No"))
        {
            MaxWidth = packages.Count == 1 ? 520 : 560,
            MaxHeight = packages.Count == 1 ? 220 : 380,
        };
        await dialog.ShowDialog(owner);
        return dialog.Result is true;
    }
}
