using System.Collections.ObjectModel;
using System.Globalization;
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

    private readonly List<ScheduleFrequency> _frequencies;
    private bool _isLoading;

    public MaintenanceTaskKind Kind { get; }

    public string Title { get; }

    public string TaskDescription { get; }

    public IReadOnlyList<string> FrequencyOptions { get; }

    public IReadOnlyList<string> IntervalOptions { get; }

    public IReadOnlyList<string> WindowOptions { get; }

    public IReadOnlyList<string> HourOptions { get; }

    public IReadOnlyList<string> MinuteOptions { get; }

    public ObservableCollection<DayToggleViewModel> Days { get; } = [];

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private int _frequencyIndex;
    [ObservableProperty] private int _intervalIndex;
    [ObservableProperty] private int _windowIndex;
    [ObservableProperty] private int _hourIndex;
    [ObservableProperty] private int _minuteIndex;
    [ObservableProperty] private bool _runMissed;
    [ObservableProperty] private bool _isIntervalVisible;
    [ObservableProperty] private bool _isTimeVisible;
    [ObservableProperty] private bool _isDaySelectorVisible;
    [ObservableProperty] private string _stateLabel = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _statusText = "";

    public event EventHandler? RestartRequired;

    public ScheduledTaskViewModel(MaintenanceTaskKind kind, string title, string description)
    {
        Kind = kind;
        Title = title;
        TaskDescription = description;

        _frequencies = MaintenanceTasks.GetSupportedFrequencies(kind).ToList();
        FrequencyOptions = _frequencies.Select(GetFrequencyLabel).ToList();
        IntervalOptions = IntervalValues.Select(GetIntervalLabel).ToList();
        WindowOptions = WindowValues.Select(GetWindowLabel).ToList();
        HourOptions = Enumerable.Range(0, 24).Select(h => h.ToString("00", CultureInfo.CurrentCulture)).ToList();
        MinuteOptions = MinuteValues.Select(m => m.ToString("00", CultureInfo.CurrentCulture)).ToList();

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
            HourIndex = Math.Clamp(schedule.StartMinutes / 60, 0, 23);
            MinuteIndex = GetNearestIndex(MinuteValues, schedule.StartMinutes % 60);
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
        schedule.StartMinutes = Math.Clamp(HourIndex, 0, 23) * 60
            + MinuteValues[Math.Clamp(MinuteIndex, 0, MinuteValues.Length - 1)];
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

        if (Days.All(d => !d.IsSelected))
        {
            _isLoading = true;
            foreach (var day in Days)
                day.IsSelected = true;
            _isLoading = false;
        }

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
        StateLabel = IsEnabled ? CoreTools.Translate("Enabled") : CoreTools.Translate("Disabled");
        SummaryText = BuildSummary();
        StatusText = BuildStatus();
    }

    private string BuildSummary()
    {
        string time = GetTimeLabel();
        string window = WindowValues[Math.Clamp(WindowIndex, 0, WindowValues.Length - 1)] > 0
            ? " · " + CoreTools.Translate("within {0}", WindowOptions[WindowIndex])
            : "";

        return GetSelectedFrequency() switch
        {
            ScheduleFrequency.AtAppStart => CoreTools.Translate("When UniGetUI starts"),
            ScheduleFrequency.AfterEveryUpdateCheck => CoreTools.Translate("After every update check"),
            ScheduleFrequency.Interval => CoreTools.Translate("Every {0}", IntervalOptions[Math.Clamp(IntervalIndex, 0, IntervalOptions.Count - 1)]),
            ScheduleFrequency.Daily => CoreTools.Translate("Every day at {0}", time) + window,
            ScheduleFrequency.Weekly => CoreTools.Translate("{0} at {1}", GetSelectedDaysLabel(), time) + window,
            _ => "",
        };
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

    private string GetTimeLabel()
    {
        int minutes = Math.Clamp(HourIndex, 0, 23) * 60
            + MinuteValues[Math.Clamp(MinuteIndex, 0, MinuteValues.Length - 1)];
        return DateTime.Today.AddMinutes(minutes).ToString("t", CultureInfo.CurrentCulture);
    }

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

    partial void OnRunMissedChanged(bool value) => Save();
}
