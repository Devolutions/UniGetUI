using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Avalonia.Views.Controls.Settings;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class ManagersHomepage : UserControl, ISettingsPage
{
    public bool CanGoBack => false;
    public string ShortTitle => CoreTools.Translate("Package manager preferences");

    public event EventHandler? RestartRequired { add { } remove { } }
    public event EventHandler<Type>? NavigationRequested { add { } remove { } }
    public event EventHandler<IPackageManager>? ManagerNavigationRequested;

    private readonly List<(ToggleSwitch Toggle, IPackageManager Manager, StatusBadge Badge)> _rows = [];
    private bool _isLoadingToggles;

    public ManagersHomepage()
    {
        DataContext = new ManagersHomepageViewModel();
        InitializeComponent();

        int count = PEInterface.Managers.Length;
        for (int i = 0; i < count; i++)
        {
            var manager = PEInterface.Managers[i];
            bool isFirst = i == 0;
            bool isLast = i == count - 1;

            CornerRadius radius = isFirst && isLast ? new CornerRadius(8)
                                : isFirst ? new CornerRadius(8, 8, 0, 0)
                                : isLast ? new CornerRadius(0, 0, 8, 8)
                                : new CornerRadius(0);
            var thickness = isFirst ? new Thickness(1) : new Thickness(1, 0, 1, 1);

            // ── Status badge (decorative — status surfaced via toggle HelpText) ─
            var badge = new StatusBadge { HorizontalAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetAccessibilityView(badge, AccessibilityView.Raw);

            // ── Enable/disable toggle ────────────────────────────────────────
            var toggle = new ToggleSwitch
            {
                OnContent = "",
                OffContent = "",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(toggle, manager.DisplayName);
            toggle.Loaded += (_, _) =>
            {
                _isLoadingToggles = true;
                toggle.IsChecked = manager.IsEnabled();
                _isLoadingToggles = false;
                ApplyStatusBadge(manager, toggle, badge);
            };
            toggle.IsCheckedChanged += async (_, _) =>
            {
                if (_isLoadingToggles) return;
                CoreSettings.SetDictionaryItem(CoreSettings.K.DisabledManagers, manager.Name, toggle.IsChecked != true);
                await Task.Run(manager.Initialize);
                ApplyStatusBadge(manager, toggle, badge);
                AccessibilityAnnouncementService.AnnounceToggle(manager.DisplayName, toggle.IsChecked == true);
            };

            var toggleAndBadge = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };
            toggleAndBadge.Children.Add(toggle);
            toggleAndBadge.Children.Add(badge);

            var rightContent = toggleAndBadge;

            var btn = new SettingsPageButton
            {
                Text = manager.DisplayName,
                UnderText = manager.Properties.Description.Split("<br>")[0],
                Icon = manager.Properties.IconId,
                CornerRadius = radius,
                BorderThickness = thickness,
                Content = rightContent,
            };

            var capturedManager = manager;
            btn.Click += (_, _) => ManagerNavigationRequested?.Invoke(this, capturedManager);

            ManagersPanel.Children.Add(btn);
            _rows.Add((toggle, manager, badge));
        }
    }

    /// <summary>Re-sync toggle states after returning from a sub-page.</summary>
    public void RefreshToggles()
    {
        _isLoadingToggles = true;
        foreach (var (toggle, manager, badge) in _rows)
        {
            toggle.IsChecked = manager.IsEnabled();
            ApplyStatusBadge(manager, toggle, badge);
        }
        _isLoadingToggles = false;
    }

    private static void ApplyStatusBadge(
        IPackageManager manager,
        ToggleSwitch toggle,
        StatusBadge badge)
    {
        string label;
        if (!manager.IsEnabled())
        {
            badge.Severity = StatusBadgeSeverity.Warning;
            label = CoreTools.Translate("Disabled");
        }
        else if (manager.Status.Found)
        {
            badge.Severity = StatusBadgeSeverity.Success;
            label = CoreTools.Translate("Ready");
        }
        else
        {
            badge.Severity = StatusBadgeSeverity.Error;
            label = CoreTools.Translate("Not found");
        }
        badge.Text = label;
        // Bake state into Name so VoiceOver always announces it on macOS
        AutomationProperties.SetName(toggle, $"{manager.DisplayName}, {label}");
        AutomationProperties.SetItemStatus(toggle, label);
    }

}
