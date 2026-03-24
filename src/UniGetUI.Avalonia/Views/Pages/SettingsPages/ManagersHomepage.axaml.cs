using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.Views.Controls.Settings;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class ManagersHomepage : UserControl, ISettingsPage
{
    public bool CanGoBack => false;
    public string ShortTitle => CoreTools.Translate("Package Managers");

    public event EventHandler? RestartRequired { add { } remove { } }
    public event EventHandler<Type>? NavigationRequested { add { } remove { } }

    public ManagersHomepage()
    {
        DataContext = new ManagersHomepageViewModel();
        InitializeComponent();

        // Build the manager buttons dynamically (manager list is only known at runtime)
        foreach (var manager in PEInterface.Managers)
        {
            var btn = new SettingsPageButton
            {
                Text = manager.DisplayName,
                UnderText = CoreTools.Translate(manager.IsEnabled() ? "Enabled" : "Disabled"),
            };
            // TODO: navigate to PackageManagerPage for this manager (Phase 5)
            ManagersPanel.Children.Add(btn);
        }
    }
}
