using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels.Pages;
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
    private MenuItem? _menuShare;
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
        downloadInstallers.Click += (_, _) => { /* TODO: download-only operation not yet ported */ };

        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("add_to", CoreTools.Translate("New"),
            () => _ = AskForNewBundle());
        ViewModel.AddToolbarButton("open_folder", CoreTools.Translate("Open"),
            () => _ = AskOpenFromFile());
        ViewModel.AddToolbarButton("save_as", CoreTools.Translate("Save as"),
            () => _ = SaveFile());
        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("delete", CoreTools.Translate("Remove selection from bundle"), () =>
        {
            HasUnsavedChanges = true;
            _loader.RemoveRange(vm.FilteredPackages.GetCheckedPackages());
        });
        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("info_round", CoreTools.Translate("Package details"),
            () => _ = ShowDetailsForPackage(SelectedItem), showLabel: false);
        ViewModel.AddToolbarButton("share", CoreTools.Translate("Share"),
            () => vm.RequestShareCommand.Execute(SelectedItem), showLabel: false);
        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("help", CoreTools.Translate("Help"),
            () => vm.RequestHelpCommand.Execute(null));
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
        _menuDownloadInstaller.Click += (_, _) => { /* TODO: download-only operation not yet ported */ };

        var menuRemoveFromList = new MenuItem { Header = CoreTools.AutoTranslated("Remove from list"), Icon = LoadMenuIcon("delete") };
        menuRemoveFromList.Click += (_, _) =>
        {
            if (SelectedItem is { } pkg)
            {
                HasUnsavedChanges = true;
                _loader.Remove(pkg);
            }
        };

        _menuShare = new MenuItem { Header = CoreTools.AutoTranslated("Share this package"), Icon = LoadMenuIcon("share") };
        _menuShare.Click += (_, _) => ViewModel.RequestShareCommand.Execute(SelectedItem);

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
        menu.Items.Add(_menuShare);
        menu.Items.Add(_menuDetails);
        return menu;
    }

    protected override void WhenShowingContextMenu(IPackage package)
    {
        if (_menuInstall is null || _menuInstallOptions is null || _menuAsAdmin is null
            || _menuInteractive is null || _menuSkipHash is null || _menuDownloadInstaller is null
            || _menuShare is null || _menuDetails is null)
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
        _menuShare.IsEnabled = isValid;
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

    // ─── Dialog helpers ───────────────────────────────────────────────────────
    private static async Task<bool> AskLoseChanges()
    {
        bool result = false;
        var win = new Window
        {
            Width = 460,
            Height = 200,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = CoreTools.Translate("Unsaved changes"),
        };

        var yesBtn = new Button { Content = CoreTools.Translate("Discard changes"), MinWidth = 140 };
        var noBtn = new Button { Content = CoreTools.Translate("Cancel"), MinWidth = 80 };
        yesBtn.Classes.Add("accent");
        yesBtn.Click += (_, _) => { result = true; win.Close(); };
        noBtn.Click += (_, _) => { result = false; win.Close(); };

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        btnRow.Children.Add(noBtn);
        btnRow.Children.Add(yesBtn);

        var root = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 12,
        };
        var titleBlock = new TextBlock
        {
            Text = CoreTools.Translate("Unsaved changes"),
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
        };
        var msgBlock = new TextBlock
        {
            Text = CoreTools.Translate("You have unsaved changes in the current bundle. Do you want to discard them?"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        };
        Grid.SetRow(titleBlock, 0); Grid.SetRow(msgBlock, 1); Grid.SetRow(btnRow, 2);
        root.Children.Add(titleBlock); root.Children.Add(msgBlock); root.Children.Add(btnRow);
        win.Content = root;

        if (Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            await win.ShowDialog(owner);

        return result;
    }

    private static async Task ShowBundleSecurityReport(Window owner, BundleReport report)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (pkgId, entries) in report.Contents)
        {
            sb.AppendLine($"• {pkgId}:");
            foreach (var entry in entries)
                sb.AppendLine($"    {(entry.Allowed ? "[allowed]" : "[stripped]")} {entry.Line}");
        }

        var win = new Window
        {
            Width = 580,
            Height = 420,
            CanResize = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = CoreTools.Translate("Bundle security report"),
        };
        var okBtn = new Button
        {
            Content = CoreTools.Translate("OK"),
            MinWidth = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        okBtn.Classes.Add("accent");
        okBtn.Click += (_, _) => win.Close();

        var root = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 12,
        };
        var title = new TextBlock
        {
            Text = CoreTools.Translate("The bundle contained restricted content"),
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
        };
        var scroll = new ScrollViewer
        {
            Content = new TextBlock
            {
                Text = sb.ToString(),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Monospace"),
                FontSize = 12,
                Opacity = 0.85,
            },
        };
        Grid.SetRow(title, 0); Grid.SetRow(scroll, 1); Grid.SetRow(okBtn, 2);
        root.Children.Add(title); root.Children.Add(scroll); root.Children.Add(okBtn);
        win.Content = root;

        await win.ShowDialog(owner);
    }

    private static async Task ShowErrorDialog(Window owner, string title, string message)
    {
        var win = new Window
        {
            Width = 480,
            Height = 200,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = title,
        };
        var okBtn = new Button
        {
            Content = CoreTools.Translate("OK"),
            MinWidth = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        okBtn.Classes.Add("accent");
        okBtn.Click += (_, _) => win.Close();

        var root = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 12,
        };
        var titleBlock = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold };
        var msgBlock = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        Grid.SetRow(titleBlock, 0); Grid.SetRow(msgBlock, 1); Grid.SetRow(okBtn, 2);
        root.Children.Add(titleBlock); root.Children.Add(msgBlock); root.Children.Add(okBtn);
        win.Content = root;

        await win.ShowDialog(owner);
    }
}
