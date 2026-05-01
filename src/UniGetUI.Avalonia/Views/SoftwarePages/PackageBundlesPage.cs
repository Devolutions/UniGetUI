using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.Interface.Telemetry;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Classes.Serializable;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.Views.Pages;

public class PackageBundlesPage : AbstractPackagesPage
{
    // Context-menu items whose enabled state depends on the focused package
    private MenuItem? _menuInstall;
    private MenuItem? _menuInstallOptions;
    private MenuItem? _menuAsAdmin;
    private MenuItem? _menuInteractive;
    private MenuItem? _menuSkipHash;
    private MenuItem? _menuDownloadInstaller;
    private MenuItem? _menuDetails;

    private readonly PackageBundlesLoader _loader;

    private bool _hasUnsavedChanges;
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set
        {
            _hasUnsavedChanges = value;
            UnsavedChangesStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public event EventHandler? UnsavedChangesStateChanged;

    public PackageBundlesPage() : base(new PackagesPageData
    {
        PageName = "SoftwarePages.PackageBundlesPage",
        PageTitle = CoreTools.Translate("Package Bundles"),
        IconName = "PackagesBundle",
        PageRole = OperationType.Install,
        Loader = PackageBundlesLoader.Instance!,
        MegaQueryBlockEnabled = false,
        DisableSuggestedResultsRadio = true,
        PackagesAreCheckedByDefault = false,
        ShowLastLoadTime = false,
        DisableAutomaticPackageLoadOnStart = true,
        DisableFilterOnQueryChange = false,
        DisableReload = true,
        NoPackages_BackgroundText = CoreTools.Translate("Add packages or open an existing package bundle"),
        NoPackages_SourcesText = CoreTools.Translate("Add packages to start"),
        NoPackages_SubtitleText_Base = CoreTools.Translate("The current bundle has no packages. Add some packages to get started"),
        MainSubtitle_StillLoading = CoreTools.Translate("Loading packages"),
        NoMatches_BackgroundText = CoreTools.Translate("No results were found matching the input criteria"),
    })
    {
        _loader = PackageBundlesLoader.Instance!;
        _loader.PackagesChanged += (_, _) => HasUnsavedChanges = true;
    }

    // ─── Toolbar ──────────────────────────────────────────────────────────────
    protected override void GenerateToolBar(PackagesPageViewModel vm)
    {
        var installAsAdmin = new MenuItem { Header = CoreTools.Translate("Install as administrator"), IsVisible = OperatingSystem.IsWindows() };
        var installInteractive = new MenuItem { Header = CoreTools.Translate("Interactive installation") };
        var installSkipHash = new MenuItem { Header = CoreTools.Translate("Skip integrity checks") };
        var downloadInstallers = new MenuItem { Header = CoreTools.Translate("Download selected installers") };

        SetMainButton("download", CoreTools.Translate("Install selection"), () =>
            _ = ImportAndInstallPackage(GetCheckedNonInstalledPackages(vm)));

        SetMainButtonDropdown(new MenuFlyout
        {
            Items = { installAsAdmin, installInteractive, installSkipHash, new Separator(), downloadInstallers },
        });

        installAsAdmin.Click += (_, _) => _ = ImportAndInstallPackage(GetCheckedNonInstalledPackages(vm), elevated: true);
        installInteractive.Click += (_, _) => _ = ImportAndInstallPackage(GetCheckedNonInstalledPackages(vm), interactive: true);
        installSkipHash.Click += (_, _) => _ = ImportAndInstallPackage(GetCheckedNonInstalledPackages(vm), skiphash: true);
        downloadInstallers.Click += (_, _) => _ = AvaloniaPackageOperationHelper.DownloadSelectedAsync(
            GetCheckedNonInstalledPackages(vm), TEL_InstallReferral.FROM_BUNDLE);

        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("add_to", CoreTools.Translate("New"),
            () => _ = AskForNewBundle());
        ViewModel.AddToolbarButton("open_folder", CoreTools.Translate("Open"),
            () => _ = AskOpenFromFile());
        ViewModel.AddToolbarButton("save_as", CoreTools.Translate("Save as"),
            () => _ = SaveFile());
        ViewModel.AddToolbarButton("console", CoreTools.Translate("Create .ps1 script"),
            () => _ = CreateBatchScriptAsync());
        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("delete", CoreTools.Translate("Remove selection from bundle"), () =>
        {
            HasUnsavedChanges = true;
            _loader.RemoveRange(vm.FilteredPackages.GetCheckedPackages());
        });
        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("info_round", CoreTools.Translate("Package details"),
            () => _ = ShowDetailsForPackage(SelectedItem), showLabel: false);
    }

    private static IReadOnlyList<IPackage> GetCheckedNonInstalledPackages(PackagesPageViewModel vm)
    {
        if (Settings.Get(Settings.K.InstallInstalledPackagesBundlesPage))
            return vm.FilteredPackages.GetCheckedPackages();

        return vm.FilteredPackages.GetCheckedPackages()
            .Where(p => p.Tag is not PackageTag.AlreadyInstalled)
            .ToList();
    }

    // ─── Context menu ─────────────────────────────────────────────────────────
    protected override ContextMenu? GenerateContextMenu()
    {
        _menuInstall = new MenuItem { Header = CoreTools.AutoTranslated("Install"), Icon = LoadMenuIcon("download") };
        _menuInstall.Click += (_, _) => _ = ImportAndInstallPackage(SelectedItem is { } p ? [p] : []);

        _menuInstallOptions = new MenuItem { Header = CoreTools.AutoTranslated("Install options"), Icon = LoadMenuIcon("options") };
        _menuInstallOptions.Click += (_, _) =>
        {
            if (SelectedItem is ImportedPackage imported)
            {
                HasUnsavedChanges = true;
                _ = ShowInstallationOptionsForPackage(imported);
            }
        };

        _menuAsAdmin = new MenuItem { Header = CoreTools.AutoTranslated("Install as administrator"), Icon = LoadMenuIcon("uac"), IsVisible = OperatingSystem.IsWindows() };
        _menuAsAdmin.Click += (_, _) => _ = ImportAndInstallPackage(SelectedItem is { } p ? [p] : [], elevated: true);

        _menuInteractive = new MenuItem { Header = CoreTools.AutoTranslated("Interactive installation"), Icon = LoadMenuIcon("interactive") };
        _menuInteractive.Click += (_, _) => _ = ImportAndInstallPackage(SelectedItem is { } p ? [p] : [], interactive: true);

        _menuSkipHash = new MenuItem { Header = CoreTools.AutoTranslated("Skip hash checks"), Icon = LoadMenuIcon("checksum") };
        _menuSkipHash.Click += (_, _) => _ = ImportAndInstallPackage(SelectedItem is { } p ? [p] : [], skiphash: true);

        _menuDownloadInstaller = new MenuItem { Header = CoreTools.AutoTranslated("Download installer"), Icon = LoadMenuIcon("download") };
        _menuDownloadInstaller.Click += (_, _) => _ = AvaloniaPackageOperationHelper.AskLocationAndDownloadAsync(
            SelectedItem, TEL_InstallReferral.FROM_BUNDLE);

        var menuRemoveFromList = new MenuItem { Header = CoreTools.AutoTranslated("Remove from list"), Icon = LoadMenuIcon("delete") };
        menuRemoveFromList.Click += (_, _) =>
        {
            if (SelectedItem is { } pkg)
            {
                HasUnsavedChanges = true;
                _loader.Remove(pkg);
            }
        };

        _menuDetails = new MenuItem { Header = CoreTools.AutoTranslated("Package details"), Icon = LoadMenuIcon("info_round") };
        _menuDetails.Click += (_, _) => _ = ShowDetailsForPackage(SelectedItem);

        var menu = new ContextMenu();
        menu.Items.Add(_menuInstall);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuInstallOptions);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuAsAdmin);
        menu.Items.Add(_menuInteractive);
        menu.Items.Add(_menuSkipHash);
        menu.Items.Add(_menuDownloadInstaller);
        menu.Items.Add(new Separator());
        menu.Items.Add(menuRemoveFromList);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuDetails);
        return menu;
    }

    protected override void WhenShowingContextMenu(IPackage package)
    {
        if (_menuInstall is null || _menuInstallOptions is null || _menuAsAdmin is null
            || _menuInteractive is null || _menuSkipHash is null || _menuDownloadInstaller is null
            || _menuDetails is null)
        {
            Logger.Warn("Context menu items are null on PackageBundlesPage");
            return;
        }

        bool isValid = package is not InvalidImportedPackage;
        var caps = package.Manager.Capabilities;

        _menuInstall.IsEnabled = isValid;
        _menuInstallOptions.IsEnabled = isValid;
        _menuAsAdmin.IsEnabled = isValid && caps.CanRunAsAdmin;
        _menuInteractive.IsEnabled = isValid && caps.CanRunInteractively;
        _menuSkipHash.IsEnabled = isValid && caps.CanSkipIntegrityChecks;
        _menuDownloadInstaller.IsEnabled = isValid && caps.CanDownloadInstaller;
        _menuDetails.IsEnabled = isValid;
    }

    // ─── Abstract action overrides ────────────────────────────────────────────
    protected override void PerformMainPackageAction(IPackage? package)
    {
        if (package is null) return;
        _ = ImportAndInstallPackage([package]);
    }

    protected override async Task ShowDetailsForPackage(IPackage? package)
    {
        if (package is null || package is InvalidImportedPackage) return;
        if (GetMainWindow() is not { } win) return;

        var dialog = new PackageDetailsWindow(package, OperationType.None);
        await dialog.ShowDialog(win);

        if (dialog.ShouldProceedWithOperation)
            _ = ImportAndInstallPackage([package]);
    }

    protected override async Task ShowInstallationOptionsForPackage(IPackage? package)
    {
        if (package is not ImportedPackage imported) return;
        if (GetMainWindow() is not { } win) return;

        var opts = imported.installation_options;
        var dialog = new InstallOptionsWindow(imported, OperationType.Install, opts);
        await dialog.ShowDialog(win);

        if (dialog.ShouldProceedWithOperation)
            _ = ImportAndInstallPackage([imported]);
    }

    // ─── Bundle operations ────────────────────────────────────────────────────
    public async Task<bool> AskForNewBundle()
    {
        if (_loader.Any() && HasUnsavedChanges && !await AskLoseChanges())
            return false;

        _loader.ClearPackages();
        HasUnsavedChanges = false;
        return true;
    }

    public async Task ImportAndInstallPackage(
        IReadOnlyList<IPackage> packages,
        bool? elevated = null,
        bool? interactive = null,
        bool? skiphash = null)
    {
        var toInstall = new List<Package>();
        foreach (var package in packages)
        {
            if (package is ImportedPackage imported)
            {
                Logger.ImportantInfo($"Registering package {imported.Id} from manager {imported.Source.AsString}");
                toInstall.Add(await imported.RegisterAndGetPackageAsync());
            }
            else
            {
                Logger.Warn($"Attempted to install an invalid/incompatible package with Id={package.Id}");
            }
        }

        foreach (var pkg in toInstall)
        {
            var opts = await InstallOptionsFactory.LoadApplicableAsync(
                pkg, elevated: elevated, interactive: interactive, no_integrity: skiphash);
            var op = new InstallPackageOperation(pkg, opts);
            op.OperationSucceeded += (_, _) => TelemetryHandler.InstallPackage(pkg, TEL_OP_RESULT.SUCCESS, TEL_InstallReferral.FROM_BUNDLE);
            op.OperationFailed += (_, _) => TelemetryHandler.InstallPackage(pkg, TEL_OP_RESULT.FAILED, TEL_InstallReferral.FROM_BUNDLE);
            AvaloniaOperationRegistry.Add(op);
            _ = op.MainThread();
        }
    }

    public async Task OpenFromFile(string file)
    {
        try
        {
            var formatType = file.Split('.')[^1].ToLower() switch
            {
                "yaml" => BundleFormatType.YAML,
                "xml" => BundleFormatType.XML,
                "json" => BundleFormatType.JSON,
                _ => BundleFormatType.UBUNDLE,
            };

            string fileContent = await File.ReadAllTextAsync(file);
            await OpenFromString(fileContent, formatType, file, null);
        }
        catch (Exception ex)
        {
            Logger.Error("An error occurred while attempting to open a bundle");
            Logger.Error(ex);
            if (GetMainWindow() is { } win)
                await ShowErrorDialog(win,
                    CoreTools.Translate("The package bundle is not valid"),
                    CoreTools.Translate("The bundle you are trying to load appears to be invalid. Please check the file and try again.")
                    + "\n\n" + ex.Message);
        }
    }

    public async Task OpenFromString(string payload, BundleFormatType format, string source, int? _loadingId = null)
    {
        if (!await AskForNewBundle()) return;

        var (openVersion, report) = await AddFromBundle(payload, format);
        TelemetryHandler.ImportBundle(format);
        HasUnsavedChanges = false;

        if ((int)(openVersion * 10) != (int)(SerializableBundle.ExpectedVersion * 10))
            Logger.Warn($"Bundle \"{source}\" uses schema version {openVersion}, expected {SerializableBundle.ExpectedVersion}.");

        if (!report.IsEmpty && GetMainWindow() is { } win)
            await ShowBundleSecurityReport(win, report);
    }

    /// <summary>Compatibility overload matching the legacy stub signature.</summary>
    public Task OpenFromString(string payload, object format, string source, int loadingId)
        => OpenFromString(payload,
            format is BundleFormatType f ? f : BundleFormatType.UBUNDLE,
            source, (int?)loadingId);

    public async Task AskOpenFromFile()
    {
        if (!await AskForNewBundle()) return;
        if (GetMainWindow() is not { } win) return;

        var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Package bundles") { Patterns = ["*.ubundle", "*.json", "*.yaml", "*.xml"] },
                new FilePickerFileType("All files")       { Patterns = ["*"] },
            ],
        });

        if (files is not [{ } file]) return;
        var path = file.TryGetLocalPath();
        if (path is null) return;

        await OpenFromFile(path);
    }

    public async Task SaveFile()
    {
        if (GetMainWindow() is not { } win) return;

        var file = await win.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = CoreTools.Translate("Package bundle") + ".ubundle",
            FileTypeChoices =
            [
                new FilePickerFileType("UniGetUI Bundle") { Patterns = ["*.ubundle"] },
                new FilePickerFileType("JSON")            { Patterns = ["*.json"] },
            ],
        });

        if (file is null) return;
        var path = file.TryGetLocalPath();
        if (path is null) return;

        try
        {
            var content = await CreateBundle(_loader.Packages);
            await File.WriteAllTextAsync(path, content);

            var formatType = path.Split('.')[^1].ToLower() == "json"
                ? BundleFormatType.JSON : BundleFormatType.UBUNDLE;
            TelemetryHandler.ExportBundle(formatType);

            HasUnsavedChanges = false;
            await CoreTools.ShowFileOnExplorer(path);
        }
        catch (Exception ex)
        {
            Logger.Error("An error occurred when saving packages to a file");
            Logger.Error(ex);
            await ShowErrorDialog(win,
                CoreTools.Translate("Could not create bundle"),
                CoreTools.Translate("The package bundle could not be created due to an error.")
                + "\n\n" + ex.Message);
        }
    }

    public static async Task<string> CreateBundle(IReadOnlyList<IPackage> unsortedPackages)
    {
        var exportableData = new SerializableBundle();
        var packages = unsortedPackages.ToList();
        packages.Sort((x, y) =>
        {
            if (x.Id != y.Id) return string.Compare(x.Id, y.Id, StringComparison.Ordinal);
            if (x.Name != y.Name) return string.Compare(x.Name, y.Name, StringComparison.Ordinal);
            return x.NormalizedVersion > y.NormalizedVersion ? -1 : 1;
        });

        foreach (var package in packages)
        {
            if (package is Package && !package.Source.IsVirtualManager)
                exportableData.packages.Add(await package.AsSerializableAsync());
            else
                exportableData.incompatible_packages.Add(package.AsSerializable_Incompatible());
        }

        return exportableData.AsJsonString();
    }

    public async Task<(double, BundleReport)> AddFromBundle(string content, BundleFormatType format)
    {
        if (format is BundleFormatType.YAML)
        {
            content = await SerializationHelpers.YAML_to_JSON(content);
            Logger.ImportantInfo("YAML bundle was converted to JSON before deserialization");
        }
        if (format is BundleFormatType.XML)
        {
            content = await SerializationHelpers.XML_to_JSON(content);
            Logger.ImportantInfo("XML bundle was converted to JSON before deserialization");
        }

        var deserializedData = await Task.Run(() =>
            new SerializableBundle(JsonNode.Parse(content)
                ?? throw new JsonException("Could not parse JSON object")));

        var report = new BundleReport { IsEmpty = true };
        bool allowCLI = SecureSettings.Get(SecureSettings.K.AllowCLIArguments)
                            && SecureSettings.Get(SecureSettings.K.AllowImportingCLIArguments);
        bool allowPrePost = SecureSettings.Get(SecureSettings.K.AllowPrePostOpCommand)
                            && SecureSettings.Get(SecureSettings.K.AllowImportPrePostOpCommands);

        var packages = new List<IPackage>();
        foreach (var pkg in deserializedData.packages)
        {
            var opts = pkg.InstallationOptions;
            ReportList(ref report, pkg.Id, opts.CustomParameters_Install, "Custom install arguments", allowCLI);
            ReportList(ref report, pkg.Id, opts.CustomParameters_Update, "Custom update arguments", allowCLI);
            ReportList(ref report, pkg.Id, opts.CustomParameters_Uninstall, "Custom uninstall arguments", allowCLI);
            opts.PreInstallCommand = ReportStr(ref report, pkg.Id, opts.PreInstallCommand, "Pre-install command", allowPrePost);
            opts.PostInstallCommand = ReportStr(ref report, pkg.Id, opts.PostInstallCommand, "Post-install command", allowPrePost);
            opts.PreUpdateCommand = ReportStr(ref report, pkg.Id, opts.PreUpdateCommand, "Pre-update command", allowPrePost);
            opts.PostUpdateCommand = ReportStr(ref report, pkg.Id, opts.PostUpdateCommand, "Post-update command", allowPrePost);
            opts.PreUninstallCommand = ReportStr(ref report, pkg.Id, opts.PreUninstallCommand, "Pre-uninstall command", allowPrePost);
            opts.PostUninstallCommand = ReportStr(ref report, pkg.Id, opts.PostUninstallCommand, "Post-uninstall command", allowPrePost);
            pkg.InstallationOptions = opts;
            packages.Add(DeserializePackage(pkg));
        }

        foreach (var pkg in deserializedData.incompatible_packages)
            packages.Add(DeserializeIncompatiblePackage(pkg, NullSource.Instance));

        await PackageBundlesLoader.Instance.AddPackagesAsync(packages);

        return (deserializedData.export_version, report);
    }

    // ─── Deserialization helpers ──────────────────────────────────────────────
    public static IPackage DeserializePackage(SerializablePackage raw)
    {
        IPackageManager? manager = null;
        foreach (var m in PEInterface.Managers)
        {
            if (m.Name == raw.ManagerName || m.DisplayName == raw.ManagerName)
            { manager = m; break; }
        }

        IManagerSource? source;
        if (manager?.Capabilities.SupportsCustomSources == true)
        {
            if (raw.Source.Contains(": "))
                raw.Source = raw.Source.Split(": ")[^1];
            source = manager?.SourcesHelper?.Factory.GetSourceIfExists(raw.Source);
        }
        else
            source = manager?.DefaultSource;

        if (manager is null || source is null)
            return DeserializeIncompatiblePackage(raw.GetInvalidEquivalent(), NullSource.Instance);

        return new ImportedPackage(raw, manager, source);
    }

    public static IPackage DeserializeIncompatiblePackage(SerializableIncompatiblePackage raw, IManagerSource source)
        => new InvalidImportedPackage(raw, source);

    // ─── Security report helpers ──────────────────────────────────────────────
    private static void ReportList(ref BundleReport report, string id, List<string> values, string label, bool allowed)
    {
        if (!values.Any(x => x.Any())) return;
        if (!report.Contents.ContainsKey(id)) report.Contents[id] = [];
        report.Contents[id].Add(new BundleReportEntry($"{label}: [{string.Join(", ", values)}]", allowed));
        report.IsEmpty = false;
        if (!allowed) values.Clear();
    }

    private static string ReportStr(ref BundleReport report, string id, string value, string label, bool allowed)
    {
        if (!value.Any()) return value;
        if (!report.Contents.ContainsKey(id)) report.Contents[id] = [];
        report.Contents[id].Add(new BundleReportEntry($"{label}: {value}", allowed));
        report.IsEmpty = false;
        return allowed ? value : "";
    }

    // ─── Batch script export ──────────────────────────────────────────────────
    private async Task CreateBatchScriptAsync()
    {
        try
        {
            if (GetMainWindow() is not { } win) return;

            string defaultName = CoreTools.Translate("Install script") + ".ps1";
            var file = await win.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = defaultName,
                FileTypeChoices =
                [
                    new FilePickerFileType(CoreTools.Translate("PowerShell script")) { Patterns = ["*.ps1"] },
                ],
            });

            var path = file?.TryGetLocalPath();
            if (path is null) return;

            var packages = new List<string>();
            var commands = new List<string>();

            bool forceKill = Settings.Get(Settings.K.KillProcessesThatRefuseToDie);
            foreach (var p in _loader.Packages)
            {
                if (p is not ImportedPackage pkg) continue;

                packages.Add(pkg.Name + " from " + pkg.Manager.DisplayName);

                foreach (var process in pkg.installation_options.KillBeforeOperation)
                    commands.Add($"taskkill /im \"{process}\"" + (forceKill ? " /f" : ""));

                if (pkg.installation_options.PreInstallCommand != "")
                    commands.Add(pkg.installation_options.PreInstallCommand);

                var param = pkg.Manager.OperationHelper.GetParameters(
                    pkg, pkg.installation_options, OperationType.Install);
                commands.Add($"{pkg.Manager.Properties.ExecutableFriendlyName} {string.Join(' ', param)}");

                if (pkg.installation_options.PostInstallCommand != "")
                    commands.Add(pkg.installation_options.PostInstallCommand);
            }

            await File.WriteAllTextAsync(path, GenerateCommandString(packages, commands));

            MainWindow.Instance?.ShowBanner(
                CoreTools.Translate("Success!"),
                CoreTools.Translate("The installation script saved to {0}", path),
                MainWindow.RuntimeNotificationLevel.Success);

            TelemetryHandler.ExportBatch();
            await CoreTools.ShowFileOnExplorer(path);
        }
        catch (Exception ex)
        {
            Logger.Error("An error occurred while attempting to export an installation script");
            Logger.Error(ex);
            MainWindow.Instance?.ShowBanner(
                CoreTools.Translate("An error occurred"),
                CoreTools.Translate("An error occurred while attempting to create an installation script:") + " " + ex.Message,
                MainWindow.RuntimeNotificationLevel.Error);
        }
    }

    private static string GenerateCommandString(IReadOnlyList<string> names, IReadOnlyList<string> commands)
    {
        return $$"""
            Clear-Host
            Write-Host ""
            Write-Host "========================================================"
            Write-Host ""
            Write-Host "        __  __      _ ______     __  __  ______" -ForegroundColor Cyan
            Write-Host "       / / / /___  (_) ____/__  / _// / / /  _/" -ForegroundColor Cyan
            Write-Host "      / / / / __ \/ / / __/ _ \/ __/ / / // /" -ForegroundColor Cyan
            Write-Host "     / /_/ / / / / / /_/ /  __/ /_/ /_/ // /" -ForegroundColor Cyan
            Write-Host "     \____/_/ /_/_/\____/\___/\__/\____/___/" -ForegroundColor Cyan
            Write-Host "          UniGetUI Package Installer Script"
            Write-Host "        Created with UniGetUI Version {{CoreData.VersionName}}"
            Write-Host ""
            Write-Host "========================================================"
            Write-Host ""
            Write-Host "NOTES:" -ForegroundColor Yellow
            Write-Host "  - The install process will not be as reliable as importing a bundle with UniGetUI. Expect issues and errors." -ForegroundColor Yellow
            Write-Host "  - Packages will be installed with the install options specified at the time of creation of this script." -ForegroundColor Yellow
            Write-Host "  - Error/Success detection may not be 100% accurate." -ForegroundColor Yellow
            Write-Host "  - Some of the packages may require elevation. Some of them may ask for permission, but others may fail. Consider running this script elevated." -ForegroundColor Yellow
            Write-Host "  - You can skip confirmation prompts by running this script with the parameter `/DisablePausePrompts` " -ForegroundColor Yellow
            Write-Host ""
            Write-Host ""
            if ($args[0] -ne "/DisablePausePrompts") { pause }
            Write-Host ""
            Write-Host "This script will attempt to install the following packages:"
            {{string.Join('\n', names.Select(x => $"Write-Host \"  - {x}\""))}}
            Write-Host ""
            if ($args[0] -ne "/DisablePausePrompts") { pause }
            Clear-Host

            $success_count=0
            $failure_count=0
            $commands_run=0
            $results=""

            $commands= @(
                {{string.Join(
                    ",\n    ",
                    commands.Select(x => $"'cmd.exe /C {x.Replace("'", "''")}'")
                )}}
            )

            foreach ($command in $commands) {
                Write-Host "Running: $command" -ForegroundColor Yellow
                cmd.exe /C $command
                if ($LASTEXITCODE -eq 0) {
                    Write-Host "[  OK  ] $command" -ForegroundColor Green
                    $success_count++
                    $results += "$([char]0x1b)[32m[  OK  ] $command`n"
                }
                else {
                    Write-Host "[ FAIL ] $command" -ForegroundColor Red
                    $failure_count++
                    $results += "$([char]0x1b)[31m[ FAIL ] $command`n"
                }
                $commands_run++
                Write-Host ""
            }

            Write-Host "========================================================"
            Write-Host "                  OPERATION SUMMARY"
            Write-Host "========================================================"
            Write-Host "Total commands run: $commands_run"
            Write-Host "Successful: $success_count"
            Write-Host "Failed: $failure_count"
            Write-Host ""
            Write-Host "Details:"
            Write-Host "$results$([char]0x1b)[37m"
            Write-Host "========================================================"

            if ($failure_count -gt 0) {
                Write-Host "Some commands failed. Please check the log above." -ForegroundColor Yellow
            }
            else {
                Write-Host "All commands executed successfully!" -ForegroundColor Green
            }
            Write-Host ""
            if ($args[0] -ne "/DisablePausePrompts") { pause }
            exit $failure_count
            """;
    }

    // ─── Dialog helpers ───────────────────────────────────────────────────────
    private static async Task<bool> AskLoseChanges()
    {
        if (GetMainWindow() is not { } owner) return false;
        var dialog = new DiscardBundleChangesDialog();
        await dialog.ShowDialog(owner);
        return dialog.Confirmed;
    }

    private static async Task ShowBundleSecurityReport(Window owner, BundleReport report)
        => await new BundleSecurityReportDialog(report).ShowDialog(owner);

    private static async Task ShowErrorDialog(Window owner, string title, string message)
        => await new SimpleErrorDialog(title, message).ShowDialog(owner);
}
