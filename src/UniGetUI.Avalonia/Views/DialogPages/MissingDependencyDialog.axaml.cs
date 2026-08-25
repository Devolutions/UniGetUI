using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.Classes;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class MissingDependencyDialog : UniGetUI.Avalonia.Views.DialogPages.ImmersiveDialog
{
    private const int MaxRetainedOutputLines = 200;
    private const int ShownOutputLines = 12;

    private readonly ManagerDependency _dep;
    private readonly int _current;
    private readonly int _total;

    private readonly object _outputLock = new();
    private readonly List<string> _output = [];
    private string? _pendingOutputLine;

    private Process? _installProcess;
    private bool _hasInstalled;
    private bool _blockClose;
    private bool _installRunning;
    private bool _canceled;

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

        DescBlock.Text = CoreTools.Translate(
            "UniGetUI requires {0} to operate, but it was not found on your system.", dep.Name);
        InfoBlock.Text = CoreTools.Translate(
            "Click on Install to begin the installation process. If you skip the installation, UniGetUI may not work as expected.");
        CommandInfoBlock.Text = CoreTools.Translate(
            "Alternatively, you can also install {0} by running the following command in a Windows PowerShell prompt:",
            dep.Name);
        CommandBlock.Text = dep.FancyInstallCommand;
        SetButtonLabel(InstallButton, CoreTools.Translate("Install {0}", dep.Name));
        SetButtonLabel(SkipButton, CoreTools.Translate("Not right now"));

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

        SkipButton.Click += (_, _) =>
        {
            if (_installRunning)
                CancelInstall();
            else if (!_blockClose)
                Close();
        };
        InstallButton.Click += async (_, _) => await OnInstallClickedAsync();

        Closing += (_, e) => e.Cancel = _blockClose;
    }

    private static void SetButtonLabel(Button button, string label)
    {
        button.Content = label;
        AutomationProperties.SetName(button, label);
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
            AppRestartHelper.Restart();
        }
        else
        {
            Close();
        }
    }

    private async Task RunInstallAsync()
    {
        InstallButton.IsEnabled = false;
        SkipButton.IsEnabled = true;
        SkipButton.IsVisible = true;
        SetButtonLabel(SkipButton, CoreTools.Translate("Cancel"));
        DontShowCheck.IsEnabled = false;
        OutputBorder.IsVisible = false;
        OutputBlock.Text = "";
        ProgressBar.IsIndeterminate = true;
        ProgressBar.IsVisible = true;
        _blockClose = true;
        _installRunning = true;
        _canceled = false;

        InfoBlock.Text = CoreTools.Translate(
            "Please wait while {0} is being installed. This may take several minutes.",
            _dep.Name);

        lock (_outputLock)
        {
            _output.Clear();
            _pendingOutputLine = null;
        }

        try
        {
            int exitCode = await RunInstallProcessAsync();
            bool installed = await IsDependencyInstalledAsync();

            Logger.Info(
                $"Installation of dependency {_dep.Name} exited with code {exitCode}; "
                + $"post-install detection returned {installed}");

            EndInstallAttempt();

            if (installed)
                ShowSuccess();
            else if (_canceled)
                ShowFailure(
                    CoreTools.Translate("Installation was canceled while in progress."));
            else
                ShowFailure(FailureSummary(exitCode));
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not install dependency {_dep.Name}");
            Logger.Error(ex);
            EndInstallAttempt();
            ShowFailure(CoreTools.Translate("An error occurred:") + " " + ex.Message);
        }
    }

    private async Task<int> RunInstallProcessAsync()
    {
        var (fileName, arguments) = await Task.Run(_dep.GetInstallCommand);
        Logger.Info($"Installing dependency {_dep.Name} via: {fileName} {arguments}");

        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        p.OutputDataReceived += (_, args) => OnOutputLine(args.Data);
        p.ErrorDataReceived += (_, args) => OnOutputLine(args.Data);

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.StandardInput.Close();
        _installProcess = p;
        if (_canceled)
            KillInstallProcess();

        try
        {
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        finally
        {
            _installProcess = null;
        }
    }

    private void CancelInstall()
    {
        _canceled = true;
        SkipButton.IsEnabled = false;
        Logger.Warn($"Canceling the installation of dependency {_dep.Name}");
        KillInstallProcess();
    }

    private void KillInstallProcess()
    {
        if (_installProcess is not { } process)
            return;

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not cancel the installation of dependency {_dep.Name}");
            Logger.Error(ex);
        }
    }

    private void OnOutputLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        string trimmed = line.Trim();
        bool scheduleFlush;
        lock (_outputLock)
        {
            _output.Add(trimmed);
            if (_output.Count > MaxRetainedOutputLines)
                _output.RemoveRange(0, _output.Count - MaxRetainedOutputLines);

            scheduleFlush = _pendingOutputLine is null;
            _pendingOutputLine = trimmed;
        }

        Logger.Info($"[{_dep.Name}] {trimmed}");
        if (scheduleFlush)
            Dispatcher.UIThread.Post(FlushPendingOutputLine, DispatcherPriority.Background);
    }

    private void FlushPendingOutputLine()
    {
        string? line;
        lock (_outputLock)
        {
            line = _pendingOutputLine;
            _pendingOutputLine = null;
        }

        if (line is null || !_installRunning)
            return;

        OutputBorder.IsVisible = true;
        OutputBlock.Text = line;
    }

    private async Task<bool> IsDependencyInstalledAsync()
    {
        try
        {
            return await _dep.IsInstalled();
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not verify the installation of dependency {_dep.Name}");
            Logger.Error(ex);
            return false;
        }
    }

    private string FailureSummary(int exitCode) => exitCode is 0
        ? CoreTools.Translate(
            "The installer finished, but {0} could not be found on your system.", _dep.Name)
        : CoreTools.Translate(
            "{0} could not be installed. The installer exited with code {1}.",
            _dep.Name,
            exitCode.ToString());

    private void EndInstallAttempt()
    {
        _installRunning = false;
        _blockClose = false;
        ProgressBar.IsIndeterminate = false;
        ProgressBar.IsVisible = false;
        InstallButton.IsEnabled = true;
        SkipButton.IsEnabled = true;
        DontShowCheck.IsEnabled = true;
    }

    private void ShowSuccess()
    {
        _hasInstalled = true;
        OutputBorder.IsVisible = false;

        if (_current < _total)
        {
            InfoBlock.Text =
                CoreTools.Translate("{0} has been installed successfully.", _dep.Name)
                + " "
                + CoreTools.Translate("Please click on \"Continue\" to continue", _dep.Name);
            SetButtonLabel(InstallButton, CoreTools.Translate("Continue"));
            SkipButton.IsVisible = false;
        }
        else
        {
            InfoBlock.Text = CoreTools.Translate(
                "{0} has been installed successfully. It is recommended to restart UniGetUI to finish the installation",
                _dep.Name);
            SetButtonLabel(InstallButton, CoreTools.Translate("Restart UniGetUI"));
            SetButtonLabel(SkipButton, CoreTools.Translate("Restart later"));
        }
    }

    private void ShowFailure(string summary)
    {
        string[] tail;
        lock (_outputLock)
            tail = _output.TakeLast(ShownOutputLines).ToArray();

        InfoBlock.Text = summary
            + " "
            + CoreTools.Translate("You can try again, or install it manually with the command below.");

        if (tail.Length > 0)
        {
            OutputBorder.IsVisible = true;
            OutputBlock.Text = string.Join('\n', tail);
        }
        else
        {
            OutputBorder.IsVisible = false;
        }

        SetButtonLabel(InstallButton, CoreTools.Translate("Retry"));
        SetButtonLabel(SkipButton, CoreTools.Translate("Not right now"));
        SkipButton.IsVisible = true;
    }
}
