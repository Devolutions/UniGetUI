using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UniGetUI.Avalonia.Models;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Telemetry;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.Views.Pages;

public sealed class CatalogEditorPage : AbstractPackagesPage
{
    private readonly CatalogEditorViewModel _document;
    private readonly CatalogPackagesLoader _loader;
    private CatalogEditorCatalog? _observedCatalog;
    private Button? _renameButton;
    private Button? _addButton;
    private Button? _discardButton;
    private bool _pickingFile;
    private bool _saveFailed;

    public CatalogEditorPage(CatalogEditorViewModel document)
        : this(document, new CatalogPackagesLoader()) { }

    private CatalogEditorPage(CatalogEditorViewModel document, CatalogPackagesLoader loader)
        : base(new PackagesPageData
        {
            PageName = "SoftwarePages.CatalogEditorPage",
            PageTitle = CoreTools.Translate("Catalog Editor"),
            IconName = "package_managers",
            PageRole = OperationType.Install,
            Loader = loader,
            DisableAutomaticPackageLoadOnStart = true,
            DisableReload = true,
            NoPackages_BackgroundText = CoreTools.Translate("This catalog contains no packages."),
            NoPackages_SourcesText = CoreTools.Translate("Add packages from Discover using Add selection to catalog."),
            NoPackages_SubtitleText_Base = CoreTools.Translate("No packages in this catalog"),
            MainSubtitle_StillLoading = CoreTools.Translate("Loading catalog"),
            NoMatches_BackgroundText = CoreTools.Translate("No packages match your filters"),
        }, vm =>
        {
            var selector = new CatalogSelector(document, catalog => RemoveCatalogAsync(document, catalog))
            { MinWidth = 260, MaxWidth = double.PositiveInfinity };
            vm.AddToolbarEntry(new ToolbarEntry(selector, "catalog", CoreTools.Translate("Select a catalog"),
                null, selector.ShowFlyoutAt), index: vm.ToolbarEntries.Count - 1);
        })
    {
        _document = document;
        _loader = loader;

        document.PropertyChanged += DocumentChanged;
        document.ErrorRaised += message => Dispatcher.UIThread.Post(() =>
        {
            _saveFailed = true;
            // Discover shares this document; only interrupt with a dialog while the editor is on screen.
            if (TopLevel.GetTopLevel(this) is not null && IsEffectivelyVisible) _ = ShowErrorAsync(message);
        });
        SyncCatalog();
        SyncStatus();
    }

    protected override void GenerateToolBar(PackagesPageViewModel vm)
    {
        SetMainButton("save_as", CoreTools.Translate("Save"), () => _ = SaveAsync(false));
        vm.AddToolbarButton("document_add", CoreTools.Translate("New file"), () => _ = NewAsync());
        vm.AddToolbarButton("open_folder", CoreTools.Translate("Open file"), () => _ = OpenAsync());
        vm.AddToolbarButton("save_as", CoreTools.Translate("Save as"), () => _ = SaveAsync(true));
        _discardButton = vm.AddToolbarButton("undo", CoreTools.Translate("Discard changes"),
            () => _ = DiscardChangesAsync());
        vm.AddToolbarButton("collections_add", CoreTools.Translate("New catalog"), () =>
        {
            if (_document.CanEdit && !_pickingFile) _document.NewCatalog();
        });
        _addButton = vm.AddToolbarButton("catalog_add", CoreTools.Translate("Add packages"), AddPackages);
        vm.AddToolbarButton("delete", CoreTools.Translate("Remove selection from catalog"), RemoveSelection);
        vm.AddToolbarButton("version", CoreTools.Translate("Pin version"), () => _ = PinVersionAsync(SelectedItem));
        vm.AddToolbarButton("info_round", CoreTools.Translate("Package details"),
            () => _ = ShowDetailsForPackage(SelectedItem));
        _renameButton = vm.AddToolbarButton("rename", CoreTools.Translate("Rename catalog"),
            () => _ = RenameCatalogAsync());
    }

    protected override ContextMenu GenerateContextMenu()
    {
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = CoreTools.Translate("Remove selection from catalog") };
        remove.Click += (_, _) => RemoveSelection();
        menu.Items.Add(remove);
        var pin = new MenuItem { Header = CoreTools.Translate("Pin version") };
        pin.Click += (_, _) => _ = PinVersionAsync(SelectedItem);
        menu.Items.Add(pin);
        return menu;
    }

    protected override void PerformMainPackageAction(IPackage? package) => _ = ShowDetailsForPackage(package);

    protected override Task ShowInstallationOptionsForPackage(IPackage? package) => ShowDetailsForPackage(package);

    protected override async Task ShowDetailsForPackage(IPackage? package)
    {
        if (package is null || GetMainWindow() is not { } window) return;
        if (package is InvalidImportedPackage)
        {
            window.ShowBanner(CoreTools.Translate("Package unavailable"),
                CoreTools.Translate("Enable this package's manager and source to view its details."),
                MainWindow.RuntimeNotificationLevel.Error);
            return;
        }
        var dialog = new PackageDetailsWindow(package, OperationType.None, TEL_InstallReferral.FROM_BUNDLE);
        await dialog.ShowDialog(window);
    }

    public override void OnEnter() => _ = LoadInitialAsync();

    private async Task LoadInitialAsync()
    {
        await _document.EnsureLoadedAsync();
        SyncCatalog();
        SyncStatus();
    }

    private void DocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CatalogEditorViewModel.SelectedCatalog))
            SyncCatalog();
        SyncStatus();
    }

    private void SyncCatalog()
    {
        _observedCatalog?.Packages.CollectionChanged -= PackagesChanged;
        _observedCatalog = _document.SelectedCatalog;
        _observedCatalog?.Packages.CollectionChanged += PackagesChanged;
        _loader.Replace(_observedCatalog?.Packages ?? []);
    }

    private void PackagesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        _loader.Replace(_observedCatalog?.Packages ?? []);

    private void SyncStatus()
    {
        ViewModel.SubtitleSuffix = _document.IsDirty ? " · " + CoreTools.Translate("Unsaved changes") : "";
        _renameButton?.IsEnabled = _document.CanEdit && _document.HasSelectedCatalog && !_pickingFile;
        _addButton?.IsEnabled = _document.CanEdit && _document.HasSelectedCatalog && !_pickingFile;
        _discardButton?.IsEnabled = _document.CanDiscardChanges && !_pickingFile;
        SetMainButtonState(_document.CanEdit && !_pickingFile, showDropdown: false);
    }

    private async Task RenameCatalogAsync()
    {
        if (!_document.CanEdit || _pickingFile || _document.SelectedCatalog is not { } catalog
            || GetMainWindow() is not { } window) return;
        var input = new TextBox { Text = catalog.Name, MinWidth = 280 };
        AutomationProperties.SetName(input, CoreTools.Translate("Catalog name"));
        var error = new TextBlock
        {
            Text = CoreTools.Translate("Enter a catalog name."),
            IsVisible = false,
            TextWrapping = TextWrapping.Wrap,
        };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = CoreTools.Translate("Catalog name") });
        body.Children.Add(input);
        body.Children.Add(error);
        var dialog = new ImmersiveConfirmationDialog(CoreTools.Translate("Rename catalog"), body,
            CoreTools.Translate("Rename"), CoreTools.Translate("Cancel"))
        { FocusPrimaryButton = false };
        var confirm = dialog.GetControl<Button>("PrimaryButton");
        input.TextChanged += (_, _) =>
        {
            bool valid = !string.IsNullOrWhiteSpace(input.Text);
            confirm.IsEnabled = valid;
            error.IsVisible = !valid;
        };
        input.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            input.Focus();
            input.SelectAll();
        }, DispatcherPriority.ApplicationIdle);
        await dialog.ShowDialog(window);
        if (dialog.Result is true && _document.CanEdit && ReferenceEquals(_document.SelectedCatalog, catalog)
            && !string.IsNullOrWhiteSpace(input.Text))
            catalog.Name = input.Text.Trim();
    }

    private static async Task RemoveCatalogAsync(CatalogEditorViewModel document, CatalogEditorCatalog catalog)
    {
        if (!document.CanEdit) return;
        if (catalog.Packages.Count > 0)
        {
            var dialog = new ImmersiveConfirmationDialog(CoreTools.Translate("Remove catalog"),
                new TextBlock
                {
                    Text = catalog.Packages.Count == 1
                        ? CoreTools.Translate("Remove catalog {0} and its package?", catalog.Name)
                        : CoreTools.Translate("Remove catalog {0} and its {1} packages?", catalog.Name, catalog.Packages.Count),
                    TextWrapping = TextWrapping.Wrap,
                },
                CoreTools.Translate("Remove"), CoreTools.Translate("Cancel"));
            await dialog.ShowDialog(MainWindow.Instance!);
            if (dialog.Result is not true) return;
        }
        document.RemoveCatalog(catalog);
    }

    private async Task DiscardChangesAsync()
    {
        if (!_document.CanDiscardChanges || _pickingFile) return;
        if (await ConfirmDiscardAsync()) _document.DiscardChanges();
    }

    // Packages are added from Discover, which shares this editor's selected catalog.
    private void AddPackages()
    {
        if (!_document.CanEdit || _pickingFile || _document.SelectedCatalog is not { } catalog
            || GetMainWindow() is not { DataContext: MainWindowViewModel main }) return;
        main.NavigateTo(PageType.Discover);
        var hint = new InfoBarViewModel
        {
            Title = CoreTools.Translate("Add packages to {0}", catalog.Name),
            Message = CoreTools.Translate("Search for packages, check the ones you want, then click Add selection to catalog."),
            Severity = InfoBarSeverity.Informational,
            IsClosable = true,
            IsOpen = true,
        };
        hint.OnClosed = () => main.DismissToast(hint);
        main.ShowToast(hint);
    }

    // Picks the version the catalog installs for a package: "Latest", or one the manager offers.
    private async Task PinVersionAsync(IPackage? package)
    {
        if (!_document.CanEdit || _pickingFile || _document.SelectedCatalog is not { } catalog || package is null
            || _loader.FindEntry(package) is not { } entry || GetMainWindow() is not { } window) return;
        if (package is InvalidImportedPackage)
        {
            window.ShowBanner(CoreTools.Translate("Package unavailable"),
                CoreTools.Translate("Enable this package's manager and source to pin its version."),
                MainWindow.RuntimeNotificationLevel.Error);
            return;
        }
        if (!package.Manager.Capabilities.SupportsCustomVersions)
        {
            window.ShowBanner(CoreTools.Translate("Pin version"),
                CoreTools.Translate("{0} cannot install a specific version, so this package always installs the latest version.", package.Manager.DisplayName),
                MainWindow.RuntimeNotificationLevel.Error);
            return;
        }

        string latest = CoreTools.Translate("Latest");
        var initialItems = entry.Version.Length > 0 ? new List<string> { latest, entry.Version } : [latest];
        var picker = new ComboBox
        {
            ItemsSource = initialItems,
            SelectedItem = initialItems[^1],
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 280,
        };
        AutomationProperties.SetName(picker, CoreTools.Translate("Version"));
        var status = new TextBlock { Text = CoreTools.Translate("Loading versions"), TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = CoreTools.Translate("Choose the version of {0} that this catalog installs.", entry.Name),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(picker);
        body.Children.Add(status);
        var dialog = new ImmersiveConfirmationDialog(CoreTools.Translate("Pin version"), body,
            CoreTools.Translate("Pin version"), CoreTools.Translate("Cancel"));

        async Task LoadVersionsAsync()
        {
            var items = new List<string> { latest };
            try
            {
                items.AddRange(await Task.Run(() => package.Manager.DetailsHelper.GetVersions(package)));
                status.IsVisible = false;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not list the versions of {entry.Id}.");
                Logger.Warn(ex);
                status.Text = CoreTools.Translate("The available versions could not be loaded: {0}", ex.Message);
            }
            if (entry.Version.Length > 0 && !items.Contains(entry.Version)) items.Insert(1, entry.Version);
            picker.ItemsSource = items;
            picker.SelectedItem = entry.Version.Length > 0 ? entry.Version : latest;
            picker.IsEnabled = true;
        }
        _ = LoadVersionsAsync();
        await dialog.ShowDialog(window);

        if (dialog.Result is not true || picker.SelectedItem is not string chosen || !_document.CanEdit) return;
        string version = chosen == latest ? "" : chosen;
        int index = catalog.Packages.IndexOf(entry);
        if (version == entry.Version || index < 0) return;
        // Replace the entry rather than mutating it, so the list and the unsaved-changes state update.
        catalog.Packages[index] = new CatalogEntry
        {
            Id = entry.Id,
            Name = entry.Name,
            Version = version,
            Source = entry.Source,
            ManagerName = entry.ManagerName,
        };
    }

    private void RemoveSelection()
    {
        if (!_document.CanEdit || _pickingFile || _document.SelectedCatalog is not { } catalog) return;
        var packages = ViewModel.FilteredPackages.GetCheckedPackages().ToList();
        if (packages.Count == 0 && SelectedItem is { } focused) packages.Add(focused);
        var entries = packages.Select(_loader.FindEntry).OfType<CatalogEntry>().ToArray();
        foreach (var entry in entries) catalog.Packages.Remove(entry);
    }

    public async Task<bool> ConfirmDiscardAsync()
    {
        if (_document.IsBusy || _pickingFile) return false;
        if (!_document.IsDirty) return true;
        var dialog = new ImmersiveConfirmationDialog(CoreTools.Translate("Unsaved changes"),
            new TextBlock { Text = CoreTools.Translate("Discard unsaved catalog changes?"), TextWrapping = TextWrapping.Wrap },
            CoreTools.Translate("Discard changes"), CoreTools.Translate("Cancel"));
        var window = MainWindow.Instance!;
        // Quitting can ask while the window is hidden (macOS, tray); a dialog hosted in a hidden
        // window never completes. Keep it shown afterwards so a cancelled quit can still save.
        if (!window.IsVisible || window.WindowState == WindowState.Minimized)
            window.ShowFromTray();
        await dialog.ShowDialog(window);
        return dialog.Result is true;
    }

    private async Task NewAsync()
    {
        if (_document.IsBusy || _pickingFile) return;
        // Starting a new file closes every catalog, so confirm even when nothing is unsaved.
        if (_document.IsDirty || _document.Catalogs.Count > 0)
        {
            string text = _document.IsDirty
                ? CoreTools.Translate("Discard unsaved catalog changes and start a new catalog file?")
                : CoreTools.Translate("Close the current catalog file and start a new one? Use New catalog to add a catalog to this file instead.");
            var dialog = new ImmersiveConfirmationDialog(CoreTools.Translate("New file"),
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                CoreTools.Translate("New file"), CoreTools.Translate("Cancel"));
            await dialog.ShowDialog(MainWindow.Instance!);
            if (dialog.Result is not true) return;
        }
        _document.NewDocument();
    }

    private async Task OpenAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        _pickingFile = true;
        SyncStatus();
        try
        {
            var files = await MainWindow.Instance!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = CoreTools.Translate("Open catalog file"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
            });
            if (files.Count > 0)
                await _document.LoadAsync(files[0].TryGetLocalPath()
                    ?? throw new IOException(CoreTools.Translate("Select a local catalog file.")));
        }
        catch (Exception ex) { ReportFileError(ex); }
        finally { _pickingFile = false; SyncStatus(); }
    }

    private async Task SaveAsync(bool saveAs)
    {
        if (!_document.CanEdit || _pickingFile) return;
        _pickingFile = true;
        SyncStatus();
        try
        {
            string path = _document.FilePath;
            if (saveAs || string.IsNullOrEmpty(path))
            {
                var file = await MainWindow.Instance!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = CoreTools.Translate("Save catalog as"),
                    SuggestedFileName = string.IsNullOrEmpty(path) ? "SoftwareCatalog.json" : Path.GetFileName(path),
                    DefaultExtension = "json",
                    ShowOverwritePrompt = true,
                    FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
                });
                if (file is null) return;
                path = file.TryGetLocalPath()
                    ?? throw new IOException(CoreTools.Translate("Select a local catalog file."));
            }
            _saveFailed = false;
            if (await _document.SaveAsync(path))
                GetMainWindow()?.ShowBanner(CoreTools.Translate("Catalog saved"), _document.FilePath,
                    MainWindow.RuntimeNotificationLevel.Success);
            else if (!_saveFailed && GetMainWindow()?.DataContext is MainWindowViewModel main)
            {
                // No error was reported, so the administrator prompt was dismissed: say nothing was saved.
                var notice = new InfoBarViewModel
                {
                    Title = CoreTools.Translate("The catalog was not saved."),
                    Severity = InfoBarSeverity.Informational,
                    IsClosable = true,
                    IsOpen = true,
                };
                notice.OnClosed = () => main.DismissToast(notice);
                main.ShowToast(notice);
            }
        }
        catch (Exception ex) { ReportFileError(ex); }
        finally { _pickingFile = false; SyncStatus(); }
    }

    private static async Task ShowErrorAsync(string message)
    {
        if (GetMainWindow() is not { } window) return;
        var text = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        var dialog = new ImmersiveConfirmationDialog(CoreTools.Translate("Catalog Editor"), text,
            CoreTools.Translate("Copy"), CoreTools.Translate("Close"))
        { FocusPrimaryButton = false };
        await dialog.ShowDialog(window);
        if (dialog.Result is true && window.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(message);
    }

    private void ReportFileError(Exception ex)
    {
        Logger.Error("Could not select a catalog file.");
        Logger.Error(ex);
        _document.SetErrorMessage(CoreTools.Translate("Could not select a catalog file: {0}", ex.Message));
    }
}

internal sealed class CatalogPackagesLoader : AbstractPackageLoader
{
    private readonly Dictionary<long, CatalogEntry> _entries = [];

    public CatalogPackagesLoader() : base([], "CATALOG_EDITOR", true, true, false, false) { }

    public CatalogEntry? FindEntry(IPackage package) => _entries.GetValueOrDefault(HashPackage(package));

    public void Replace(IEnumerable<CatalogEntry> entries)
    {
        PackageReference.Clear();
        _entries.Clear();
        foreach (var entry in entries)
        {
            var raw = entry.AsSerializable();
            // The Version column shows an imported package's pinned install version, or "Latest".
            raw.InstallationOptions.Version = entry.Version;
            if (raw.Version.Length == 0) raw.Version = CoreTools.Translate("Latest");
            var manager = PEInterface.Managers.FirstOrDefault(entry.MatchesManager);
            if (manager is not null)
                raw.ManagerName = manager.Name;
            IPackage package = manager is not null && manager.IsReady()
                ? PackageBundlesPage.DeserializePackage(raw)
                : new InvalidImportedPackage(raw.GetInvalidEquivalent(), NullSource.Instance);
            if (package is InvalidImportedPackage)
                package = new InvalidImportedPackage(raw.GetInvalidEquivalent(), new CatalogUnavailableSource(entry, manager),
                    $"{entry.ManagerName}\0{entry.Source}\0{entry.Id}");
            PackageReference[HashPackage(package)] = package;
            _entries[HashPackage(package)] = entry;
        }
        InvokePackagesChangedEvent(false, [], []);
        InvokeFinishedLoadingEvent();
    }

    protected override IReadOnlyList<IPackage> LoadPackagesFromManager(IPackageManager manager) => [];
    protected override Task<bool> IsPackageValid(IPackage package) => Task.FromResult(true);
    protected override Task WhenAddingPackage(IPackage package) => Task.CompletedTask;
}

internal sealed class CatalogUnavailableSource : ManagerSource
{
    public CatalogUnavailableSource(CatalogEntry entry, IPackageManager? manager)
        : base(manager ?? NullSource.Instance.Manager, entry.Source, new Uri("about:blank"), isVirtualManager: true)
    {
        AsString = AsString_DisplayName = $"{entry.ManagerName}: {entry.Source}";
    }
}
