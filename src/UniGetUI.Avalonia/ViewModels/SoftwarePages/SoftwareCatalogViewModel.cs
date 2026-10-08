using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Models;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.ViewModels.Pages;

public partial class SoftwareCatalogViewModel : ViewModelBase
{
    private readonly Dictionary<string, IReadOnlyList<IManagerSource>> _sources = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<CatalogTileViewModel> Packages { get; } = [];
    public ObservableCollection<CatalogDefinition> Catalogs { get; } = [];

    [ObservableProperty]
    private CatalogDefinition? _selectedCatalog;

    public bool IsEmpty => !IsLoading && !HasError && Packages.Count == 0;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnSelectedCatalogChanged(CatalogDefinition? value)
    {
        Packages.Clear();
        foreach (var entry in value?.Packages ?? [])
        {
            var tile = new CatalogTileViewModel(entry);
            Packages.Add(tile);
            var manager = FindManager(entry.Manager);
            var source = FindSource(entry);
            if (manager is null || source is null) continue;
            var package = InstalledPackagesLoader.Instance?.Packages.FirstOrDefault(entry.Matches)
                ?? new Package(entry.Name, entry.Id, "", source, manager);
            _ = tile.LoadIconAsync(package);
        }
        UpdateStates();
        OnPropertyChanged(nameof(IsEmpty));
    }

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _errorMessage = "";

    public bool HasError => ErrorMessage.Length > 0;

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void Edit()
    {
        try
        {
            if (!File.Exists(SoftwareCatalog.FilePath))
                throw new FileNotFoundException("The software catalog file was not found.", SoftwareCatalog.FilePath);
            using var process = Process.Start(SoftwareCatalog.CreateEditorStartInfo())
                ?? throw new InvalidOperationException("The catalog editor could not be started.");
        }
        catch (Exception ex)
        {
            Logger.Error("Could not open the software catalog editor.");
            Logger.Error(ex);
            ErrorMessage = CoreTools.Translate("The software catalog could not be opened for editing. Check that SoftwareCatalog.json exists and a text editor is available.");
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorMessage = "";
        UpdateStates();
        try
        {
            var catalogs = await SoftwareCatalog.LoadAsync();
            var selectedId = SelectedCatalog?.Id;
            _sources.Clear();
            SelectedCatalog = null;
            Catalogs.Clear();

            foreach (var managerId in catalogs.SelectMany(c => c.Packages).Select(e => e.Manager).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var manager = FindManager(managerId);
                if (manager is null || !manager.IsReady()) continue;
                _sources[managerId] = manager.Capabilities.SupportsCustomSources
                    ? await Task.Run(manager.SourcesHelper.GetSources)
                    : [manager.DefaultSource];
            }

            foreach (var catalog in catalogs)
                Catalogs.Add(catalog);
            SelectedCatalog = Catalogs.FirstOrDefault(c => string.Equals(c.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                ?? Catalogs.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.Error("Could not load the software catalog.");
            Logger.Error(ex);
            SelectedCatalog = null;
            Catalogs.Clear();
            Packages.Clear();
            ErrorMessage = CoreTools.Translate("The software catalog could not be loaded. Ask an administrator to check {0}, then try again.", SoftwareCatalog.FilePath);
        }
        finally
        {
            IsLoading = false;
            UpdateStates();
        }
    }

    private static IPackageManager? FindManager(string id) =>
        PEInterface.Managers.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    private IManagerSource? FindSource(CatalogEntry entry) =>
        _sources.TryGetValue(entry.Manager, out var sources)
            ? sources.FirstOrDefault(s => string.Equals(s.Name, entry.Source, StringComparison.OrdinalIgnoreCase))
            : null;

    public void UpdateStates()
    {
        var loader = InstalledPackagesLoader.Instance;
        var installed = loader?.Packages ?? [];
        foreach (var tile in Packages)
        {
            var manager = FindManager(tile.Entry.Manager);
            var source = FindSource(tile.Entry);
            string? unavailableReason = manager is null
                ? CoreTools.Translate("The package manager {0} is not available on this platform.", tile.Entry.Manager)
                : !manager.IsEnabled()
                    ? CoreTools.Translate("The package manager {0} is disabled. Enable it in Package Managers.", manager.DisplayName)
                    : !manager.IsReady()
                        ? CoreTools.Translate("The package manager {0} is not available or is still initializing.", manager.DisplayName)
                        : source is null
                            ? CoreTools.Translate("The source {0} is not enabled for {1}. Add it in Package Managers.", tile.Entry.Source, manager.DisplayName)
                            : null;
            bool pending = Avalonia.Infrastructure.AvaloniaOperationRegistry.OperationViewModels.Any(o =>
                o.Operation is PackageOperation operation && tile.Entry.Matches(operation.Package)
                && o.Operation.Status is OperationStatus.InQueue or OperationStatus.Running);
            tile.UpdateState(
                installed.Any(tile.Entry.Matches),
                unavailableReason,
                !IsLoading && loader is { HasPendingInitialLoad: false, IsLoading: false }
                    && manager is { LastInstalledListingFailed: false },
                pending);
        }
    }

    [RelayCommand]
    private async Task InstallAsync(CatalogTileViewModel? tile)
    {
        UpdateStates();
        if (tile is null || !tile.CanInstall) return;
        tile.IsBusy = true;
        try
        {
            var manager = FindManager(tile.Entry.Manager)
                ?? throw new InvalidOperationException("The catalog package manager is unavailable.");
            var matches = await Task.Run(() => manager.FindPackages(tile.Entry.Id));
            var package = matches.FirstOrDefault(tile.Entry.Matches)
                ?? throw new InvalidOperationException($"Package {tile.Entry.Id} was not found in {tile.Entry.Manager}/{tile.Entry.Source}.");
            UpdateStates();
            if (tile.IsInstalled || tile.UnavailableReason is not null || !tile.InventoryKnown || tile.IsPending) return;
            await PackagesPageViewModel.LaunchInstall([package]);
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not install catalog package {tile.Entry.Id}.");
            Logger.Error(ex);
            MainWindow.Instance?.ShowBanner(
                CoreTools.Translate("Unable to install {0}", tile.Entry.Name),
                CoreTools.Translate("The catalog package could not be found or installed. Check that its identifier and source are correct, then try again."),
                MainWindow.RuntimeNotificationLevel.Error);
        }
        finally
        {
            tile.IsBusy = false;
            UpdateStates();
        }
    }
}

public partial class CatalogTileViewModel(CatalogEntry entry) : ViewModelBase
{
    public CatalogEntry Entry { get; } = entry;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomIcon))]
    private Bitmap? _iconBitmap;

    public bool HasCustomIcon => IconBitmap is not null;

    internal async Task LoadIconAsync(IPackage package)
    {
        try
        {
            var bitmap = await PackageWrapper.LoadSharedIconAsync(package);
            await Dispatcher.UIThread.InvokeAsync(() => IconBitmap = bitmap);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not load the catalog icon for {Entry.Id}.");
            Logger.Warn(ex);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstall))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleOpacity))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _unavailableReason;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _inventoryKnown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isBusy;

    public bool ShowInstall => !IsInstalled;
    public bool CanInstall => !IsInstalled && UnavailableReason is null && InventoryKnown && !IsPending && !IsBusy;
    public double TitleOpacity => UnavailableReason is null ? 1 : 0.45;
    public string StatusText => UnavailableReason
        ?? (IsBusy ? CoreTools.Translate("Loading packages")
            : IsPending ? CoreTools.Translate("Installing")
            : !InventoryKnown ? CoreTools.Translate("Waiting for installed package information. Refresh Installed Packages if this persists.")
            : "");

    public void UpdateState(bool installed, string? unavailableReason, bool inventoryKnown, bool pending)
    {
        IsInstalled = installed;
        UnavailableReason = unavailableReason;
        InventoryKnown = inventoryKnown;
        IsPending = pending;
    }
}
