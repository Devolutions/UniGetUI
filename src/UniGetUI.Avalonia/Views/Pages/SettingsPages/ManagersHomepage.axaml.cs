using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
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

    private readonly List<(ToggleSwitch Toggle, IPackageManager Manager, Border Badge, TextBlock BadgeText)> _rows = [];
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

            // ── Status badge ─────────────────────────────────────────────────
            var badgeText = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var badge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 3, 6, 3),
                Child = badgeText,
            };

            // ── Enable/disable toggle ────────────────────────────────────────
            var toggle = new ToggleSwitch
            {
                OnContent = "",
                OffContent = "",
                VerticalAlignment = VerticalAlignment.Center,
            };
            toggle.Loaded += (_, _) =>
            {
                _isLoadingToggles = true;
                toggle.IsChecked = manager.IsEnabled();
                _isLoadingToggles = false;
                ApplyStatusBadge(manager, badge, badgeText);
            };
            toggle.IsCheckedChanged += async (_, _) =>
            {
                if (_isLoadingToggles) return;
                CoreSettings.SetDictionaryItem(CoreSettings.K.DisabledManagers, manager.Name, toggle.IsChecked != true);
                await Task.Run(manager.Initialize);
                ApplyStatusBadge(manager, badge, badgeText);
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
            _rows.Add((toggle, manager, badge, badgeText));
        }
    }

    /// <summary>Re-sync toggle states after returning from a sub-page.</summary>
    public void RefreshToggles()
    {
        _isLoadingToggles = true;
        foreach (var (toggle, manager, badge, badgeText) in _rows)
        {
            toggle.IsChecked = manager.IsEnabled();
            ApplyStatusBadge(manager, badge, badgeText);
        }
        _isLoadingToggles = false;
    }

    private void ApplyStatusBadge(IPackageManager manager, Border badge, TextBlock text)
    {
        string bgKey, fgKey, label;
        if (!manager.IsEnabled())
        {
            bgKey = "WarningBannerBackground";
            fgKey = "StatusWarningForeground";
            label = CoreTools.Translate("Disabled");
        }
        else if (manager.Status.Found)
        {
            bgKey = "StatusSuccessBackground";
            fgKey = "StatusSuccessForeground";
            label = CoreTools.Translate("Ready");
        }
        else
        {
            bgKey = "StatusErrorBackground";
            fgKey = "StatusErrorForeground";
            label = CoreTools.Translate("Not found");
        }
        badge.Background = LookupBrush(bgKey);
        text.Foreground = LookupBrush(fgKey);
        text.Text = label;
    }

    private IBrush LookupBrush(string key)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var res) && res is IBrush brush)
            return brush;
        return Brushes.Transparent;
    }
}
