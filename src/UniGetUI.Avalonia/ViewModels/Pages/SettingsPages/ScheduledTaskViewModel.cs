using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Tools;
using UniGetUI.Core.Tools.Scheduling;

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
    private static readonly int[] IntervalValues = [600, 1800, 3600, 7200, 14400, 28800, 43200, 86400, 172800, 259200, 604800];
    private static readonly int[] WindowValues = [0, 30, 60, 120, 240, 480];
    private static readonly int[] MinuteValues = [0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55];

    private static readonly bool Uses12HourClock =
        CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('h', StringComparison.Ordinal);

    private readonly List<ScheduleFrequency> _frequencies;
    private bool _isLoading;

    public MaintenanceTaskKind Kind { get; }

    public string Title { get; }

    public string TaskDescription { get; }

    public string IconPath { get; }

    public IReadOnlyList<string> FrequencyOptions { get; }

    public IReadOnlyList<string> IntervalOptions { get; }

    public IReadOnlyList<string> WindowOptions { get; }

    public IReadOnlyList<string> HourOptions { get; }

    public IReadOnlyList<string> MinuteOptions { get; }

    public IReadOnlyList<string> MeridiemOptions { get; }

    public bool IsMeridiemVisible => Uses12HourClock;

    public ObservableCollection<DayToggleViewModel> Days { get; } = [];

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private int _frequencyIndex;
    [ObservableProperty] private int _intervalIndex;
    [ObservableProperty] private int _windowIndex;
    [ObservableProperty] private int _hourIndex;
    [ObservableProperty] private int _minuteIndex;
    [ObservableProperty] private int _meridiemIndex;
    [ObservableProperty] private bool _runMissed;
    [ObservableProperty] private bool _isIntervalVisible;
    [ObservableProperty] private bool _isTimeVisible;
    [ObservableProperty] private bool _isDaySelectorVisible;
    [ObservableProperty] private string _schedulePrimaryText = "";
    [ObservableProperty] private string _scheduleSecondaryText = "";
    [ObservableProperty] private bool _hasScheduleSecondaryText;
    [ObservableProperty] private double _schedulePrimaryFontSize = 16;
    [ObservableProperty] private bool _hasNoOccurrences;
    [ObservableProperty] private double _headerOpacity = 1;
    [ObservableProperty] private string _statusText = "";

    public event EventHandler? RestartRequired;

    public ScheduledTaskViewModel(MaintenanceTaskKind kind, string title, string description, string iconName)
    {
        Kind = kind;
        Title = title;
        TaskDescription = description;
        IconPath = $"avares://UniGetUI/Assets/Symbols/{iconName}.svg";

        _frequencies = MaintenanceTasks.GetSupportedFrequencies(kind).ToList();
        FrequencyOptions = _frequencies.Select(GetFrequencyLabel).ToList();
        IntervalOptions = IntervalValues.Select(GetIntervalLabel).ToList();
        WindowOptions = WindowValues.Select(GetWindowLabel).ToList();
        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        HourOptions = Uses12HourClock
            ? [.. Enumerable.Range(0, 12).Select(h => (h == 0 ? 12 : h).ToString(CultureInfo.CurrentCulture))]
            : [.. Enumerable.Range(0, 24).Select(h => h.ToString("00", CultureInfo.CurrentCulture))];
        MinuteOptions = [.. MinuteValues.Select(m => m.ToString("00", CultureInfo.CurrentCulture))];
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

    private void Load()
    {
        _isLoading = true;
        try
        {
            var schedule = MaintenanceScheduleStore.Get(Kind);

            IsEnabled = schedule.Enabled;
            FrequencyIndex = Math.Max(0, _frequencies.IndexOf(schedule.Frequency));
            IntervalIndex = GetNearestIndex(IntervalValues, schedule.IntervalSeconds);
            WindowIndex = GetNearestIndex(WindowValues, schedule.WindowMinutes);
            int startMinutes = Math.Clamp(schedule.StartMinutes, 0, MaintenanceTaskSchedule.MinutesPerDay - 1);
            int hour24 = startMinutes / 60;
            HourIndex = Uses12HourClock ? hour24 % 12 : hour24;
            MinuteIndex = GetNearestIndex(MinuteValues, startMinutes % 60);
            MeridiemIndex = hour24 >= 12 ? 1 : 0;
            RunMissed = schedule.RunMissed;

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
        schedule.IntervalSeconds = IntervalValues[Math.Clamp(IntervalIndex, 0, IntervalValues.Length - 1)];
        schedule.WindowMinutes = WindowValues[Math.Clamp(WindowIndex, 0, WindowValues.Length - 1)];
        schedule.StartMinutes = GetStartMinutes();
        schedule.RunMissed = RunMissed;

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
            ScheduleFrequency.Daily => CoreTools.Translate("Every day") + GetWindowSuffix(),
            ScheduleFrequency.Weekly => GetSelectedDaysLabel() + GetWindowSuffix(),
            _ => "",
        };

        SchedulePrimaryFontSize = frequency is ScheduleFrequency.AtAppStart or ScheduleFrequency.AfterEveryUpdateCheck
            ? 13
            : 16;

        HasScheduleSecondaryText = ScheduleSecondaryText.Length > 0;
        HeaderOpacity = IsEnabled ? 1 : 0.7;
        StatusText = BuildStatus();
    }

    private string GetWindowSuffix()
        => WindowValues[Math.Clamp(WindowIndex, 0, WindowValues.Length - 1)] > 0
            ? " · " + CoreTools.Translate("within {0}", WindowOptions[WindowIndex])
            : "";

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

        return hour * 60 + MinuteValues[Math.Clamp(MinuteIndex, 0, MinuteValues.Length - 1)];
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
        600 => CoreTools.Translate("{0} minutes", 10),
        1800 => CoreTools.Translate("{0} minutes", 30),
        3600 => CoreTools.Translate("1 hour"),
        7200 => CoreTools.Translate("{0} hours", 2),
        14400 => CoreTools.Translate("{0} hours", 4),
        28800 => CoreTools.Translate("{0} hours", 8),
        43200 => CoreTools.Translate("{0} hours", 12),
        86400 => CoreTools.Translate("1 day"),
        172800 => CoreTools.Translate("{0} days", 2),
        259200 => CoreTools.Translate("{0} days", 3),
        _ => CoreTools.Translate("1 week"),
    };

    private static string GetWindowLabel(int minutes) => minutes switch
    {
        0 => CoreTools.Translate("Any time after the scheduled time"),
        30 => CoreTools.Translate("{0} minutes", 30),
        60 => CoreTools.Translate("1 hour"),
        120 => CoreTools.Translate("{0} hours", 2),
        240 => CoreTools.Translate("{0} hours", 4),
        _ => CoreTools.Translate("{0} hours", 8),
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

    partial void OnIntervalIndexChanged(int value)
    {
        bool wasLoading = _isLoading;
        Save();

        if (!wasLoading && Kind is MaintenanceTaskKind.CheckForUpdates)
            RestartRequired?.Invoke(this, EventArgs.Empty);
    }

    partial void OnWindowIndexChanged(int value) => Save();

    partial void OnHourIndexChanged(int value) => Save();

    partial void OnMinuteIndexChanged(int value) => Save();

    partial void OnMeridiemIndexChanged(int value) => Save();

    partial void OnRunMissedChanged(bool value) => Save();
}
