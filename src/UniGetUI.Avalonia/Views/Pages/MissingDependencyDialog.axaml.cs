using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.Classes;

namespace UniGetUI.Avalonia.Views.Pages;

/// <summary>
/// Modal dialog shown once per missing <see cref="ManagerDependency"/>.
/// Offers an automated install, a "not right now" dismiss, and a persistent
/// "do not show again" checkbox once the user has already been prompted once.
/// </summary>
public partial class MissingDependencyDialog : Window
{
    private readonly ManagerDependency _dep;
    private bool _installing;

    public MissingDependencyDialog()
    {
        _dep = default;
        InitializeComponent();
        IsEnabled = false;
    }

    public MissingDependencyDialog(ManagerDependency dep, int current, int total)
    {
        _dep = dep;
        InitializeComponent();

        Title = CoreTools.Translate("Missing dependency")
            + (total > 1 ? $" ({current}/{total})" : "");

        DescriptionBlock.Text = CoreTools.Translate(
            "UniGetUI requires {0} to operate, but it was not found on your system.",
            dep.Name);

        SubDescriptionBlock.Text = CoreTools.Translate(
            "Click on Install to begin the installation process. If you skip the installation, UniGetUI may not work as expected.");

        if (!string.IsNullOrWhiteSpace(dep.FancyInstallCommand))
        {
            CommandLabelBlock.Text = CoreTools.Translate(
                "Alternatively, you can install {0} by running the following command in a PowerShell prompt:",
                dep.Name);
            CommandBlock.Text = dep.FancyInstallCommand;
            CommandBlock.IsVisible = true;
        }
        else
        {
            CommandLabelBlock.IsVisible = false;
        }

        bool notFirstTime =
            Settings.GetDictionaryItem<string, string>(Settings.K.DependencyManagement, dep.Name)
            == "attempted";
        Settings.SetDictionaryItem(Settings.K.DependencyManagement, dep.Name, "attempted");

        if (notFirstTime)
        {
            DoNotShowAgainCheckBox.IsVisible = true;
            DoNotShowAgainCheckBox.Content =
                CoreTools.Translate("Do not show this dialog again for {0}", dep.Name);
        }

        InstallButton.Content = CoreTools.Translate("Install {0}", dep.Name);
        SkipButton.Content = CoreTools.Translate("Not right now");
    }

    private async void InstallButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_installing) return;
        _installing = true;

        InstallButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        InstallButton.Content = CoreTools.Translate("Please wait");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _dep.InstallFileName,
                Arguments = _dep.InstallArguments,
                UseShellExecute = true,
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
                await proc.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to start dependency installer for {_dep.Name}: {ex.Message}");
        }

        Close();
    }

    private void SkipButton_OnClick(object? sender, RoutedEventArgs e) => Close();

    private void DoNotShowAgainCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        Settings.SetDictionaryItem(
            Settings.K.DependencyManagement,
            _dep.Name,
            DoNotShowAgainCheckBox.IsChecked == true ? "skipped" : "attempted");
    }
}
