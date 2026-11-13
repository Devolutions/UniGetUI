using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.Drivers;

namespace UniGetUI.Avalonia.ViewModels.Pages;

public enum DriverItemState
{
    Available,
    Installing,
    Installed,
    RestartRequired,
    Failed,
    Unknown,
}

public partial class DriverUpdateItemViewModel : ObservableObject
{
    public DriverUpdateItemViewModel(DriverUpdate update) => Update = update;

    public DriverUpdate Update { get; }
    public string Id => Update.Id;
    public string Title => Update.Title;

    public string DeviceText => FirstNonEmpty(Update.DeviceName, Update.Model, Update.HardwareId);

    public string DetailsText => string.Join(" · ", new[] { Update.DriverClass, Update.Provider }.Where(part => part.Length > 0));

    public string InstalledText => Update.InstalledVersion is { Length: > 0 } version
        ? Update.InstalledDate is { Length: > 0 } date
            ? CoreTools.Translate("Installed: {0} ({1})", version, date)
            : CoreTools.Translate("Installed: {0}", version)
        : CoreTools.Translate("Installed version unknown");

    public string AvailableText => Update.DriverDate is { Length: > 0 } date
        ? CoreTools.Translate("New driver dated {0}", date)
        : CoreTools.Translate("New driver");

    public string SizeText => Update.Size > 0 ? CoreTools.FormatAsSize(Update.Size) : "";

    public bool MayRequireRestart => Update.RebootBehavior is not DriverRebootBehavior.NeverReboots;

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(IsInstalling))]
    [NotifyPropertyChangedFor(nameof(ShowStateBadge))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    private DriverItemState _state;

    [ObservableProperty]
    private string _stateText = "";

    public bool CanInstall => State is DriverItemState.Available or DriverItemState.Failed or DriverItemState.Unknown;
    public bool IsInstalling => State is DriverItemState.Installing;
    public bool ShowStateBadge => State is not DriverItemState.Available;
    public bool IsDone => State is DriverItemState.Installed or DriverItemState.RestartRequired;
    public bool IsFailed => State is DriverItemState.Failed;

    internal void Apply(DriverInstallResult result)
    {
        (State, StateText) = result.ResultCode switch
        {
            _ when result.Succeeded && result.RebootRequired => (DriverItemState.RestartRequired, CoreTools.Translate("Restart required")),
            _ when result.Succeeded => (DriverItemState.Installed, CoreTools.Translate("Installed")),
            DriverInstallResultCode.RequiresLicense => (DriverItemState.Failed, CoreTools.Translate("Its license must be accepted in Windows Update")),
            DriverInstallResultCode.NotFound => (DriverItemState.Failed, CoreTools.Translate("No longer offered by Windows Update")),
            DriverInstallResultCode.Aborted => (DriverItemState.Failed, CoreTools.Translate("Cancelled")),
            DriverInstallResultCode.DownloadFailed => (DriverItemState.Failed, CoreTools.Translate("Download failed ({0})", HResultText(result.HResult))),
            _ => (DriverItemState.Failed, CoreTools.Translate("Installation failed ({0})", HResultText(result.HResult))),
        };
        if (!CanInstall) IsSelected = false;
    }

    internal void MarkUnknown()
    {
        State = DriverItemState.Unknown;
        StateText = CoreTools.Translate("Result unknown");
    }

    internal void MarkNoLongerOffered()
    {
        State = DriverItemState.Installed;
        StateText = CoreTools.Translate("No longer offered by Windows Update");
        IsSelected = false;
    }

    internal void Reset()
    {
        State = DriverItemState.Available;
        StateText = "";
    }

    internal static string HResultText(int hResult) => $"0x{hResult:X8}";

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}

public partial class DriversViewModel : ViewModelBase
{
    internal static readonly TimeSpan BackgroundCheckInterval = TimeSpan.FromHours(12);
    internal static readonly TimeSpan FailedCheckRetryInterval = TimeSpan.FromHours(1);

    private readonly DriverUpdateService _service;
    private readonly DriverScanCache? _cache;
    private readonly Func<DateTimeOffset> _now;
    private readonly CancellationTokenSource _lifetime = new();
    private DateTimeOffset? _lastScan;
    private DateTimeOffset? _lastFailure;
    private bool _stopped;

    public DriversViewModel()
        : this(
            new DriverUpdateService(new PowerShellDriverScriptRunner(CurrentElevation)),
            new DriverScanCache(Path.Combine(CoreData.UniGetUICacheDirectory_Data, "drivers.json")))
    {
    }

    internal DriversViewModel(DriverUpdateService service, DriverScanCache? cache = null, Func<DateTimeOffset>? now = null)
    {
        _service = service;
        _cache = cache;
        _now = now ?? (() => DateTimeOffset.Now);
        Updates.CollectionChanged += (_, _) => RefreshCounts();
    }

    private static DriversElevation CurrentElevation() => new(
        CoreTools.IsAdministrator(),
        Settings.Get(Settings.K.ProhibitElevation),
        CoreData.ElevatorPath,
        CoreData.ElevatorArgumentVector,
        CoreTools.PrepareForegroundForElevation);

    public ObservableCollection<DriverUpdateItemViewModel> Updates { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowScanningPlaceholder))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand))]
    private bool _isInstalling;

    public bool IsBusy => IsScanning || IsInstalling;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _hasScanned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private string _errorText = "";

    public bool HasError => ErrorText.Length > 0;

    public InfoBarViewModel ErrorNotice { get; } = new() { Severity = InfoBarSeverity.Error };

    public InfoBarViewModel RestartNotice { get; } = new() { Severity = InfoBarSeverity.Warning };

    partial void OnErrorTextChanged(string value)
    {
        ErrorNotice.Title = CoreTools.Translate("Windows Update");
        ErrorNotice.Message = value;
        ErrorNotice.IsOpen = value.Length > 0;
    }

    [ObservableProperty]
    private bool _restartRequired;

    partial void OnRestartRequiredChanged(bool value)
    {
        RestartNotice.Title = CoreTools.Translate("Restart required");
        RestartNotice.Message = CoreTools.Translate("Restart your computer to finish installing the drivers.");
        RestartNotice.IsOpen = value;
    }

    [ObservableProperty]
    private int _updateCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand))]
    private int _selectedCount;

    [ObservableProperty]
    private string _lastCheckedText = "";

    public bool ShowEmptyState => HasScanned && !IsScanning && !HasError && Updates.Count == 0;

    public bool HasUpdates => Updates.Count > 0;

    public bool ShowScanningPlaceholder => IsScanning && Updates.Count == 0;

    public async Task CheckForUpdatesAsync()
    {
        if (IsBusy || _stopped) return;
        if (_lastScan is null && _cache?.Load() is { } snapshot)
        {
            ApplyScan(snapshot.Updates, snapshot.CheckedAt);
            HasScanned = true;
        }
        if (IsRecent(_lastScan, BackgroundCheckInterval) || IsRecent(_lastFailure, FailedCheckRetryInterval)) return;
        await ScanAsync();
    }

    private bool IsRecent(DateTimeOffset? moment, TimeSpan window)
    {
        if (moment is not { } value) return false;
        TimeSpan age = _now() - value;
        return age >= TimeSpan.Zero && age < window;
    }

    public void Stop()
    {
        _stopped = true;
        _lifetime.Cancel();
    }

    private bool CanScan() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (IsBusy || _stopped) return;
        IsScanning = true;
        ErrorText = "";
        CancellationToken token = _lifetime.Token;
        try
        {
            IReadOnlyList<DriverUpdate> updates = await _service.ScanAsync(token);
            if (token.IsCancellationRequested) return;
            DateTimeOffset checkedAt = _now();
            ApplyScan(updates, checkedAt);
            _lastFailure = null;
            _cache?.Save(new DriverScanSnapshot(updates, checkedAt));
            Logger.Info($"Windows Update offers {updates.Count} driver update(s)");
        }
        catch (OperationCanceledException)
        {
        }
        catch (DriversException ex)
        {
            _lastFailure = _now();
            ErrorText = Describe(ex);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            _lastFailure = _now();
            ErrorText = CoreTools.Translate("Windows Update failed: {0}", ex.Message);
        }
        finally
        {
            HasScanned = true;
            IsScanning = false;
            RefreshCounts();
        }
    }

    private bool CanInstallSelected() => !IsBusy && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanInstallSelected))]
    private Task InstallSelectedAsync()
        => InstallAsync([.. Updates.Where(item => item.IsSelected && item.CanInstall)]);

    [RelayCommand]
    private Task InstallOneAsync(DriverUpdateItemViewModel? item)
        => item is { CanInstall: true } && !IsBusy ? InstallAsync([item]) : Task.CompletedTask;

    [RelayCommand]
    private void SelectAll()
    {
        bool select = Updates.Any(item => item.CanInstall && !item.IsSelected);
        foreach (var item in Updates.Where(item => item.CanInstall)) item.IsSelected = select;
    }

    [RelayCommand]
    private static void OpenWindowsUpdate() => CoreTools.Launch("ms-settings:windowsupdate-optionalupdates");

    private async Task InstallAsync(IReadOnlyList<DriverUpdateItemViewModel> items)
    {
        if (items.Count == 0 || IsBusy || _stopped) return;
        IsInstalling = true;
        ErrorText = "";
        foreach (var item in items)
        {
            item.State = DriverItemState.Installing;
            item.StateText = CoreTools.Translate("Installing...");
        }

        bool outcomeUnknown = false;
        string error = "";
        try
        {
            DriverInstallReport report = await _service.InstallAsync([.. items.Select(item => item.Id)]);
            Dictionary<string, DriverInstallResult> results = report.Results
                .GroupBy(result => result.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                item.Apply(results.TryGetValue(item.Id, out var result)
                    ? result
                    : new DriverInstallResult { Id = item.Id, ResultCode = DriverInstallResultCode.Failed });
            }
            if (report.RebootRequired) RestartRequired = true;
            SaveCache();
            Logger.Info($"Driver installation finished: {items.Count(item => item.IsDone)} of {items.Count} installed");
        }
        catch (DriversException ex) when (NothingWasInstalled(ex.Kind))
        {
            foreach (var item in items) item.Reset();
            error = Describe(ex);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            foreach (var item in items) item.MarkUnknown();
            outcomeUnknown = true;
            error = ex is DriversException driversException
                ? Describe(driversException)
                : CoreTools.Translate("Windows Update failed: {0}", ex.Message);
        }
        finally
        {
            IsInstalling = false;
            RefreshCounts();
        }

        if (_stopped)
        {
            Logger.Info("The Drivers extension was turned off while drivers were installing");
            return;
        }

        if (outcomeUnknown)
        {
            await ScanAsync();
            if (!HasError)
                ErrorText = CoreTools.Translate("{0} UniGetUI checked Windows Update again: drivers that are no longer offered were installed.", error);
            return;
        }

        ErrorText = error;
    }

    private static bool NothingWasInstalled(DriversErrorKind kind) => kind is DriversErrorKind.ElevationCancelled
        or DriversErrorKind.ElevationProhibited
        or DriversErrorKind.NoElevator
        or DriversErrorKind.NotSupported;

    private void ApplyScan(IReadOnlyList<DriverUpdate> updates, DateTimeOffset checkedAt)
    {
        Dictionary<string, DriverUpdateItemViewModel> previous = Updates
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        HashSet<string> offered = new(updates.Select(update => update.Id), StringComparer.OrdinalIgnoreCase);

        List<DriverUpdateItemViewModel> items = [];
        foreach (DriverUpdate update in updates)
        {
            if (previous.TryGetValue(update.Id, out var old) && old.State is not (DriverItemState.Available or DriverItemState.Unknown))
                items.Add(old);
            else
                items.Add(new DriverUpdateItemViewModel(update) { IsSelected = old?.IsSelected ?? true });
        }
        foreach (var old in previous.Values.Where(item => !offered.Contains(item.Id)))
        {
            if (old.State is DriverItemState.Unknown) old.MarkNoLongerOffered();
            if (old.IsDone) items.Add(old);
        }

        foreach (var item in Updates) item.PropertyChanged -= Item_PropertyChanged;
        Updates.Clear();
        foreach (var item in items.OrderBy(item => item.Update.DriverClass).ThenBy(item => item.Title))
        {
            item.PropertyChanged += Item_PropertyChanged;
            Updates.Add(item);
        }

        _lastScan = checkedAt;
        LastCheckedText = CoreTools.Translate("Last checked: {0}", checkedAt.LocalDateTime.ToString("g"));
    }

    private void SaveCache()
    {
        if (_cache is null || _lastScan is not { } checkedAt) return;
        _cache.Save(new DriverScanSnapshot([.. Updates.Where(item => !item.IsDone).Select(item => item.Update)], checkedAt));
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DriverUpdateItemViewModel.IsSelected) or nameof(DriverUpdateItemViewModel.State))
            RefreshCounts();
    }

    private void RefreshCounts()
    {
        UpdateCount = Updates.Count(item => item.CanInstall);
        SelectedCount = Updates.Count(item => item.IsSelected && item.CanInstall);
        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowScanningPlaceholder));
    }

    internal static string Describe(DriversException ex) => ex.Kind switch
    {
        DriversErrorKind.Offline => CoreTools.Translate("Windows Update could not be reached. Check your internet connection and try again."),
        DriversErrorKind.DisabledByPolicy => CoreTools.Translate("Windows Update is managed by your organization, and does not let this computer look for drivers."),
        DriversErrorKind.ServiceUnavailable => CoreTools.Translate("The Windows Update service is not running."),
        DriversErrorKind.AccessDenied => CoreTools.Translate("Windows Update refused the request because administrator rights are missing."),
        DriversErrorKind.ElevationProhibited => CoreTools.Translate("Installing drivers requires administrator rights, which are prohibited in UniGetUI's settings."),
        DriversErrorKind.ElevationCancelled => CoreTools.Translate("The administrator prompt was cancelled, so no driver was installed."),
        DriversErrorKind.NoElevator => CoreTools.Translate("UniGetUI could not find its elevator to ask for administrator rights."),
        DriversErrorKind.NotSupported => CoreTools.Translate("Driver updates are only available on Windows."),
        DriversErrorKind.UnreadableOutput => CoreTools.Translate("Windows Update did not return a result UniGetUI could read. The UniGetUI log has the details."),
        _ when ex.ErrorCode != 0 => CoreTools.Translate("Windows Update failed: {0}", $"{ex.Message} ({DriverUpdateItemViewModel.HResultText(ex.ErrorCode)})"),
        _ => CoreTools.Translate("Windows Update failed: {0}", ex.Message),
    };
}
