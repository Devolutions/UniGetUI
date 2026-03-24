using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class Updates : UserControl, ISettingsPage
{
    private UpdatesViewModel VM => (UpdatesViewModel)DataContext!;

    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Package update preferences");

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested;

    public void ShowRestartBanner(object? sender, EventArgs e) =>
        RestartRequired?.Invoke(this, e);

    public Updates()
    {
        DataContext = new UpdatesViewModel();
        InitializeComponent();

        // Assign setting names and labels
        DisableAutoCheckForUpdates.SettingName = Settings.K.DisableAutoCheckforUpdates;
        DisableAutoCheckForUpdates.Text = "Check for package updates periodically";

        UpdatesCheckIntervalSelector.SettingName = Settings.K.UpdatesCheckInterval;
        UpdatesCheckIntervalSelector.Text = "Check for updates every:";
        UpdatesCheckIntervalSelector.ValueChanged += ShowRestartBanner;

        var updates_dict = new Dictionary<string, string>
        {
            { CoreTools.Translate("{0} minutes", 10), "600"    },
            { CoreTools.Translate("{0} minutes", 30), "1800"   },
            { CoreTools.Translate("1 hour"),           "3600"  },
            { CoreTools.Translate("{0} hours", 2),    "7200"   },
            { CoreTools.Translate("{0} hours", 4),    "14400"  },
            { CoreTools.Translate("{0} hours", 8),    "28800"  },
            { CoreTools.Translate("{0} hours", 12),   "43200"  },
            { CoreTools.Translate("1 day"),            "86400" },
            { CoreTools.Translate("{0} days", 2),    "172800"  },
            { CoreTools.Translate("{0} days", 3),    "259200"  },
            { CoreTools.Translate("1 week"),          "604800" },
        };
        foreach (var (name, val) in updates_dict)
            UpdatesCheckIntervalSelector.AddItem(name, val, false);
        UpdatesCheckIntervalSelector.ShowAddedItems();

        AutomaticallyUpdatePackages.SettingName = Settings.K.AutomaticallyUpdatePackages;
        AutomaticallyUpdatePackages.Text = "Install available updates automatically";

        DisableAUPOnMeteredConnections.SettingName = Settings.K.DisableAUPOnMeteredConnections;
        DisableAUPOnMeteredConnections.ForceInversion = true;
        DisableAUPOnMeteredConnections.Text = "Do not automatically install updates when the network connection is metered";

        DisableAUPOnBattery.SettingName = Settings.K.DisableAUPOnBattery;
        DisableAUPOnBattery.ForceInversion = true;
        DisableAUPOnBattery.Text = "Do not automatically install updates when the device runs on battery";

        DisableAUPOnBatterySaver.SettingName = Settings.K.DisableAUPOnBatterySaver;
        DisableAUPOnBatterySaver.ForceInversion = true;
        DisableAUPOnBatterySaver.Text = "Do not automatically install updates when the battery saver is on";

        // Wire navigation cards
        OperationsSettingsButton.Click += (_, _) => NavigationRequested?.Invoke(this, typeof(Operations));
        AdminButton.Click += (_, _) => NavigationRequested?.Invoke(this, typeof(Administrator));

        // Set initial VM state from the auto-check toggle
        VM.IsAutoCheckEnabled = DisableAutoCheckForUpdates._checkbox.IsChecked ?? false;

        // Keep VM in sync with the toggle
        DisableAutoCheckForUpdates._checkbox.IsCheckedChanged += (_, _) =>
            VM.IsAutoCheckEnabled = DisableAutoCheckForUpdates._checkbox.IsChecked ?? false;
    }
}
