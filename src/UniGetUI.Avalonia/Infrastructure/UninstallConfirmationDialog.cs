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

    public static async Task<bool> ConfirmAsync(Window owner, IReadOnlyList<IPackage> packages)
    {
        if (packages.Count == 0)
        {
            return false;
        }

        var messageBlock = new TextBlock
        {
            Text = packages.Count == 1
                ? CoreTools.Translate("Do you really want to uninstall {0}?", packages[0].Name)
                : CoreTools.Translate(
                    "Do you really want to uninstall the following {0} packages?",
                    packages.Count),
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
