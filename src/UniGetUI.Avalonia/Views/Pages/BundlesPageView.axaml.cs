using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Models;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Telemetry;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Classes.Serializable;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Views.Pages;

/// <summary>
/// Row view-model for the bundle package list.
/// </summary>
public sealed class BundleRowModel : INotifyPropertyChanged
{
    public IPackage Package { get; }
    public string Name => Package.Name;
    public string Id => Package.Id;
    public string Version => Package.VersionString;
    public string Source => Package.Source.AsString_DisplayName;
    public bool IsValid => Package is ImportedPackage;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public BundleRowModel(IPackage package)
    {
        Package = package;
    }
}

public partial class BundlesPageView : UserControl, IShellPage
{
    // ── IShellPage ──────────────────────────────────────────────────────────
    public string Title { get; } = string.Empty;
    public string Subtitle { get; } = string.Empty;
    public bool SupportsSearch => true;
    public string SearchPlaceholder { get; } = string.Empty;

    public void UpdateSearchQuery(string query)
    {
        _searchQuery = query.Trim();
        RefreshRows();
    }

    // ── State ────────────────────────────────────────────────────────────────
    private readonly ObservableCollection<BundleRowModel> _visibleRows = [];
    private BundleRowModel? _selectedRow;
    private string _searchQuery = string.Empty;
    private AbstractOperation? _lastOperation;

    private bool _hasUnsavedChanges;
    private bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        set
        {
            _hasUnsavedChanges = value;
            Dispatcher.UIThread.Post(UpdateStatusBadge);
        }
    }

    // ── Control accessors ────────────────────────────────────────────────────
    private Button NewBtn => GetControl<Button>("NewBundleButton");
    private Button OpenBtn => GetControl<Button>("OpenBundleButton");
    private Button SaveBtn => GetControl<Button>("SaveBundleButton");
    private Button CreateScriptBtn => GetControl<Button>("CreateScriptButton");
    private Button InstallBtn => GetControl<Button>("InstallSelectedButton");
    private Button InstallDropdownBtn => GetControl<Button>("InstallDropdownButton");
    private Button RemoveBtn => GetControl<Button>("RemoveSelectedButton");
    private Button DetailsBtn => GetControl<Button>("BundleDetailsButton");
    private Button ShareBtn => GetControl<Button>("BundleShareButton");
    private Button ViewOutputBtn => GetControl<Button>("BundleViewOutputButton");
    private Button AddToBundleInfoBtn => GetControl<Button>("AddToBundleInfoButton");
    private Button HelpBtn => GetControl<Button>("BundleHelpButton");
    private TextBlock StateText => GetControl<TextBlock>("BundleStateBlock");
    private TextBlock StatusBadge => GetControl<TextBlock>("BundleStatusBlock");
    private TextBox SearchBox => GetControl<TextBox>("BundleSearchBox");
    private CheckBox CheckAll => GetControl<CheckBox>("CheckAllCheckBox");
    private ItemsControl Rows => GetControl<ItemsControl>("BundleRowsItemsControl");
    private ScrollViewer RowsHost => GetControl<ScrollViewer>("BundleRowsScrollViewer");
    private Border EmptyCard => GetControl<Border>("BundleEmptyStateCard");
    private TextBlock TitleBlock => GetControl<TextBlock>("BundlesTitleBlock");
    private TextBlock SubtitleBlock => GetControl<TextBlock>("BundlesSubtitleBlock");

    // ── Constructor ──────────────────────────────────────────────────────────
    public BundlesPageView()
    {
        InitializeComponent();
        ApplyTranslations();
        Rows.ItemsSource = _visibleRows;

        // Toolbar wiring
        NewBtn.Click += NewBtn_OnClick;
        OpenBtn.Click += OpenBtn_OnClick;
        SaveBtn.Click += SaveBtn_OnClick;
        InstallBtn.Click += async (_, _) => await QueueInstallCheckedAsync();
        InstallDropdownBtn.Click += InstallDropdownBtn_OnClick;
        RemoveBtn.Click += RemoveBtn_OnClick;
        DetailsBtn.Click += DetailsBtn_OnClick;
        ShareBtn.Click += ShareBtn_OnClick;
        AddToBundleInfoBtn.Click += AddToBundleInfoBtn_OnClick;
        HelpBtn.Click += HelpBtn_OnClick;
        CreateScriptBtn.Click += async (_, _) => await CreateBatchScriptAsync();
        CheckAll.IsCheckedChanged += CheckAll_OnChanged;

        SearchBox.TextChanged += (_, _) =>
        {
            _searchQuery = SearchBox.Text?.Trim() ?? string.Empty;
            RefreshRows();
        };

        // Row pointer-press for selection
        Rows.AddHandler(
            InputElement.PointerPressedEvent,
            OnRowsPointerPressed,
            RoutingStrategies.Bubble
        );

        // Loader events
        var loader = PackageBundlesLoader.Instance;
        loader.PackagesChanged += (_, _) =>
        {
            HasUnsavedChanges = true;
            Dispatcher.UIThread.Post(RefreshRows);
        };
        loader.FinishedLoading += (_, _) => Dispatcher.UIThread.Post(RefreshRows);

        RefreshRows();
    }

    private void ApplyTranslations()
    {
        TitleBlock.Text = CoreTools.Translate("Package Bundles");
        SubtitleBlock.Text = CoreTools.Translate("Add packages or open an existing bundle");

        NewBtn.Content = CoreTools.Translate("New");
        OpenBtn.Content = CoreTools.Translate("Open");
        SaveBtn.Content = CoreTools.Translate("Save as");
        InstallBtn.Content = CoreTools.Translate("Install selection");
        RemoveBtn.Content = CoreTools.Translate("Remove selection from bundle");
        DetailsBtn.Content = CoreTools.Translate("Package details");
        ShareBtn.Content = CoreTools.Translate("Share");
        HelpBtn.Content = CoreTools.Translate("Help");
        ToolTip.SetTip(AddToBundleInfoBtn, CoreTools.Translate(
            "To add packages to a bundle, right-click on a package in the Discover, Updates or Installed pages and select \"Add to bundle\"."));
        CreateScriptBtn.Content = CoreTools.Translate("Create .ps1 script");
        SearchBox.Watermark = CoreTools.Translate("Search");

        GetControl<TextBlock>("BundleEmptyTitleBlock").Text =
            CoreTools.Translate("Add packages or open an existing package bundle");
        GetControl<TextBlock>("BundleEmptyDescBlock").Text =
            CoreTools.Translate("The current bundle has no packages. Add some packages to get started");
        GetControl<TextBlock>("BundleColNameBlock").Text = CoreTools.Translate("Package Name");
        GetControl<TextBlock>("BundleColIdBlock").Text = CoreTools.Translate("Package ID");
        GetControl<TextBlock>("BundleColVersionBlock").Text = CoreTools.Translate("Version");
        GetControl<TextBlock>("BundleColSourceBlock").Text = CoreTools.Translate("Source");
    }

    // ── PS1 script export ────────────────────────────────────────────────────

    private async Task CreateBatchScriptAsync()
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = CoreTools.Translate("Create .ps1 script"),
                SuggestedFileName = CoreTools.Translate("Install script") + ".ps1",
                FileTypeChoices = [new FilePickerFileType("PowerShell script") { Patterns = ["*.ps1"] }],
            });
            if (file is null) return;

            var packages = new List<string>();
            var commands = new List<string>();
            var forceKill = Settings.Get(Settings.K.KillProcessesThatRefuseToDie);

            foreach (var pkg in PackageBundlesLoader.Instance.Packages)
            {
                if (pkg is not ImportedPackage package) continue;

                packages.Add(package.Name + " from " + package.Manager.DisplayName);

                foreach (var process in package.installation_options.KillBeforeOperation)
                    commands.Add($"taskkill /im \"{process}\"" + (forceKill ? " /f" : ""));

                if (package.installation_options.PreInstallCommand != "")
                    commands.Add(package.installation_options.PreInstallCommand);

                var exeName = package.Manager.Properties.ExecutableFriendlyName;
                var param = package.Manager.OperationHelper.GetParameters(
                    package,
                    package.installation_options,
                    OperationType.Install
                );
                commands.Add($"{exeName} {string.Join(' ', param)}");

                if (package.installation_options.PostInstallCommand != "")
                    commands.Add(package.installation_options.PostInstallCommand);
            }

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(GeneratePowerShellScript(packages, commands));

            TelemetryHandler.ExportBatch();
            StateText.Text = CoreTools.Translate("The installation script saved to {0}", file.Name);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to create batch script:");
            Logger.Error(ex);
        }
    }

    private static string GeneratePowerShellScript(
        IReadOnlyList<string> names,
        IReadOnlyList<string> commands)
    {
        return $$"""
            Clear-Host
            Write-Host ""
            Write-Host "========================================================"
            Write-Host ""
            Write-Host "        __  __      _ ______     __  __  ______" -ForegroundColor Cyan
            Write-Host "       / / / /___  (_) ____/__  / /_/ / / /  _/" -ForegroundColor Cyan
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
            Write-Host "  - Error/Sucess detection may not be 100% accurate." -ForegroundColor Yellow
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
                commands.Select(x => $"'cmd.exe /C {x.Replace("'", "''")}'"))}}
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

    // ── Row management ───────────────────────────────────────────────────────

    private void RefreshRows()
    {
        var packages = GetFilteredPackages();

        // Deselect removed row
        if (_selectedRow is not null && !packages.Any(r => r.Package == _selectedRow.Package))
        {
            _selectedRow.IsSelected = false;
            _selectedRow = null;
        }

        _visibleRows.Clear();
        foreach (var row in packages)
            _visibleRows.Add(row);

        RowsHost.IsVisible = _visibleRows.Count > 0;
        EmptyCard.IsVisible = _visibleRows.Count == 0;

        UpdateToolbar();
        UpdateStatusBadge();
    }

    private List<BundleRowModel> GetFilteredPackages()
    {
        var allPackages = PackageBundlesLoader.Instance.Packages;
        if (string.IsNullOrWhiteSpace(_searchQuery))
            return allPackages
                .Select(FindOrCreateRow)
                .ToList();

        var q = _searchQuery.ToLowerInvariant();
        return allPackages
            .Where(p =>
                p.Name.ToLowerInvariant().Contains(q)
                || p.Id.ToLowerInvariant().Contains(q))
            .Select(FindOrCreateRow)
            .ToList();
    }

    private readonly Dictionary<IPackage, BundleRowModel> _rowCache = new(ReferenceEqualityComparer.Instance);

    private BundleRowModel FindOrCreateRow(IPackage package)
    {
        if (!_rowCache.TryGetValue(package, out var row))
        {
            row = new BundleRowModel(package);
            _rowCache[package] = row;
        }
        return row;
    }

    private void UpdateToolbar()
    {
        bool anyRows = _visibleRows.Count > 0;
        bool anyChecked = _visibleRows.Any(r => r.IsChecked);
        bool rowSelected = _selectedRow is not null && _visibleRows.Any(r => r == _selectedRow);

        InstallBtn.IsEnabled = anyChecked;
        InstallDropdownBtn.IsEnabled = anyChecked;
        RemoveBtn.IsEnabled = anyChecked || rowSelected;
        DetailsBtn.IsEnabled = rowSelected;
        ShareBtn.IsEnabled = rowSelected;
        SaveBtn.IsEnabled = anyRows;
    }

    private void UpdateStatusBadge()
    {
        StatusBadge.Text = _hasUnsavedChanges
            ? CoreTools.Translate("You may lose unsaved data")
            : string.Empty;
    }

    // ── Row selection ────────────────────────────────────────────────────────

    private void OnRowsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var visual = e.Source as global::Avalonia.Visual;
        while (visual is not null)
        {
            if (visual is Control ctrl && ctrl.DataContext is BundleRowModel row)
            {
                SelectRow(row);
                return;
            }
            visual = visual.GetVisualParent();
        }
    }

    private void SelectRow(BundleRowModel row)
    {
        _selectedRow?.IsSelected = false;
        _selectedRow = row;
        _selectedRow.IsSelected = true;
        UpdateToolbar();
    }

    // ── Check-all ────────────────────────────────────────────────────────────

    private void CheckAll_OnChanged(object? sender, RoutedEventArgs e)
    {
        bool check = CheckAll.IsChecked ?? false;
        foreach (var row in _visibleRows)
            row.IsChecked = check;
        UpdateToolbar();
    }

    // ── Toolbar handlers ─────────────────────────────────────────────────────

    private async void NewBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        if (PackageBundlesLoader.Instance.Any() && HasUnsavedChanges)
        {
            bool confirmed = await ShowConfirmDialogAsync(
                CoreTools.Translate("You may lose unsaved data"),
                CoreTools.Translate("Are you sure you want to create a new package bundle? "),
                CoreTools.Translate("Continue"),
                CoreTools.Translate("Cancel")
            );
            if (!confirmed) return;
        }

        _rowCache.Clear();
        PackageBundlesLoader.Instance.ClearPackages(emitFinishSignal: true);
        HasUnsavedChanges = false;
        StateText.Text = CoreTools.Translate("New bundle");
        RefreshRows();
    }

    private async void OpenBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        if (PackageBundlesLoader.Instance.Any() && HasUnsavedChanges)
        {
            bool confirmed = await ShowConfirmDialogAsync(
                CoreTools.Translate("You may lose unsaved data"),
                CoreTools.Translate("Any unsaved changes will be lost"),
                CoreTools.Translate("Continue"),
                CoreTools.Translate("Cancel")
            );
            if (!confirmed) return;
        }

        var topLevel = global::Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = CoreTools.Translate("Open existing bundle"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("UniGetUI Bundle (*.ubundle)") { Patterns = ["*.ubundle"] },
                new FilePickerFileType("JSON (*.json)")               { Patterns = ["*.json"]    },
                new FilePickerFileType("YAML (*.yaml)")               { Patterns = ["*.yaml"]    },
                new FilePickerFileType("XML  (*.xml)")                { Patterns = ["*.xml"]     },
            ],
        });

        if (files.Count == 0) return;
        await OpenFromFileAsync(files[0].Path.LocalPath);
    }

    internal async Task OpenFromFileAsync(string filePath)
    {
        StateText.Text = CoreTools.Translate("Loading packages, please wait...");
        try
        {
            string ext = filePath.Split('.')[^1].ToLowerInvariant();
            BundleFormatType format = ext switch
            {
                "yaml" => BundleFormatType.YAML,
                "xml" => BundleFormatType.XML,
                "json" => BundleFormatType.JSON,
                _ => BundleFormatType.UBUNDLE,
            };

            string content = await File.ReadAllTextAsync(filePath);
            await AddFromBundleStringAsync(content, format);
            TelemetryHandler.ImportBundle(format);
            HasUnsavedChanges = false;
            StateText.Text = CoreTools.Translate("{0} packages found", PackageBundlesLoader.Instance.Packages.Count);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to open bundle file");
            Logger.Error(ex);
            StateText.Text = CoreTools.Translate("The package bundle is not valid");
        }
    }

    private async void SaveBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = global::Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = CoreTools.Translate("Save bundle as"),
            SuggestedFileName = CoreTools.Translate("Package bundle"),
            FileTypeChoices =
            [
                new FilePickerFileType("UniGetUI Bundle (*.ubundle)") { Patterns = ["*.ubundle"] },
                new FilePickerFileType("JSON (*.json)")               { Patterns = ["*.json"]    },
            ],
        });

        if (file is null) return;

        StateText.Text = CoreTools.Translate("Saving packages, please wait...");
        try
        {
            string serialized = await CreateBundleStringAsync(PackageBundlesLoader.Instance.Packages);
            string path = file.Path.LocalPath;
            await File.WriteAllTextAsync(path, serialized);
            string saveExt = path.Split('.')[^1].ToLowerInvariant();
            TelemetryHandler.ExportBundle(saveExt == "json" ? BundleFormatType.JSON : BundleFormatType.UBUNDLE);
            HasUnsavedChanges = false;
            StateText.Text = CoreTools.Translate("The bundle was created successfully on {0}", path);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save bundle file");
            Logger.Error(ex);
            StateText.Text = CoreTools.Translate("Could not create bundle");
        }
    }

    private void AddToBundleInfoBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        StateText.Text = CoreTools.Translate(
            "To add packages to a bundle, right-click on a package in the Discover, Updates or Installed pages and select \"Add to bundle\".");
    }

    private void HelpBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        var shell = this.FindAncestorOfType<MainShellView>();
        shell?.OpenPage(ShellPageType.Help);
    }

    private void InstallDropdownBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        var adminItem = new MenuItem { Header = CoreTools.Translate("Install as administrator") };
        adminItem.Click += async (_, _) => await QueueInstallCheckedAsync(elevated: true);
        menu.Items.Add(adminItem);

        var interactiveItem = new MenuItem { Header = CoreTools.Translate("Interactive installation") };
        interactiveItem.Click += async (_, _) => await QueueInstallCheckedAsync(interactive: true);
        menu.Items.Add(interactiveItem);

        var skipHashItem = new MenuItem { Header = CoreTools.Translate("Skip integrity checks") };
        skipHashItem.Click += async (_, _) => await QueueInstallCheckedAsync(skipHash: true);
        menu.Items.Add(skipHashItem);

        if (sender is Control anchor)
        {
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.Bottom;
            menu.Open(anchor);
        }
    }

    private async void InstallBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        await QueueInstallCheckedAsync();
    }

    private async Task QueueInstallCheckedAsync(
        bool elevated = false,
        bool interactive = false,
        bool skipHash = false)
    {
        var checkedPackages = _visibleRows
            .Where(r => r.IsChecked && r.IsValid)
            .Select(r => r.Package)
            .ToList();

        if (checkedPackages.Count == 0)
        {
            StateText.Text = CoreTools.Translate("No package was selected");
            return;
        }

        StateText.Text = CoreTools.Translate("Preparing packages, please wait...");
        var toInstall = new List<Package>();

        foreach (var pkg in checkedPackages)
        {
            if (pkg is ImportedPackage imported)
            {
                try
                {
                    toInstall.Add(await imported.RegisterAndGetPackageAsync());
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to register bundle package {imported.Id}");
                    Logger.Error(ex);
                }
            }
        }

        if (toInstall.Count == 0)
        {
            StateText.Text = CoreTools.Translate("No package was selected");
            return;
        }

        StateText.Text = CoreTools.Translate("{0} packages found", toInstall.Count);

        foreach (var package in toInstall)
        {
            try
            {
                var options = await InstallOptionsFactory.LoadApplicableAsync(package);
                if (elevated) options.RunAsAdministrator = true;
                if (interactive) options.InteractiveInstallation = true;
                if (skipHash) options.SkipHashCheck = true;
                var op = new InstallPackageOperation(package, options);
                _lastOperation = op;
                ViewOutputBtn.IsVisible = true;
                AvaloniaOperationRegistry.Add(op);
                _ = op.MainThread();
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to queue installation of {package.Id}");
                Logger.Error(ex);
            }
        }
    }

    private async void BundleViewOutputButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_lastOperation is null) return;
        var window = new OperationLogWindow(_lastOperation);
        if (VisualRoot is Window parentWindow)
            await window.ShowDialog(parentWindow);
        else
            window.Show();
    }

    private void RemoveBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        var checkedPackages = _visibleRows
            .Where(r => r.IsChecked)
            .Select(r => r.Package)
            .ToList();

        if (checkedPackages.Count > 0)
        {
            foreach (var pkg in checkedPackages)
                _rowCache.Remove(pkg);
            PackageBundlesLoader.Instance.RemoveRange(checkedPackages);
            HasUnsavedChanges = true;
            return;
        }

        // Fall back to selected row
        if (_selectedRow is not null)
        {
            _rowCache.Remove(_selectedRow.Package);
            PackageBundlesLoader.Instance.RemoveRange([_selectedRow.Package]);
            _selectedRow = null;
            HasUnsavedChanges = true;
        }
    }

    private async void DetailsBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedRow is null) return;
        var pkg = _selectedRow.Package;
        if (pkg.Source.IsVirtualManager)
        {
            StateText.Text = CoreTools.Translate("Show package details");
            return;
        }
        var window = new PackageDetailsWindow(pkg, PackagePageMode.None);
        if (VisualRoot is Window parentWindow)
            await window.ShowDialog(parentWindow);
        else
            window.Show();
    }

    private async void ShareBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedRow is null) return;
        var package = _selectedRow.Package;
        if (package.Source.IsVirtualManager)
        {
            StateText.Text = CoreTools.Translate("Share this package");
            return;
        }
        var url = "https://marticliment.com/unigetui/share?"
            + "name=" + Uri.EscapeDataString(package.Name)
            + "&id=" + Uri.EscapeDataString(package.Id)
            + "&sourceName=" + Uri.EscapeDataString(package.Source.Name)
            + "&managerName=" + Uri.EscapeDataString(package.Manager.DisplayName);
        var clipboard = global::Avalonia.Controls.TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(url);
            StateText.Text = CoreTools.Translate("Copy to clipboard");
        }
    }

    // ── Bundle serialization helpers ─────────────────────────────────────────

    internal static async Task<string> CreateBundleStringAsync(IReadOnlyList<IPackage> packages)
    {
        var bundle = new SerializableBundle();
        var sorted = packages.ToList();
        sorted.Sort((a, b) =>
        {
            int c = string.Compare(a.Id, b.Id, StringComparison.Ordinal);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        foreach (var pkg in sorted)
        {
            if (pkg is Package && !pkg.Source.IsVirtualManager)
                bundle.packages.Add(await pkg.AsSerializableAsync());
            else
                bundle.incompatible_packages.Add(pkg.AsSerializable_Incompatible());
        }

        return bundle.AsJsonString();
    }

    private async Task AddFromBundleStringAsync(string content, BundleFormatType format)
    {
        if (format is BundleFormatType.YAML)
            content = await SerializationHelpers.YAML_to_JSON(content);

        if (format is BundleFormatType.XML)
            content = await SerializationHelpers.XML_to_JSON(content);

        var data = await Task.Run(() =>
            new SerializableBundle(
                JsonNode.Parse(content) ?? throw new JsonException("Could not parse JSON")
            )
        );

        bool allowCLI = SecureSettings.Get(SecureSettings.K.AllowCLIArguments)
                         && SecureSettings.Get(SecureSettings.K.AllowImportingCLIArguments);
        bool allowPrePost = SecureSettings.Get(SecureSettings.K.AllowPrePostOpCommand)
                         && SecureSettings.Get(SecureSettings.K.AllowImportPrePostOpCommands);

        // Build a security report tracking stripped / allowed-through sensitive settings
        var report = new UniGetUI.Interface.Enums.BundleReport { IsEmpty = true };

        void AddToReport(string pkgId, string line, bool allowed)
        {
            report.IsEmpty = false;
            if (!report.Contents.ContainsKey(pkgId))
                report.Contents[pkgId] = new List<UniGetUI.Interface.Enums.BundleReportEntry>();
            report.Contents[pkgId].Add(new UniGetUI.Interface.Enums.BundleReportEntry(line, allowed));
        }

        var packages = new List<IPackage>();
        foreach (var pkg in data.packages)
        {
            var opts = pkg.InstallationOptions;

            if (opts.CustomParameters_Install.Any(x => x.Any()))
            {
                AddToReport(pkg.Id, $"Custom install arguments: [{string.Join(", ", opts.CustomParameters_Install)}]", allowCLI);
                if (!allowCLI) opts.CustomParameters_Install.Clear();
            }
            if (opts.CustomParameters_Update.Any(x => x.Any()))
            {
                AddToReport(pkg.Id, $"Custom update arguments: [{string.Join(", ", opts.CustomParameters_Update)}]", allowCLI);
                if (!allowCLI) opts.CustomParameters_Update.Clear();
            }
            if (opts.CustomParameters_Uninstall.Any(x => x.Any()))
            {
                AddToReport(pkg.Id, $"Custom uninstall arguments: [{string.Join(", ", opts.CustomParameters_Uninstall)}]", allowCLI);
                if (!allowCLI) opts.CustomParameters_Uninstall.Clear();
            }
            if (opts.PreInstallCommand.Any())
            {
                AddToReport(pkg.Id, $"Pre-install command: {opts.PreInstallCommand}", allowPrePost);
                if (!allowPrePost) opts.PreInstallCommand = "";
            }
            if (opts.PostInstallCommand.Any())
            {
                AddToReport(pkg.Id, $"Post-install command: {opts.PostInstallCommand}", allowPrePost);
                if (!allowPrePost) opts.PostInstallCommand = "";
            }
            if (opts.PreUpdateCommand.Any())
            {
                AddToReport(pkg.Id, $"Pre-update command: {opts.PreUpdateCommand}", allowPrePost);
                if (!allowPrePost) opts.PreUpdateCommand = "";
            }
            if (opts.PostUpdateCommand.Any())
            {
                AddToReport(pkg.Id, $"Post-update command: {opts.PostUpdateCommand}", allowPrePost);
                if (!allowPrePost) opts.PostUpdateCommand = "";
            }
            if (opts.PreUninstallCommand.Any())
            {
                AddToReport(pkg.Id, $"Pre-uninstall command: {opts.PreUninstallCommand}", allowPrePost);
                if (!allowPrePost) opts.PreUninstallCommand = "";
            }
            if (opts.PostUninstallCommand.Any())
            {
                AddToReport(pkg.Id, $"Post-uninstall command: {opts.PostUninstallCommand}", allowPrePost);
                if (!allowPrePost) opts.PostUninstallCommand = "";
            }

            pkg.InstallationOptions = opts;
            packages.Add(DeserializePackage(pkg));
        }

        foreach (var inc in data.incompatible_packages)
            packages.Add(DeserializeIncompatiblePackage(inc));

        _rowCache.Clear();
        PackageBundlesLoader.Instance.ClearPackages(emitFinishSignal: false);
        await PackageBundlesLoader.Instance.AddPackagesAsync(packages);

        // Show security report if anything sensitive was found (D1)
        if (!report.IsEmpty)
            await ShowBundleSecurityReportAsync(report);
    }

    private static IPackage DeserializePackage(SerializablePackage raw)
    {
        IPackageManager? manager = PackageEngine.PEInterface.Managers
            .FirstOrDefault(m => m.Name == raw.ManagerName || m.DisplayName == raw.ManagerName);

        IManagerSource? source = null;
        if (manager?.Capabilities.SupportsCustomSources == true)
        {
            string sourceName = raw.Source.Contains(": ")
                ? raw.Source.Split(": ")[^1]
                : raw.Source;
            source = manager.SourcesHelper?.Factory.GetSourceIfExists(sourceName);
        }
        else
        {
            source = manager?.DefaultSource;
        }

        if (manager is null || source is null)
            return DeserializeIncompatiblePackage(raw.GetInvalidEquivalent());

        return new ImportedPackage(raw, manager, source);
    }

    private static IPackage DeserializeIncompatiblePackage(SerializableIncompatiblePackage raw)
    {
        return new InvalidImportedPackage(raw, NullSource.Instance);
    }

    // ── Confirm dialog helper ─────────────────────────────────────────────────

    private async Task ShowBundleSecurityReportAsync(UniGetUI.Interface.Enums.BundleReport report)
    {
        if (VisualRoot is not Window parentWindow)
            return;

        bool isDark = ActualThemeVariant == global::Avalonia.Styling.ThemeVariant.Dark;
        var colorIgnored = new global::Avalonia.Media.SolidColorBrush(isDark
            ? global::Avalonia.Media.Color.Parse("#FFD700") // gold
            : global::Avalonia.Media.Color.Parse("#B8860B")); // dark golden rod
        var colorAllowed = new global::Avalonia.Media.SolidColorBrush(isDark
            ? global::Avalonia.Media.Color.Parse("#FF6B6B") // light red
            : global::Avalonia.Media.Color.Parse("#CC0000")); // dark red

        var itemsList = new StackPanel { Spacing = 4 };

        foreach (var pair in report.Contents)
        {
            itemsList.Children.Add(new TextBlock
            {
                Text = $" - {CoreTools.Translate("Package")}: {pair.Key}:",
                FontFamily = new global::Avalonia.Media.FontFamily("Cascadia Code,Consolas,monospace"),
                FontWeight = global::Avalonia.Media.FontWeight.SemiBold,
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            });
            foreach (var entry in pair.Value)
            {
                itemsList.Children.Add(new TextBlock
                {
                    Text = $"   * {entry.Line}",
                    FontFamily = new global::Avalonia.Media.FontFamily("Cascadia Code,Consolas,monospace"),
                    Foreground = entry.Allowed ? colorAllowed : colorIgnored,
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                });
            }
        }

        var dialog = new Window
        {
            Title = CoreTools.Translate("Bundle security report"),
            Width = 700,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
        };

        var closeBtn = new Button { Content = CoreTools.Translate("Close"), HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right };
        closeBtn.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new global::Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = CoreTools.Translate("This package bundle had some settings that are potentially dangerous, and may be ignored by default."),
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = " - " + CoreTools.Translate("Entries that show in YELLOW will be IGNORED."),
                    Foreground = colorIgnored,
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = " - " + CoreTools.Translate("Entries that show in RED will be IMPORTED."),
                    Foreground = colorAllowed,
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = CoreTools.Translate("You can change this behavior on UniGetUI security settings."),
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = CoreTools.Translate("Details of the report:"),
                    FontWeight = global::Avalonia.Media.FontWeight.SemiBold,
                },
                new ScrollViewer
                {
                    HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = itemsList,
                },
                closeBtn,
            },
        };

        await dialog.ShowDialog(parentWindow);
    }

    private async Task<bool> ShowConfirmDialogAsync(
        string title,
        string message,
        string confirmLabel,
        string cancelLabel)
    {
        if (VisualRoot is not Window parentWindow)
            return true;

        var dialog = new Window
        {
            Title = title,
            Width = 460,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        bool result = false;
        var confirmBtn = new Button { Content = confirmLabel };
        var cancelBtn = new Button { Content = cancelLabel };
        confirmBtn.Click += (_, _) => { result = true; dialog.Close(); };
        cancelBtn.Click += (_, _) => { result = false; dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Margin = new global::Avalonia.Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 12,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { confirmBtn, cancelBtn },
                },
            },
        };

        await dialog.ShowDialog(parentWindow);
        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private T GetControl<T>(string name) where T : Control
    {
        return this.FindControl<T>(name)
            ?? throw new InvalidOperationException($"Control '{name}' not found.");
    }
}
