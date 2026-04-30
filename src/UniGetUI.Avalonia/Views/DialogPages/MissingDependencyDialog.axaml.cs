using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.Classes;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class MissingDependencyDialog : Window
{
    private readonly ManagerDependency _dep;
    private readonly int _current;
    private readonly int _total;

    private bool _hasInstalled;
    private bool _blockClose;

    public MissingDependencyDialog(ManagerDependency dep, int current, int total)
    {
        _dep = dep;
        _current = current;
        _total = total;

        InitializeComponent();

        bool notFirstTime =
            Settings.GetDictionaryItem<string, string>(Settings.K.DependencyManagement, dep.Name)
            == "attempted";
        Settings.SetDictionaryItem(Settings.K.DependencyManagement, dep.Name, "attempted");

        Title = CoreTools.Translate("Missing dependency")
            + (total > 1 ? $" ({current}/{total})" : "");

        TitleBlock.Text = Title;
        DescBlock.Text = CoreTools.Translate(
            "UniGetUI requires {0} to operate, but it was not found on your system.", dep.Name);
        InfoBlock.Text = CoreTools.Translate(
            "Click on Install to begin the installation process. If you skip the installation, UniGetUI may not work as expected.");
        CommandInfoBlock.Text = CoreTools.Translate(
            "Alternatively, you can also install {0} by running the following command in a Windows PowerShell prompt:",
            dep.Name);
        CommandBlock.Text = dep.FancyInstallCommand;
        InstallButton.Content = CoreTools.Translate("Install {0}", dep.Name);
        SkipButton.Content = CoreTools.Translate("Not right now");

        if (notFirstTime)
        {
            DontShowCheck.Content = CoreTools.Translate("Do not show this dialog again for {0}", dep.Name);
            DontShowCheck.IsVisible = true;
            DontShowCheck.IsCheckedChanged += (_, _) =>
            {
                var val = DontShowCheck.IsChecked == true ? "skipped" : "attempted";
                Settings.SetDictionaryItem(Settings.K.DependencyManagement, dep.Name, val);
            };
        }

        SkipButton.Click += (_, _) => { if (!_blockClose) Close(); };
        InstallButton.Click += async (_, _) => await OnInstallClickedAsync();

        Closing += (_, e) => e.Cancel = _blockClose;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() => InstallButton.Focus(), DispatcherPriority.Background);
    }

    private async Task OnInstallClickedAsync()
    {
        if (!_hasInstalled)
        {
            await RunInstallAsync();
        }
        else if (_current == _total)
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                ?.Shutdown();
        }
        else
        {
            Close();
        }
    }

    private async Task RunInstallAsync()
    {
        InstallButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        DontShowCheck.IsEnabled = false;
        ProgressBar.IsIndeterminate = true;
        ProgressBar.IsVisible = true;
        _blockClose = true;

        InfoBlock.Text = CoreTools.Translate(
            "Please wait while {0} is being installed. A black window may show up. Please wait until it closes.",
            _dep.Name);

        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _dep.InstallFileName,
                    Arguments = _dep.InstallArguments,
                },
            };
            p.Start();
            await p.WaitForExitAsync();

            _hasInstalled = true;
            _blockClose = false;
            ProgressBar.IsIndeterminate = false;
            ProgressBar.IsVisible = false;
            InstallButton.IsEnabled = true;
            SkipButton.IsEnabled = true;

            if (_current < _total)
            {
                InfoBlock.Text =
                    CoreTools.Translate("{0} has been installed successfully.", _dep.Name)
                    + " "
                    + CoreTools.Translate("Please click on \"Continue\" to continue", _dep.Name);
                InstallButton.Content = CoreTools.Translate("Continue");
                SkipButton.IsVisible = false;
            }
            else
            {
                InfoBlock.Text = CoreTools.Translate(
                    "{0} has been installed successfully. It is recommended to restart UniGetUI to finish the installation",
                    _dep.Name);
                InstallButton.Content = CoreTools.Translate("Restart UniGetUI");
                SkipButton.Content = CoreTools.Translate("Restart later");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            _hasInstalled = true;
            _blockClose = false;
            ProgressBar.IsIndeterminate = false;
            ProgressBar.IsVisible = false;
            InstallButton.IsEnabled = true;
            SkipButton.IsEnabled = true;

            InfoBlock.Text =
                CoreTools.Translate("An error occurred:") + " " + ex.Message + "\n"
                + CoreTools.Translate("Please click on \"Continue\" to continue");
            InstallButton.Content = _current < _total
                ? CoreTools.Translate("Continue")
                : CoreTools.Translate("Close");
            SkipButton.IsVisible = false;
        }
    }
}
