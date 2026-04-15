using ABI.Microsoft.UI.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Dialogs;
using UniGetUI.Interface.Enums;
using UniGetUI.Interface.Telemetry;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.Pages.SettingsPages.GeneralPages;
using Windows.UI.Text;

namespace UniGetUI.Pages.DialogPages;

public static partial class DialogHelper
{
    /// <summary>
    /// Will update the Installation Options for the given Package, and will return whether the user choose to continue
    /// </summary>
    public static async Task<bool> ShowInstallatOptions_Continue(
        IPackage package,
        OperationType operation
    )
    {
        var options = await InstallOptionsFactory.LoadForPackageAsync(package);
        var (dialogOptions, dialogResult) = await ShowInstallOptions(package, operation, options);

        if (dialogResult is not ContentDialogResult.None)
        {
            Logger.Debug($"Saving updated options for package {package.Id}");
            await InstallOptionsFactory.SaveForPackageAsync(dialogOptions, package);
        }
        else
        {
            Logger.Debug(
                $"Install options dialog for {package.Id} was canceled, no changes will be saved"
            );
        }

        return dialogResult is ContentDialogResult.Secondary;
    }

    /// <summary>
    /// Will update the Installation Options for the given imported package
    /// </summary>
    public static async Task<ContentDialogResult> ShowInstallOptions_ImportedPackage(
        ImportedPackage importedPackage
    )
    {
        var (options, dialogResult) = await ShowInstallOptions(
            importedPackage,
            OperationType.None,
            importedPackage.installation_options.Copy()
        );

        if (dialogResult != ContentDialogResult.None)
        {
            importedPackage.installation_options = options;
            importedPackage.FirePackageVersionChangedEvent();
        }

        return dialogResult;
    }

    private static async Task<(InstallOptions, ContentDialogResult)> ShowInstallOptions(
        IPackage package,
        OperationType operation,
        InstallOptions options
    )
    {
        InstallOptionsPage OptionsPage = new(package, operation, options);

        ContentDialog OptionsDialog = DialogFactory.Create_AsWindow(true, true);

        OptionsDialog.SecondaryButtonText = operation switch
        {
            OperationType.Install => CoreTools.Translate("Install"),
            OperationType.Uninstall => CoreTools.Translate("Uninstall"),
            OperationType.Update => CoreTools.Translate("Update"),
            _ => "",
        };
        OptionsDialog.PrimaryButtonText = CoreTools.Translate("Save and close");
        OptionsDialog.DefaultButton = ContentDialogButton.Secondary;
        // OptionsDialog.Title = CoreTools.Translate("{0} installation options", package.Name);
        OptionsDialog.Content = OptionsPage;

        OptionsPage.Close += (_, _) =>
        {
            OptionsDialog.Hide();
        };

        ContentDialogResult result = await ShowDialogAsync(OptionsDialog);
        return (await OptionsPage.GetUpdatedOptions(), result);
    }

    public static async Task ShowPackageDetails(
        IPackage package,
        OperationType operation,
        TEL_InstallReferral referral
    )
    {
        PackageDetailsPage DetailsPage = new(package, operation, referral);

        ContentDialog DetailsDialog = DialogFactory.Create_AsWindow(false);
        DetailsDialog.Content = DetailsPage;
        DetailsPage.Close += (_, _) =>
        {
            DetailsDialog.Hide();
        };

        await ShowDialogAsync(DetailsDialog);
    }

    public static async Task<bool> ConfirmUninstallation(IPackage package)
    {
        ContentDialog dialog = DialogFactory.Create();
        dialog.Title = CoreTools.Translate("Are you sure?");
        dialog.PrimaryButtonText = CoreTools.Translate("Yes");
        dialog.SecondaryButtonText = CoreTools.Translate("No");
        dialog.DefaultButton = ContentDialogButton.Secondary;
        dialog.Content = CoreTools.Translate("Do you really want to uninstall {0}?", package.Name);

        return await ShowDialogAsync(dialog) is ContentDialogResult.Primary;
    }

    public static async Task<bool> ConfirmUninstallation(IReadOnlyList<IPackage> packages)
    {
        if (!packages.Any())
        {
            return false;
        }

        if (packages.Count == 1)
        {
            return await ConfirmUninstallation(packages[0]);
        }

        ContentDialog dialog = DialogFactory.Create();
        dialog.Title = CoreTools.Translate("Are you sure?");
        dialog.PrimaryButtonText = CoreTools.Translate("Yes");
        dialog.SecondaryButtonText = CoreTools.Translate("No");
        dialog.DefaultButton = ContentDialogButton.Secondary;

        StackPanel p = new();
        p.Children.Add(
            new TextBlock
            {
                Text = CoreTools.Translate(
                    "Do you really want to uninstall the following {0} packages?",
                    packages.Count
                ),
                Margin = new Thickness(0, 0, 0, 5),
            }
        );

        string pkgList = "";
        foreach (IPackage package in packages)
        {
            pkgList += " ● " + package.Name + "\x0a";
        }

        TextBlock PackageListTextBlock = new()
        {
            FontFamily = new FontFamily("Consolas"),
            Text = pkgList,
        };
        p.Children.Add(new ScrollView { Content = PackageListTextBlock, MaxHeight = 200 });

        dialog.Content = p;

        return await ShowDialogAsync(dialog) is ContentDialogResult.Primary;
    }

    public static async Task ShowBundleSecurityReport(
        Dictionary<string, List<BundleReportEntry>> packageReport
    )
    {
        var dialog = DialogFactory.Create_AsWindow(true, true);
        Brush bad = new SolidColorBrush(Colors.PaleVioletRed);
        Brush good = new SolidColorBrush(Colors.Gold);

        if (Window.NavigationPage.ActualTheme is ElementTheme.Light)
        {
            bad = new SolidColorBrush(Colors.Red);
            good = new SolidColorBrush(Colors.DarkGoldenrod);
        }

        var title = CoreTools.Translate("Bundle security report");
        dialog.Title = title;
        Hyperlink a;
        Paragraph p = new();

        foreach (var pair in packageReport)
        {
            p.Inlines.Add(
                new Run()
                {
                    Text = $" - {CoreTools.Translate("Package")}: {pair.Key}:\n",
                    FontFamily = new("Consolas"),
                }
            );

            foreach (var issue in pair.Value)
            {
                p.Inlines.Add(
                    new Run()
                    {
                        Text = $"   * {issue.Line}\n",
                        FontFamily = new("Consolas"),
                        Foreground = issue.Allowed ? bad : good,
                    }
                );
            }
            p.Inlines.Add(new LineBreak());
        }

        dialog.Content = new ScrollViewer()
        {
            MaxWidth = 800,
            Background = (Brush)
                Application.Current.Resources["ApplicationPageBackgroundThemeBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Content = new RichTextBlock()
            {
                Blocks =
                {
                    new Paragraph()
                    {
                        Inlines =
                        {
                            new Run()
                            {
                                Text = CoreTools.Translate(
                                    "This package bundle had some settings that are potentially dangerous, and may be ignored by default."
                                ),
                            },
                            new Run()
                            {
                                Text =
                                    "\n - "
                                    + CoreTools.Translate(
                                        "Entries that show in YELLOW will be IGNORED."
                                    ),
                                Foreground = good,
                            },
                            new Run()
                            {
                                Text =
                                    "\n - "
                                    + CoreTools.Translate(
                                        "Entries that show in RED will be IMPORTED."
                                    ),
                                Foreground = bad,
                            },
                            new Run()
                            {
                                Text =
                                    "\n"
                                    + CoreTools.Translate(
                                        "You can change this behavior on UniGetUI security settings."
                                    )
                                    + " ",
                            },
                            (
                                a = new Hyperlink
                                {
                                    Inlines =
                                    {
                                        new Run()
                                        {
                                            Text = CoreTools.Translate(
                                                "Open UniGetUI security settings"
                                            ),
                                        },
                                    },
                                }
                            ),
                            new LineBreak(),
                            new Run()
                            {
                                Text = CoreTools.Translate(
                                    "Should you modify the security settings, you will need to open the bundle again for the changes to take effect."
                                ),
                            },
                            new LineBreak(),
                            new LineBreak(),
                            new Run() { Text = CoreTools.Translate("Details of the report:") },
                            new LineBreak(),
                        },
                    },
                    p,
                },
            },
        };
        a.Click += (_, _) =>
        {
            dialog.Hide();
            Window.NavigationPage.OpenSettingsPage(typeof(Administrator));
        };
        dialog.SecondaryButtonText = CoreTools.Translate("Close");
        await ShowDialogAsync(dialog);
    }

    /// <summary>
    /// Returns true if the user confirms to lose unsaved changes, and wants to proceed with the creation of a new bundle
    /// </summary>
    /// <returns></returns>
    public static async Task<bool> AskLoseChangesAndCreateBundle()
    {
        RichTextBlock rtb = new();
        var p = new Paragraph();
        rtb.Blocks.Add(p);
        p.Inlines.Add(
            new Run
            {
                Text = CoreTools.Translate(
                    "Are you sure you want to create a new package bundle? "
                ),
            }
        );
        p.Inlines.Add(new LineBreak());
        p.Inlines.Add(
            new Run
            {
                Text = CoreTools.Translate("Any unsaved changes will be lost"),
                FontWeight = new FontWeight(600),
            }
        );

        ContentDialog dialog = DialogFactory.Create();
        dialog.Title = CoreTools.Translate("Warning!");
        dialog.Content = rtb;
        dialog.DefaultButton = ContentDialogButton.Secondary;
        dialog.PrimaryButtonText = CoreTools.Translate("Yes");
        dialog.SecondaryButtonText = CoreTools.Translate("No");

        return await DialogHelper.ShowDialogAsync(dialog) is ContentDialogResult.Primary;
    }
}
