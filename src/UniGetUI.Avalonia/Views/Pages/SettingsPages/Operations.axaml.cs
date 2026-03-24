using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class Operations : UserControl, ISettingsPage
{
    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Package operation preferences");

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested;

    public void ShowRestartBanner(object? sender, EventArgs e) =>
        RestartRequired?.Invoke(this, e);

    public Operations()
    {
        DataContext = new OperationsViewModel();
        InitializeComponent();

        ParallelOperationCount.SettingName = Settings.K.ParallelOperationCount;
        ParallelOperationCount.Text = "Choose how many operations should be performed in parallel";
        for (int i = 1; i <= 10; i++) ParallelOperationCount.AddItem(i.ToString(), i.ToString(), false);
        foreach (var v in new[] { "15", "20", "30", "50", "75", "100" })
            ParallelOperationCount.AddItem(v, v, false);
        ParallelOperationCount.ShowAddedItems();
        ParallelOperationCount.ValueChanged += ParallelOperationCount_OnValueChanged;

        MaintainSuccessfulInstalls.SettingName = Settings.K.MaintainSuccessfulInstalls;
        MaintainSuccessfulInstalls.ForceInversion = true;
        MaintainSuccessfulInstalls.WarningText = "Download operations are not affected by this setting";
        MaintainSuccessfulInstalls.Text = "Clear successful operations from the operation list after a 5 second delay";

        KillProcessesThatRefuseToDie.SettingName = Settings.K.KillProcessesThatRefuseToDie;
        KillProcessesThatRefuseToDie.Text = "Try to kill the processes that refuse to close when requested to";
        KillProcessesThatRefuseToDie.WarningOpacity = 0.7;
        KillProcessesThatRefuseToDie.WarningText = "You may lose unsaved data";

        AskToDeleteNewDesktopShortcuts.SettingName = Settings.K.AskToDeleteNewDesktopShortcuts;
        AskToDeleteNewDesktopShortcuts.CheckboxText = "Ask to delete desktop shortcuts created during an install or upgrade.";
        AskToDeleteNewDesktopShortcuts.ButtonText = "Manage shortcuts";
        AskToDeleteNewDesktopShortcuts.Click += (_, _) =>
        {
            // DialogHelper.ManageDesktopShortcuts() — not yet ported; no-op on macOS
        };

        UpdatesSettingsButton.Click += (_, _) => NavigationRequested?.Invoke(this, typeof(Updates));
        AdminButton.Click += (_, _) => NavigationRequested?.Invoke(this, typeof(Administrator));
    }

    private void ParallelOperationCount_OnValueChanged(object? sender, EventArgs e)
    {
        if (sender is UniGetUI.Avalonia.Views.Controls.Settings.ComboboxCard card &&
            int.TryParse(card.SelectedValue(), out int value))
        {
            AbstractOperation.MAX_OPERATIONS = value;
        }
    }
}
