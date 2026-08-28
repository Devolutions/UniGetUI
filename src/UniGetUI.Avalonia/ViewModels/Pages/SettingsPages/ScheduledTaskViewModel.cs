using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Tools;
using UniGetUI.Core.Tools.Scheduling;
using UniGetUI.PackageEngine.Classes.Packages.Classes;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class DayToggleViewModel : ViewModelBase
{
    private readonly Action _onChanged;

    public DayOfWeek Day { get; }

    public string Label { get; }

    [ObservableProperty] private bool _isSelected;

    public DayToggleViewModel(DayOfWeek day, string label, bool isSelected, Action onChanged)
    {
        Day = day;
        Label = label;
        _isSelected = isSelected;
        _onChanged = onChanged;
    }

    partial void OnIsSelectedChanged(bool value) => _onChanged();
}

public partial class ScheduledTaskViewModel : ViewModelBase
{
    private static readonly int[] DefaultIntervalValues = [600, 1800, 3600, 7200, 14400, 28800, 43200, 86400, 172800, 259200, 604800];
    private static readonly int[] DefaultGraceValues = [0, 30, 60, 120, 240, 480];
    private static readonly int[] DefaultMinuteValues = [0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55];

    private static readonly bool Uses12HourClock =
        CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('h', StringComparison.Ordinal);

    private readonly List<ScheduleFrequency> _frequencies;
    private readonly int[] _intervalValues;
    private readonly int[] _minuteValues;
    private readonly int[] _graceValues;
    private bool _isLoading;

    public event EventHandler? ManageAutoUpdatesRequested;

    public MaintenanceTaskKind Kind { get; }

    public string Title { get; }

    private readonly string _baseDescription;

    public string IconPath { get; }

    public IReadOnlyList<string> FrequencyOptions { get; }

    public IReadOnlyList<string> IntervalOptions { get; }

    public IReadOnlyList<string> GraceOptions { get; }

    public IReadOnlyList<string> InstallTargetOptions { get; } =
    [
        CoreTools.Translate("All available updates"),
        CoreTools.Translate("Only packages marked for automatic updates"),
    ];

    public bool IsInstallTargetSelectorVisible { get; }

    public IReadOnlyList<string> HourOptions { get; }

    public IReadOnlyList<string> MinuteOptions { get; }

    public IReadOnlyList<string> MeridiemOptions { get; }

    public bool IsMeridiemVisible => Uses12HourClock;

    public ObservableCollection<DayToggleViewModel> Days { get; } = [];

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private int _frequencyIndex;
    [ObservableProperty] private int _intervalIndex;
    [ObservableProperty] private int _graceIndex;
    [ObservableProperty] private int _hourIndex;
    [ObservableProperty] private int _minuteIndex;
    [ObservableProperty] private int _meridiemIndex;
    [ObservableProperty] private bool _isIntervalVisible;
    [ObservableProperty] private bool _isTimeVisible;
    [ObservableProperty] private bool _isDaySelectorVisible;
    [ObservableProperty] private int _installTargetIndex;
    [ObservableProperty] private bool _isMarkedListVisible;
    [ObservableProperty] private string _markedListSummary = "";
    [ObservableProperty] private string _taskDescription = "";
    [ObservableProperty] private string _schedulePrimaryText = "";
    [ObservableProperty] private string _scheduleSecondaryText = "";
    [ObservableProperty] private bool _hasScheduleSecondaryText;
    [ObservableProperty] private double _schedulePrimaryFontSize = 16;
    [ObservableProperty] private bool _hasNoOccurrences;
    [ObservableProperty] private double _headerOpacity = 1;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _failureText = "";
    [ObservableProperty] private bool _hasFailure;

    public ScheduledTaskViewModel(MaintenanceTaskKind kind, string title, string description, string iconName)
    {
        Kind = kind;
        Title = title;
        _baseDescription = description;
        TaskDescription = description;
        IconPath = $"avares://UniGetUI/Assets/Symbols/{iconName}.svg";
        IsInstallTargetSelectorVisible = kind is MaintenanceTaskKind.InstallUpdates;

        _frequencies = MaintenanceTasks.GetSupportedFrequencies(kind).ToList();
        FrequencyOptions = _frequencies.Select(GetFrequencyLabel).ToList();
        var stored = MaintenanceScheduleStore.Get(kind);
        _intervalValues = WithExtraValue(DefaultIntervalValues, stored.IntervalSeconds);
        _minuteValues = WithExtraValue(DefaultMinuteValues, stored.StartMinutes % 60);

        IntervalOptions = _intervalValues.Select(GetIntervalLabel).ToList();
        _graceValues = BuildGraceValues(stored.GraceMinutes);

        GraceOptions = _graceValues.Select(GetGraceLabel).ToList();
        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        HourOptions = Uses12HourClock
            ? [.. Enumerable.Range(0, 12).Select(h => (h == 0 ? 12 : h).ToString(CultureInfo.CurrentCulture))]
            : [.. Enumerable.Range(0, 24).Select(h => h.ToString("00", CultureInfo.CurrentCulture))];
        MinuteOptions = [.. _minuteValues.Select(m => m.ToString("00", CultureInfo.CurrentCulture))];
        MeridiemOptions = [format.AMDesignator, format.PMDesignator];

        Load();
    }

    public void Refresh()
    {
        Load();
    }

    [RelayCommand]
    private async Task RunNow()
    {
        await MaintenanceScheduler.RunAsync(Kind);
        RefreshLabels();
    }

    [RelayCommand]
    private void ManageAutoUpdates() => ManageAutoUpdatesRequested?.Invoke(this, EventArgs.Empty);

    private void Load()
    {
        _isLoading = true;
        try
        {
            var schedule = MaintenanceScheduleStore.Get(Kind);

            IsEnabled = schedule.Enabled;
            FrequencyIndex = Math.Max(0, _frequencies.IndexOf(schedule.Frequency));
            IntervalIndex = GetNearestIndex(_intervalValues, schedule.IntervalSeconds);
            GraceIndex = GetGraceIndex(schedule.GraceMinutes);
            int startMinutes = Math.Clamp(schedule.StartMinutes, 0, MaintenanceTaskSchedule.MinutesPerDay - 1);
            int hour24 = startMinutes / 60;
            HourIndex = Uses12HourClock ? hour24 % 12 : hour24;
            MinuteIndex = GetNearestIndex(_minuteValues, startMinutes % 60);
            MeridiemIndex = hour24 >= 12 ? 1 : 0;
            InstallTargetIndex = schedule.InstallTargets is ScheduleInstallTargets.MarkedPackagesOnly ? 1 : 0;

            Days.Clear();
            foreach (var day in GetCultureOrderedDays())
                Days.Add(new DayToggleViewModel(day, GetDayLabel(day), schedule.HasDay(day), OnDayToggled));
        }
        finally
        {
            _isLoading = false;
        }

        RefreshVisibility();
        RefreshLabels();
    }

    private void Save()
    {
        if (_isLoading) return;

        var schedule = MaintenanceScheduleStore.Get(Kind);
        schedule.Enabled = IsEnabled;
        schedule.Frequency = _frequencies[Math.Clamp(FrequencyIndex, 0, _frequencies.Count - 1)];
        schedule.IntervalSeconds = _intervalValues[Math.Clamp(IntervalIndex, 0, _intervalValues.Length - 1)];
        schedule.GraceMinutes = _graceValues[Math.Clamp(GraceIndex, 0, _graceValues.Length - 1)];
        schedule.StartMinutes = GetStartMinutes();
        schedule.InstallTargets = InstallTargetIndex is 1
            ? ScheduleInstallTargets.MarkedPackagesOnly
            : ScheduleInstallTargets.AllPackages;

        foreach (var day in Days)
            schedule.SetDay(day.Day, day.IsSelected);

        MaintenanceScheduleStore.Set(Kind, schedule);

        RefreshVisibility();
        RefreshLabels();
    }

    private void OnDayToggled()
    {
        if (_isLoading) return;
        Save();
    }

    private void RefreshVisibility()
    {
        var frequency = GetSelectedFrequency();
        IsIntervalVisible = frequency is ScheduleFrequency.Interval;
        IsTimeVisible = ScheduleEvaluator.IsTimeBased(frequency);
        IsDaySelectorVisible = frequency is ScheduleFrequency.Weekly;
        IsMarkedListVisible = IsInstallTargetSelectorVisible && InstallTargetIndex is 1;
        MarkedListSummary = IsMarkedListVisible ? BuildMarkedListSummary() : "";
        TaskDescription = IsMarkedListVisible
            ? CoreTools.Translate("Update only the packages marked for automatic updates, respecting the battery and metered connection restrictions")
            : _baseDescription;
    }

    private static string BuildMarkedListSummary()
    {
        int count = AutoUpdatesDatabase.Count;
        return count is 0
            ? CoreTools.Translate("No packages are marked yet, so nothing will be installed")
            : CoreTools.Translate("{0} package(s) are marked", count);
    }

    private void RefreshLabels()
    {
        var frequency = GetSelectedFrequency();

        HasNoOccurrences = frequency is ScheduleFrequency.Weekly && Days.All(d => !d.IsSelected);
        if (HasNoOccurrences)
        {
            SchedulePrimaryText = CoreTools.Translate("Never");
            ScheduleSecondaryText = CoreTools.Translate("No days selected");
            HasScheduleSecondaryText = true;
            SchedulePrimaryFontSize = 14;
            HeaderOpacity = IsEnabled ? 1 : 0.7;
            StatusText = BuildStatus();
            RefreshFailure();
            return;
        }

        SchedulePrimaryText = frequency switch
        {
            ScheduleFrequency.AtAppStart => CoreTools.Translate("When UniGetUI starts"),
            ScheduleFrequency.AfterEveryUpdateCheck => CoreTools.Translate("After every update check"),
            ScheduleFrequency.Interval => IntervalOptions[Math.Clamp(IntervalIndex, 0, IntervalOptions.Count - 1)],
            _ => GetTimeLabel(),
        };

        ScheduleSecondaryText = frequency switch
        {
            ScheduleFrequency.Interval => GetFrequencyLabel(ScheduleFrequency.Interval),
            ScheduleFrequency.Daily => CoreTools.Translate("Every day") + GetGraceSuffix(),
            ScheduleFrequency.Weekly => GetSelectedDaysLabel() + GetGraceSuffix(),
            _ => "",
        };

        SchedulePrimaryFontSize = frequency is ScheduleFrequency.AtAppStart or ScheduleFrequency.AfterEveryUpdateCheck
            ? 13
            : 16;

        HasScheduleSecondaryText = ScheduleSecondaryText.Length > 0;
        HeaderOpacity = IsEnabled ? 1 : 0.7;
        StatusText = BuildStatus();
        RefreshFailure();
    }

    private string GetGraceSuffix()
    {
        int grace = _graceValues[Math.Clamp(GraceIndex, 0, _graceValues.Length - 1)];
        return grace > 0
            ? " · " + CoreTools.Translate("within {0}", GetGraceDurationLabel(grace))
            : "";
    }

    private void RefreshFailure()
    {
        var failure = MaintenanceScheduleStore.GetLastFailure(Kind);
        HasFailure = failure is not null;
        FailureText = failure is null
            ? ""
            : CoreTools.Translate(
                "The last attempt failed on {0}, see the log for details",
                failure.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
    }

    private string BuildStatus()
    {
        var schedule = MaintenanceScheduleStore.Get(Kind);
        var lastRun = MaintenanceScheduleStore.GetLastRun(Kind);
        var nextRun = schedule.Enabled
            ? ScheduleEvaluator.GetNextOccurrence(schedule, lastRun, DateTime.Now)
            : null;

        string lastLabel = lastRun is null
            ? CoreTools.Translate("Never")
            : lastRun.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        string nextLabel = nextRun is null
            ? "—"
            : nextRun.Value.ToString("g", CultureInfo.CurrentCulture);

        return CoreTools.Translate("Last run: {0}", lastLabel)
            + " · "
            + CoreTools.Translate("Next run: {0}", nextLabel);
    }

    private ScheduleFrequency GetSelectedFrequency()
        => _frequencies[Math.Clamp(FrequencyIndex, 0, _frequencies.Count - 1)];

    private int GetStartMinutes()
    {
        int hour = Math.Clamp(HourIndex, 0, HourOptions.Count - 1);
        if (Uses12HourClock)
            hour = (hour % 12) + (MeridiemIndex == 1 ? 12 : 0);

        return hour * 60 + _minuteValues[Math.Clamp(MinuteIndex, 0, _minuteValues.Length - 1)];
    }

    private string GetTimeLabel() => GetClockLabel(GetStartMinutes());

    private static string GetClockLabel(int minutesFromMidnight)
        => DateTime.Today.AddMinutes(minutesFromMidnight).ToString("t", CultureInfo.CurrentCulture);

    private string GetSelectedDaysLabel()
    {
        var selected = Days.Where(d => d.IsSelected).Select(d => d.Label).ToList();
        if (selected.Count == Days.Count)
            return CoreTools.Translate("Every day");

        return string.Join(", ", selected);
    }

    private static IEnumerable<DayOfWeek> GetCultureOrderedDays()
    {
        int first = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        for (int offset = 0; offset < 7; offset++)
            yield return (DayOfWeek)((first + offset) % 7);
    }

    private static string GetDayLabel(DayOfWeek day)
        => CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[(int)day];

    private static string GetFrequencyLabel(ScheduleFrequency frequency) => frequency switch
    {
        ScheduleFrequency.AtAppStart => CoreTools.Translate("When UniGetUI starts"),
        ScheduleFrequency.AfterEveryUpdateCheck => CoreTools.Translate("After every update check"),
        ScheduleFrequency.Interval => CoreTools.Translate("On a fixed interval"),
        ScheduleFrequency.Daily => CoreTools.Translate("Every day"),
        ScheduleFrequency.Weekly => CoreTools.Translate("On selected days"),
        _ => "",
    };

    private static string GetIntervalLabel(int seconds) => seconds switch
    {
        3600 => CoreTools.Translate("1 hour"),
        86400 => CoreTools.Translate("1 day"),
        604800 => CoreTools.Translate("1 week"),
        _ when seconds % 86400 == 0 => CoreTools.Translate("{0} days", seconds / 86400),
        _ when seconds % 3600 == 0 => CoreTools.Translate("{0} hours", seconds / 3600),
        _ => CoreTools.Translate("{0} minutes", Math.Max(1, seconds / 60)),
    };

    private int GetGraceIndex(int graceMinutes)
    {
        int index = Array.IndexOf(_graceValues, graceMinutes);
        return index >= 0 ? index : Array.IndexOf(_graceValues, MaintenanceTaskSchedule.UnlimitedGrace);
    }

    private static int[] BuildGraceValues(int graceMinutes)
    {
        int[] bounded = graceMinutes > 0
            ? WithExtraValue(DefaultGraceValues, graceMinutes)
            : DefaultGraceValues;

        return [.. bounded, MaintenanceTaskSchedule.UnlimitedGrace];
    }

    private static int[] WithExtraValue(int[] defaults, int value)
    {
        if (defaults.Contains(value))
            return defaults;

        return [.. defaults.Append(value).Order()];
    }

    private static string GetGraceLabel(int minutes) => minutes switch
    {
        MaintenanceTaskSchedule.UnlimitedGrace => CoreTools.Translate("Run whenever UniGetUI next starts"),
        0 => CoreTools.Translate("Skip until the next occurrence"),
        _ => CoreTools.Translate("Run within {0}", GetGraceDurationLabel(minutes)),
    };

    private static string GetGraceDurationLabel(int minutes) => minutes switch
    {
        60 => CoreTools.Translate("1 hour"),
        _ when minutes % 60 == 0 => CoreTools.Translate("{0} hours", minutes / 60),
        _ => CoreTools.Translate("{0} minutes", minutes),
    };

    private static int GetNearestIndex(int[] values, int value)
    {
        int bestIndex = 0;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < values.Length; i++)
        {
            int distance = Math.Abs(values[i] - value);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    partial void OnIsEnabledChanged(bool value) => Save();

    partial void OnFrequencyIndexChanged(int value) => Save();

    partial void OnIntervalIndexChanged(int value) => Save();

    partial void OnGraceIndexChanged(int value) => Save();

    partial void OnInstallTargetIndexChanged(int value) => Save();

    partial void OnHourIndexChanged(int value) => Save();

    partial void OnMinuteIndexChanged(int value) => Save();

    partial void OnMeridiemIndexChanged(int value) => Save();

}
