using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Models;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.ViewModels;

public partial class CatalogEditorCatalog : ViewModelBase
{
    [ObservableProperty]
    private string _id;

    [ObservableProperty]
    private string _name;

    public ObservableCollection<CatalogEntry> Packages { get; }

    public CatalogEditorCatalog(CatalogDefinition definition)
    {
        _id = definition.Id;
        _name = definition.Name;
        Packages = new(definition.Packages);
    }

    public CatalogDefinition ToDefinition() => new() { Id = Id, Name = Name, Packages = Packages.ToArray() };
}

public partial class CatalogEditorViewModel : ViewModelBase
{
    public ObservableCollection<CatalogEditorCatalog> Catalogs { get; } = [];
    public ObservableCollection<IPackageManager> Managers { get; } = [];
    public ObservableCollection<CatalogEntry> SearchResults { get; } = [];

    [ObservableProperty]
    private CatalogEditorCatalog? _selectedCatalog;

    [ObservableProperty]
    private IPackageManager? _selectedManager;

    [ObservableProperty]
    private CatalogEntry? _selectedPackage;

    [ObservableProperty]
    private CatalogEntry? _selectedResult;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private string _filePath = SoftwareCatalog.FilePath;

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isSearching;

    private bool _loading;
    private Task<bool>? _initialLoad;
    private CatalogDefinition[] _savedCatalogs = [];

    public Task<bool> EnsureLoadedAsync() =>
        IsLoaded ? Task.FromResult(true) : _initialLoad ??= LoadAsync(FilePath);

    public void NewDocument()
    {
        SelectedCatalog = null;
        Catalogs.Clear();
        FilePath = "";
        _savedCatalogs = [];
        IsLoaded = true;
        IsDirty = true;
        Message = "";
        NewCatalog();
    }

    public int AddPackages(IEnumerable<IPackage> packages)
    {
        if (!CanEdit || SelectedCatalog is not { } catalog)
            throw new InvalidOperationException(CoreTools.Translate("Open or create a catalog before adding packages."));
        int added = 0;
        foreach (var package in packages)
        {
            if (catalog.Packages.Any(p => p.Matches(package))) continue;
            catalog.Packages.Add(CatalogEntry.FromPackage(package));
            added++;
        }
        Message = CoreTools.Translate("{0} packages added to catalog {1}. Save the catalog to keep your changes.", added, catalog.Name);
        return added;
    }

    public bool CanEdit => IsLoaded && !IsBusy;
    public bool CanDiscardChanges => CanEdit && IsDirty;
    public bool HasSelectedCatalog => SelectedCatalog is not null;
    public bool CanAddPackage => CanEdit && HasSelectedCatalog && SelectedResult is not null;
    public bool CanRemovePackage => CanEdit && HasSelectedCatalog && SelectedPackage is not null;
    public bool CanSearch => CanEdit && !IsSearching && SelectedManager is not null && !string.IsNullOrWhiteSpace(Query);

    partial void OnIsLoadedChanged(bool value) => UpdateAvailability();
    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(CanDiscardChanges));
    partial void OnIsBusyChanged(bool value) => UpdateAvailability();
    partial void OnIsSearchingChanged(bool value) => UpdateAvailability();
    partial void OnQueryChanged(string value) => UpdateAvailability();
    partial void OnSelectedManagerChanged(IPackageManager? value)
    {
        SearchCommand.Cancel();
        SearchResults.Clear();
        SelectedResult = null;
        UpdateAvailability();
    }

    partial void OnSelectedCatalogChanged(CatalogEditorCatalog? value)
    {
        SelectedPackage = null;
        OnPropertyChanged(nameof(HasSelectedCatalog));
        UpdateAvailability();
    }

    partial void OnSelectedResultChanged(CatalogEntry? value) => UpdateAvailability();
    partial void OnSelectedPackageChanged(CatalogEntry? value) => UpdateAvailability();

    private void UpdateAvailability()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanDiscardChanges));
        OnPropertyChanged(nameof(CanSearch));
        OnPropertyChanged(nameof(CanAddPackage));
        OnPropertyChanged(nameof(CanRemovePackage));
        SearchCommand.NotifyCanExecuteChanged();
    }

    public void RefreshManagers()
    {
        var selectedId = SelectedManager?.Id;
        Managers.Clear();
        foreach (var manager in PEInterface.Managers.Where(m => m.IsReady()))
            Managers.Add(manager);
        SelectedManager = Managers.FirstOrDefault(m => m.Id == selectedId) ?? Managers.FirstOrDefault();
        if (Managers.Count == 0 && Message.Length == 0)
            Message = CoreTools.Translate("No package managers are ready. Enable a package manager before searching.");
    }

    private void Attach(CatalogEditorCatalog catalog)
    {
        catalog.PropertyChanged += (_, _) => { if (!_loading) IsDirty = true; };
        catalog.Packages.CollectionChanged += (_, _) => { if (!_loading) IsDirty = true; };
    }

    public async Task<bool> LoadAsync(string path)
    {
        IsBusy = true;
        Message = "";
        try
        {
            var definitions = await SoftwareCatalog.LoadAsync(path);
            SearchCommand.Cancel();
            _loading = true;
            SelectedCatalog = null;
            Catalogs.Clear();
            SearchResults.Clear();
            SelectedResult = null;
            foreach (var definition in definitions)
            {
                var catalog = new CatalogEditorCatalog(definition);
                Attach(catalog);
                Catalogs.Add(catalog);
            }
            SelectedCatalog = Catalogs.FirstOrDefault();
            FilePath = Path.GetFullPath(path);
            _savedCatalogs = CopyCatalogs(definitions);
            IsLoaded = true;
            IsDirty = false;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not open catalog file {path}.");
            Logger.Error(ex);
            Message = CoreTools.Translate("Could not open catalog file {0}: {1}", path, ex.Message);
            return false;
        }
        finally
        {
            _loading = false;
            IsBusy = false;
        }
    }

    public async Task<bool> SaveAsync(string path)
    {
        IsBusy = true;
        Message = "";
        try
        {
            var definitions = CopyCatalogs(Catalogs.Select(c => c.ToDefinition()));
            await SoftwareCatalog.SaveAsync(path, definitions);
            FilePath = Path.GetFullPath(path);
            _savedCatalogs = definitions;
            IsDirty = false;
            Message = CoreTools.Translate("Catalog saved to {0}.", FilePath);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not save catalog file {path}.");
            Logger.Error(ex);
            Message = CoreTools.Translate("Could not save catalog file {0}: {1}", path, ex.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void DiscardChanges()
    {
        if (!CanDiscardChanges) return;
        string? selectedId = SelectedCatalog?.Id;
        SearchCommand.Cancel();
        _loading = true;
        try
        {
            SelectedCatalog = null;
            Catalogs.Clear();
            SearchResults.Clear();
            SelectedResult = null;
            foreach (var definition in CopyCatalogs(_savedCatalogs))
            {
                var catalog = new CatalogEditorCatalog(definition);
                Attach(catalog);
                Catalogs.Add(catalog);
            }
            SelectedCatalog = Catalogs.FirstOrDefault(c =>
                string.Equals(c.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ?? Catalogs.FirstOrDefault();
            IsDirty = false;
            Message = "";
        }
        finally { _loading = false; }
    }

    private static CatalogDefinition[] CopyCatalogs(IEnumerable<CatalogDefinition> catalogs) =>
        catalogs.Select(c => new CatalogDefinition
        {
            Id = c.Id,
            Name = c.Name,
            Packages = c.Packages.Select(p => new CatalogEntry
            {
                Id = p.Id, Name = p.Name, Version = p.Version, Source = p.Source, ManagerName = p.ManagerName,
            }).ToArray(),
        }).ToArray();

    [RelayCommand]
    public void NewCatalog()
    {
        if (!CanEdit) return;
        string id = "catalog";
        int suffix = 1;
        while (Catalogs.Any(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)))
            id = $"catalog-{++suffix}";
        var catalog = new CatalogEditorCatalog(new CatalogDefinition
        {
            Id = id,
            Name = CoreTools.Translate("New catalog"),
            Packages = [],
        });
        Attach(catalog);
        Catalogs.Add(catalog);
        SelectedCatalog = catalog;
        IsDirty = true;
    }

    [RelayCommand]
    private void AddPackage()
    {
        if (!CanEdit || SelectedCatalog is not { } catalog || SelectedResult is not { } entry) return;
        if (catalog.Packages.Any(p => p.Matches(entry.Id, entry.ManagerName, entry.Source)))
        {
            Message = CoreTools.Translate("This package is already in the selected catalog.");
            return;
        }
        catalog.Packages.Add(entry);
        Message = "";
    }

    [RelayCommand]
    private void RemovePackage()
    {
        if (!CanEdit || SelectedCatalog is not { } catalog || SelectedPackage is not { } entry) return;
        catalog.Packages.Remove(entry);
        SelectedPackage = null;
        Message = "";
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        if (SelectedManager is not { } manager) return;
        string query = Query.Trim();
        IsSearching = true;
        SearchResults.Clear();
        SelectedResult = null;
        Message = "";
        try
        {
            if (!manager.IsReady())
                throw new InvalidOperationException(CoreTools.Translate("The selected package manager is not ready."));
            var packages = await Task.Run(() => manager.FindPackages(query), cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            foreach (var package in packages)
            {
                if (SearchResults.Any(p => p.Matches(package))) continue;
                SearchResults.Add(CatalogEntry.FromPackage(package));
            }
            Message = CoreTools.Translate("{0} packages found", SearchResults.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.Error("Catalog package search failed.");
            Logger.Error(ex);
            Message = CoreTools.Translate("Catalog package search failed: {0}", ex.Message);
        }
        finally
        {
            IsSearching = false;
        }
    }
}
