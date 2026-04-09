using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.Tools;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;
using CornerRadius = global::Avalonia.CornerRadius;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class Updates : UserControl, ISettingsPage
{
    private UpdatesViewModel VM => (UpdatesViewModel)DataContext!;

    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Package update preferences");

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested;

    public Updates()
    {
        DataContext = new UpdatesViewModel();
        InitializeComponent();

        VM.RestartRequired += (s, e) => RestartRequired?.Invoke(s, e);
        VM.NavigationRequested += (s, t) => NavigationRequested?.Invoke(s, t);

        foreach (var (name, val) in VM.IntervalItems)
            UpdatesCheckIntervalSelector.AddItem(name, val, false);
        UpdatesCheckIntervalSelector.ShowAddedItems();

        foreach (var (name, val) in VM.MinimumAgeItems)
            MinimumUpdateAgeSelector.AddItem(name, val, false);
        MinimumUpdateAgeSelector.ShowAddedItems();

        MinimumUpdateAgeSelector.ValueChanged += (_, _) => RefreshMinimumAgeLayout();
        RefreshMinimumAgeLayout();

        ReleaseDateCompatTableHolder.Content = VM.BuildReleaseDateCompatTable();
    }

    private void RefreshMinimumAgeLayout()
    {
        bool isCustom = CoreSettings.GetValue(CoreSettings.K.MinimumUpdateAge) == "custom";
        VM.IsCustomAgeSelected = isCustom;
        MinimumUpdateAgeSelector.CornerRadius = isCustom
            ? new CornerRadius(8, 8, 0, 0)
            : new CornerRadius(8);
    }
}
