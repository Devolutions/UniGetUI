using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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

        bool isConfirmed = false;
        var dialog = new Window
        {
            Width = packages.Count == 1 ? 520 : 560,
            Height = packages.Count == 1 ? 220 : 380,
            MinWidth = 420,
            MinHeight = packages.Count == 1 ? 220 : 300,
            CanResize = packages.Count > 1,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = CoreTools.Translate("Are you sure?"),
        };

        var titleBlock = new TextBlock
        {
            Text = CoreTools.Translate("Are you sure?"),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

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

        var root = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 14,
        };

        var headerPanel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                titleBlock,
                messageBlock,
            },
        };
        Grid.SetRow(headerPanel, 0);
        root.Children.Add(headerPanel);

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
            Grid.SetRow(packageListViewer, 1);
            root.Children.Add(packageListViewer);
        }

        var noButton = new Button
        {
            Content = CoreTools.Translate("No"),
            MinWidth = 100,
        };
        noButton.Click += (_, _) => dialog.Close();

        var yesButton = new Button
        {
            Content = CoreTools.Translate("Yes"),
            MinWidth = 100,
        };
        yesButton.Classes.Add("accent");
        yesButton.Click += (_, _) =>
        {
            isConfirmed = true;
            dialog.Close();
        };

        var footerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                noButton,
                yesButton,
            },
        };
        Grid.SetRow(footerPanel, 2);
        root.Children.Add(footerPanel);

        dialog.Content = root;
        await dialog.ShowDialog(owner);
        return isConfirmed;
    }
}
