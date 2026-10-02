using System.Text;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageOperations;
using UniGetUI.Tui.FakeData;
using UniGetUI.Tui.Views.Dialogs;

namespace UniGetUI.Tui.Infrastructure;

internal enum ScoopMaintenanceTask
{
    Install,
    Uninstall,
    Cleanup,
}

/// <summary>
/// The Managers page's Scoop maintenance actions. The desktop app runs <c>Assets\Utilities\*.cmd</c> scripts in
/// a console window and relaunches itself afterwards; the TUI asks for confirmation in its own dialogs, runs the
/// same commands as a tracked operation (live output on the Operations page) and re-detects Scoop in place, so it
/// ships none of those scripts.
/// </summary>
internal static class TuiScoopMaintenance
{
    public static async Task RunAsync(ScoopMaintenanceTask task)
    {
        bool confirmed = task switch
        {
            ScoopMaintenanceTask.Install => await TuiPrompts.ConfirmAsync(CoreTools.Translate("Install Scoop"),
                CoreTools.Translate("This will install Scoop and its dependencies (git and scoop-search). It also sets the PowerShell execution policy to RemoteSigned for your user, which Scoop requires.")
                + "\n\n" + CoreTools.Translate("Do you want to continue?"),
                CoreTools.Translate("Install"), CoreTools.Translate("Cancel")),
            ScoopMaintenanceTask.Uninstall =>
                await TuiPrompts.ConfirmAsync(CoreTools.Translate("Uninstall Scoop"),
                    CoreTools.Translate("Removing Scoop implies removing all Scoop installed packages, buckets and preferences, and might also erase valuable user data related to the affected packages."),
                    CoreTools.Translate("Continue"), CoreTools.Translate("Cancel"))
                && await TuiPrompts.ConfirmAsync(CoreTools.Translate("Uninstall Scoop"),
                    CoreTools.Translate("ALL YOUR SCOOP PACKAGES WILL BE PERMANENTLY DELETED."),
                    CoreTools.Translate("Uninstall Scoop"), CoreTools.Translate("Cancel")),
            _ => await TuiPrompts.ConfirmAsync(CoreTools.Translate("Run cleanup and clear cache"),
                CoreTools.Translate("This will clean the Scoop cache and remove older versions of Scoop apps."),
                CoreTools.Translate("Continue"), CoreTools.Translate("Cancel")),
        };
        if (!confirmed) return;

        var operation = new ScoopMaintenanceOperation(task);
        if (task is not ScoopMaintenanceTask.Cleanup)
            operation.OperationSucceeded += (_, _) => _ = Task.Run(RedetectScoop);
        TuiOperationRegistry.Start(operation);
        TuiShell.Navigate(TuiPageIds.Operations);
    }

    /// <summary>After Scoop is installed or removed, find it again and reload the package lists.</summary>
    private static void RedetectScoop()
    {
        try
        {
            TuiEngine.FindManager("Scoop")?.Initialize();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }

        _ = InstalledPackagesLoader.Instance.ReloadPackages();
        _ = UpgradablePackagesLoader.Instance.ReloadPackages();
        TuiShell.InvalidatePages(TuiPageIds.Managers);
    }
}

/// <summary>A Scoop maintenance command run as a regular operation, so its output streams into the Operations page.</summary>
internal sealed class ScoopMaintenanceOperation : AbstractProcessOperation
{
    // The desktop app's install_scoop.cmd + install_scoop.ps1. Set-ExecutionPolicy gets -Force: stdin is not
    // interactive here, so it must not wait for a confirmation.
    private const string InstallScript = """
        Set-ExecutionPolicy RemoteSigned -Scope CurrentUser -Force -ErrorAction Continue
        If (Get-Command scoop -ErrorAction SilentlyContinue) {
            Write-Output "Scoop is already installed."
            exit 1
        }
        Write-Output "Installing scoop..."
        iex "& {$(irm get.scoop.sh)} -RunAsAdmin"
        $Env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
        If (-Not (Get-Command git -ErrorAction SilentlyContinue)) {
            Write-Output "Installing git..."
            scoop install git
        }
        scoop install scoop-search
        Write-Output "Done!"
        """;

    // The desktop app's uninstall_scoop.cmd.
    private const string UninstallScript = """
        Write-Output "Uninstalling scoop..."
        scoop uninstall -p scoop
        if ($?) { exit 0 } else { exit 1 }
        """;

    // The desktop app's scoop_cleanup.cmd, which reports success whatever the individual commands return.
    private const string CleanupScript = """
        Write-Output "Cleaning Scoop cache..."
        scoop cleanup --all
        scoop cleanup --all --global
        scoop cache rm --all
        scoop cache rm --all --global
        Write-Output "Done!"
        exit 0
        """;

    private readonly ScoopMaintenanceTask _task;

    public ScoopMaintenanceOperation(ScoopMaintenanceTask task)
        : base(queue_enabled: true)
    {
        _task = task;
        string title = task switch
        {
            ScoopMaintenanceTask.Install => CoreTools.Translate("Install Scoop"),
            ScoopMaintenanceTask.Uninstall => CoreTools.Translate("Uninstall Scoop"),
            _ => CoreTools.Translate("Run cleanup and clear cache"),
        };
        Metadata.Title = title;
        Metadata.OperationInformation = "Scoop maintenance: " + task;
        Metadata.Status = title + "…";
        Metadata.SuccessTitle = title;
        Metadata.SuccessMessage = CoreTools.Translate("The operation completed successfully");
        Metadata.FailureTitle = title;
        Metadata.FailureMessage = CoreTools.Translate("The operation failed. See its output for details.");
    }

    public override Task<Uri> GetOperationIcon() => Task.FromResult(new Uri("ms-appx:///Assets/Images/scoop.png"));

    protected override void ApplyRetryAction(string retryMode)
    {
        // A plain retry runs the same commands again.
    }

    protected override void PrepareProcessStartInfo()
    {
        if (FakeDataEnvironment.Current is { } env)
        {
            // Fake data: the sandboxed fake package manager only prints what it would do.
            process.StartInfo.FileName = env.FakeCliPath;
            SetArgumentVector([.. env.FakeCliPrefixArgs, FakePackageManagerProcess.PmFlag, "--state", env.StatePath, "Scoop", "scoop-maintenance",
                "--task", _task.ToString().ToLowerInvariant()]);
            return;
        }

        string script = _task switch
        {
            ScoopMaintenanceTask.Install => InstallScript,
            ScoopMaintenanceTask.Uninstall => UninstallScript,
            _ => CleanupScript,
        };
        // -EncodedCommand: the script is fixed text with no parameters, handed to PowerShell without a script file.
        process.StartInfo.FileName = CoreData.PowerShell5;
        SetArgumentVector(["-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]);
    }

    protected override Task<OperationVeredict> GetProcessVeredict(int ReturnCode, List<string> Output)
        => Task.FromResult(ReturnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure);
}
