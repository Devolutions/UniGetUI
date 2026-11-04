using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public enum ManagerStatusSeverity { Info, Success, Warning, Error }

public partial class PackageManagerViewModel : ViewModelBase
{
    public readonly IPackageManager Manager;

    // ── Events ────────────────────────────────────────────────────────────────
    public event EventHandler? RestartRequired;
    public event EventHandler? NavigateToAdministratorRequested;

    // ── Section headings ──────────────────────────────────────────────────────
    public string PageTitle => CoreTools.Translate("{0} settings", Manager.DisplayName);
    public string StatusSectionTitle => CoreTools.Translate("{0} status", Manager.DisplayName);
    public string InstallSectionTitle => CoreTools.Translate("Default installation options for {0} packages", Manager.DisplayName);
    public string SettingsSectionTitle => CoreTools.Translate("{0} settings", Manager.DisplayName);

    // ── Status bar ────────────────────────────────────────────────────────────
    [ObservableProperty] private ManagerStatusSeverity _severity = ManagerStatusSeverity.Info;
    [ObservableProperty] private string _statusTitle = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _statusMessageVisible;
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private bool _versionTextVisible;
    [ObservableProperty] private bool _showVersionVisible;
    public string ShowVersionLabel { get; } = CoreTools.Translate("Expand version");
    [ObservableProperty] private bool _setupGuideVisible;
    [ObservableProperty] private bool _installCommandVisible;
    public string SetupGuideLabel => CoreTools.Translate("How to set up {0}", Manager.DisplayName);
    public string InstallCommandLabel { get; } = CoreTools.Translate("Install it with this command:");
    public string OpenInTerminalLabel { get; } = CoreTools.Translate("Open in a terminal");
    [ObservableProperty] private string? _installCommand;
    public IReadOnlyList<string> CandidateExecutables { get; private set; } = [];
    private bool _executableFoundWhileDisabled;

    private const string DocsBaseUrl = "https://github.com/Devolutions/UniGetUI/blob/main/docs/";

    // ── Path label ────────────────────────────────────────────────────────────
    [ObservableProperty] private string _pathLabelText = "";

    // ── Loading state ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isLoading;

    // ── Vcpkg root ────────────────────────────────────────────────────────────
    [ObservableProperty] private string _vcpkgRootPath = "%VCPKG_ROOT%";
    [ObservableProperty] private bool _isCustomVcpkgRootSet;

    public PackageManagerViewModel(IPackageManager manager)
    {
        Manager = manager;
        ApplyProbe(ProbeExecutables());
        RefreshState();
        IsCustomVcpkgRootSet = CoreSettings.Get(CoreSettings.K.CustomVcpkgRoot);
        VcpkgRootPath = IsCustomVcpkgRootSet
            ? CoreSettings.GetValue(CoreSettings.K.CustomVcpkgRoot)
            : "%VCPKG_ROOT%";
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    [RelayCommand]
    public async Task ReloadManager()
    {
        IsLoading = true;
        RefreshState();
        string previousExecutable = GetEffectiveExecutable();
        ApplyProbe(await Task.Run(() =>
        {
            Manager.Initialize();
            return ProbeExecutables();
        }));
        IsLoading = false;
        RefreshState(showVersion: true);
        var mainWindow = MainWindow.Instance?.DataContext as MainWindowViewModel;
        if (GetEffectiveExecutable() != previousExecutable)
            mainWindow?.ReloadPagesAfterManagerExecutableChanged();
        else
            mainWindow?.ReloadPagesWithoutReadyManager();
    }

    private string GetEffectiveExecutable() =>
        Manager.IsReady() ? $"{Manager.Status.ExecutablePath} {Manager.Status.ExecutableCallArgs}" : "";

    [RelayCommand]
    public void ShowVersion() => RefreshState(showVersion: true);

    [RelayCommand]
    private void OpenSetupGuide() => CoreTools.Launch(GetSetupGuideUrl(Manager.Properties));

    [RelayCommand]
    private Task OpenInstallCommandInTerminal() =>
        InstallCommand is null ? Task.CompletedTask : ManualInstallHelper.LaunchManualAsync(InstallCommand);

    private (IReadOnlyList<string> Candidates, bool FoundWhileDisabled, string? Command) ProbeExecutables() =>
        (Manager.FindCandidateExecutableFiles(),
         !Manager.IsEnabled() && Manager.HasExecutable(),
         RunnableCommand(Manager.Properties.InstallCommands.ForCurrentPlatform(), program => CoreTools.Which(program).Item1));

    private void ApplyProbe((IReadOnlyList<string> Candidates, bool FoundWhileDisabled, string? Command) probe)
    {
        CandidateExecutables = probe.Candidates;
        _executableFoundWhileDisabled = probe.FoundWhileDisabled;
        InstallCommand = probe.Command;
    }

    internal static string? RunnableCommand(string? command, Func<string, bool> programExists)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        string program = command.Split(' ', 2)[0];
        return programExists(program) ? command : null;
    }

    internal static string GetSetupGuideUrl(ManagerProperties properties) =>
        DocsBaseUrl + (properties.SetupGuide.Length > 0 ? properties.SetupGuide : "PACKAGE_MANAGERS.md");

    public void OnExecutableSelected(string path)
    {
        CoreSettings.SetDictionaryItem(CoreSettings.K.ManagerPaths, Manager.Name, path);
        _ = ReloadManagerCommand.ExecuteAsync(null);
    }

    // ── State computation ─────────────────────────────────────────────────────
    public void RefreshState(bool showVersion = false)
    {
        PathLabelText = string.IsNullOrEmpty(Manager.Status.ExecutablePath)
            ? CoreTools.Translate("The executable file for {0} was not found", Manager.DisplayName)
            : Manager.Status.ExecutablePath + " " + Manager.Status.ExecutableCallArgs.Trim();

        VersionText = "";
        VersionTextVisible = false;
        ShowVersionVisible = false;

        if (IsLoading)
        {
            Severity = ManagerStatusSeverity.Info;
            StatusTitle = CoreTools.Translate("Please wait...");
            StatusMessage = "";
        }
        else if (!Manager.IsEnabled())
        {
            Severity = ManagerStatusSeverity.Warning;
            StatusTitle = CoreTools.Translate("{pm} is disabled").Replace("{pm}", Manager.DisplayName);
            StatusMessage = CoreTools.Translate("Enable it to install packages from {pm}.").Replace("{pm}", Manager.DisplayName);
        }
        else if (Manager.IsReady())
        {
            Severity = ManagerStatusSeverity.Success;
            StatusTitle = CoreTools.Translate("{pm} is enabled and ready to go").Replace("{pm}", Manager.DisplayName);
            if (Manager.Status.Version.Contains('\n'))
            {
                StatusMessage = "";
                if (showVersion)
                {
                    VersionText = Manager.Status.Version;
                    VersionTextVisible = true;
                }
                else
                {
                    ShowVersionVisible = true;
                }
            }
            else
            {
                StatusMessage = CoreTools.Translate("{pm} version:").Replace("{pm}", Manager.DisplayName)
                                + " " + Manager.Status.Version;
            }
        }
        else if (Manager.Status.Found)
        {
            Severity = ManagerStatusSeverity.Error;
            StatusTitle = CoreTools.Translate("{pm} could not be loaded").Replace("{pm}", Manager.DisplayName);
            StatusMessage = CoreTools.Translate("{pm} was found on your system, but it could not be started. Check the UniGetUI log for more details.").Replace("{pm}", Manager.DisplayName);
        }
        else
        {
            Severity = ManagerStatusSeverity.Error;
            StatusTitle = CoreTools.Translate("{pm} was not found!").Replace("{pm}", Manager.DisplayName);
            StatusMessage = CoreTools.Translate("You may need to install {pm} in order to use it with UniGetUI.").Replace("{pm}", Manager.DisplayName);
        }

        StatusMessageVisible = !string.IsNullOrEmpty(StatusMessage);
        (SetupGuideVisible, InstallCommandVisible) = GetSetupHelpVisibility(
            IsLoading, Manager.IsReady(), Manager.IsEnabled(), Manager.Status.Found,
            _executableFoundWhileDisabled, InstallCommand is not null);
    }

    internal static (bool Guide, bool InstallCommand) GetSetupHelpVisibility(
        bool loading, bool ready, bool enabled, bool statusFound, bool foundWhileDisabled, bool hasInstallCommand)
    {
        bool guide = !loading && !ready;
        bool executableFound = enabled ? statusFound : foundWhileDisabled;
        return (guide, guide && hasInstallCommand && !executableFound);
    }

    // ── Scoop commands ────────────────────────────────────────────────────────
    [RelayCommand]
    private void ScoopInstall()
    {
        _ = CoreTools.LaunchBatchFile(
            Path.Join(CoreData.UniGetUIExecutableDirectory, "Assets", "Utilities", "install_scoop.cmd"),
            CoreTools.Translate("Scoop Installer - UniGetUI"));
        RestartRequired?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ScoopUninstall()
    {
        _ = CoreTools.LaunchBatchFile(
            Path.Join(CoreData.UniGetUIExecutableDirectory, "Assets", "Utilities", "uninstall_scoop.cmd"),
            CoreTools.Translate("Scoop Uninstaller - UniGetUI"));
        RestartRequired?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private static void ScoopCleanup()
    {
        _ = CoreTools.LaunchBatchFile(
            Path.Join(CoreData.UniGetUIExecutableDirectory, "Assets", "Utilities", "scoop_cleanup.cmd"),
            CoreTools.Translate("Clearing Scoop cache - UniGetUI"),
            RunAsAdmin: true);
    }

    // ── Vcpkg root commands ───────────────────────────────────────────────────
    [RelayCommand]
    private void ResetVcpkgRoot()
    {
        CoreSettings.Set(CoreSettings.K.CustomVcpkgRoot, false);
        VcpkgRootPath = "%VCPKG_ROOT%";
        IsCustomVcpkgRootSet = false;
    }

    [RelayCommand]
    private static void OpenVcpkgRoot()
    {
        string directory = CoreSettings.GetValue(CoreSettings.K.CustomVcpkgRoot);
        if (!string.IsNullOrEmpty(directory))
            CoreTools.Launch(directory);
    }

    [RelayCommand]
    private async Task PickVcpkgRoot(Visual? visual)
    {
        if (visual is null || TopLevel.GetTopLevel(visual) is not { } topLevel) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { AllowMultiple = false });
        if (folders is not [{ } folder]) return;
        var path = folder.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        CoreSettings.SetValue(CoreSettings.K.CustomVcpkgRoot, path);
        VcpkgRootPath = path;
        IsCustomVcpkgRootSet = true;
        _ = ReloadManagerCommand.ExecuteAsync(null);
    }

    // ── Navigation commands ───────────────────────────────────────────────────
    [RelayCommand]
    private void NavigateToAdministrator()
    {
        NavigateToAdministratorRequested?.Invoke(this, EventArgs.Empty);
    }
}
