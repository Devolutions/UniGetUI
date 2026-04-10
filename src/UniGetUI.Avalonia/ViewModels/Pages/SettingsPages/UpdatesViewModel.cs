using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;
using CornerRadius = Avalonia.CornerRadius;
using Thickness = Avalonia.Thickness;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class UpdatesViewModel : ViewModelBase
{
    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested;

    [ObservableProperty] private bool _isAutoCheckEnabled;
    [ObservableProperty] private bool _isCustomAgeSelected;

    private static readonly HashSet<string> _managersWithoutUpdateDate =
        new(StringComparer.OrdinalIgnoreCase)
        { "Homebrew", "Scoop", "vcpkg" };

    /// <summary>Items for the minimum update age ComboboxCard, in display/value pairs.</summary>
    public IReadOnlyList<(string Name, string Value)> MinimumAgeItems { get; } =
    [
        (CoreTools.Translate("No minimum age"),    "0"),
        (CoreTools.Translate("1 day"),             "1"),
        (CoreTools.Translate("{0} days", 3),       "3"),
        (CoreTools.Translate("{0} days", 7),       "7"),
        (CoreTools.Translate("{0} days", 14),      "14"),
        (CoreTools.Translate("{0} days", 30),      "30"),
        (CoreTools.Translate("Custom..."),         "custom"),
    ];

    /// <summary>Items for the update interval ComboboxCard, in display/value pairs.</summary>
    public IReadOnlyList<(string Name, string Value)> IntervalItems { get; } =
    [
        (CoreTools.Translate("{0} minutes", 10), "600"),
        (CoreTools.Translate("{0} minutes", 30), "1800"),
        (CoreTools.Translate("1 hour"),           "3600"),
        (CoreTools.Translate("{0} hours", 2),    "7200"),
        (CoreTools.Translate("{0} hours", 4),    "14400"),
        (CoreTools.Translate("{0} hours", 8),    "28800"),
        (CoreTools.Translate("{0} hours", 12),   "43200"),
        (CoreTools.Translate("1 day"),            "86400"),
        (CoreTools.Translate("{0} days", 2),    "172800"),
        (CoreTools.Translate("{0} days", 3),    "259200"),
        (CoreTools.Translate("1 week"),          "604800"),
    ];

    public UpdatesViewModel()
    {
        _isAutoCheckEnabled = !CoreSettings.Get(CoreSettings.K.DisableAutoCheckforUpdates);
        _isCustomAgeSelected = CoreSettings.GetValue(CoreSettings.K.MinimumUpdateAge) == "custom";
    }

    public Control BuildReleaseDateCompatTable()
    {
        string yesStr = CoreTools.Translate("Yes");
        string noStr = CoreTools.Translate("No");

        var managers = PEInterface.Managers.ToList();

        var table = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            ColumnSpacing = 24,
            RowSpacing = 8,
        };
        for (int i = 0; i <= managers.Count; i++)
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var h1 = new TextBlock { Text = CoreTools.Translate("Package manager"), FontWeight = FontWeight.Bold };
        var h2 = new TextBlock { Text = CoreTools.Translate("Supports release dates"), FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetRow(h1, 0); Grid.SetColumn(h1, 0);
        Grid.SetRow(h2, 0); Grid.SetColumn(h2, 1);
        table.Children.Add(h1);
        table.Children.Add(h2);

        for (int i = 0; i < managers.Count; i++)
        {
            var manager = managers[i];
            int row = i + 1;

            var name = new TextBlock { Text = manager.DisplayName, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(name, row); Grid.SetColumn(name, 0);

            bool supported = !_managersWithoutUpdateDate.Contains(manager.Name);
            var badge = _statusBadge(supported ? yesStr : noStr, supported ? Colors.Green : Colors.Red);
            Grid.SetRow(badge, row); Grid.SetColumn(badge, 1);

            table.Children.Add(name);
            table.Children.Add(badge);
        }

        var title = new TextBlock
        {
            Text = CoreTools.Translate("Release date support per package manager"),
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var centerWrapper = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        Grid.SetColumn(table, 1);
        centerWrapper.Children.Add(table);

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(title);
        stack.Children.Add(centerWrapper);

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 12),
            Child = stack,
        };
        border.Classes.Add("settings-card");
        return border;
    }

    private static Border _statusBadge(string text, Color color) => new Border
    {
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(4, 2),
        BorderThickness = new Thickness(1),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Background = new SolidColorBrush(Color.FromArgb(60, color.R, color.G, color.B)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(120, color.R, color.G, color.B)),
        Child = new TextBlock { Text = text, TextAlignment = TextAlignment.Center },
    };

    [RelayCommand]
    private void UpdateAutoCheckEnabled()
    {
        IsAutoCheckEnabled = !CoreSettings.Get(CoreSettings.K.DisableAutoCheckforUpdates);
    }

    [RelayCommand]
    private void ShowRestartRequired() => RestartRequired?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void NavigateToOperations() => NavigationRequested?.Invoke(this, typeof(Operations));

    [RelayCommand]
    private void NavigateToAdministrator() => NavigationRequested?.Invoke(this, typeof(Administrator));
}
