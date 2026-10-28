using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Avalonia.Views.Controls.Settings;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

/// <summary>Settings &gt; Extensions: every extension with its switch and status, each opening its own page.</summary>
public sealed partial class ExtensionsHomepage : UserControl, ISettingsPage
{
    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Extensions");

    public event EventHandler? RestartRequired { add { } remove { } }
    public event EventHandler<Type>? NavigationRequested;

    private readonly List<(ToggleSwitch Toggle, AppExtension Extension, StatusBadge Badge)> _rows = [];
    private bool _isLoadingToggles;

    public ExtensionsHomepage()
    {
        InitializeComponent();

        IReadOnlyList<AppExtension> extensions = AppExtensions.All;
        for (int i = 0; i < extensions.Count; i++)
        {
            AppExtension extension = extensions[i];
            bool isFirst = i == 0;
            bool isLast = i == extensions.Count - 1;

            var badge = new StatusBadge { HorizontalAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetAccessibilityView(badge, AccessibilityView.Raw);

            var toggle = new ToggleSwitch
            {
                OnContent = "",
                OffContent = "",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            toggle.IsCheckedChanged += (_, _) =>
            {
                if (_isLoadingToggles) return;
                extension.SetEnabled(toggle.IsChecked == true);
                ApplyStatus(extension, toggle, badge);
                AccessibilityAnnouncementService.AnnounceToggle(CoreTools.Translate(extension.Name), toggle.IsChecked == true);
            };

            var toggleAndBadge = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };
            toggleAndBadge.Children.Add(toggle);
            toggleAndBadge.Children.Add(badge);

            var row = new SettingsPageButton
            {
                Text = CoreTools.Translate(extension.Name),
                UnderText = CoreTools.Translate(extension.Description),
                Icon = extension.Icon,
                CornerRadius = isFirst && isLast ? new CornerRadius(8)
                    : isFirst ? new CornerRadius(8, 8, 0, 0)
                    : isLast ? new CornerRadius(0, 0, 8, 8)
                    : new CornerRadius(0),
                BorderThickness = isFirst ? new Thickness(1) : new Thickness(1, 0, 1, 1),
                Content = toggleAndBadge,
            };
            row.Click += (_, _) => NavigationRequested?.Invoke(this, extension.SettingsPage);

            ExtensionsPanel.Children.Add(row);
            _rows.Add((toggle, extension, badge));
        }
        RefreshRows();
    }

    /// <summary>Re-reads every switch and status, as when coming back from an extension's own page.</summary>
    public void RefreshRows()
    {
        _isLoadingToggles = true;
        foreach (var (toggle, extension, badge) in _rows)
        {
            toggle.IsChecked = extension.IsEnabled;
            ApplyStatus(extension, toggle, badge);
        }
        _isLoadingToggles = false;
    }

    private static void ApplyStatus(AppExtension extension, ToggleSwitch toggle, StatusBadge badge)
    {
        string label;
        if (!extension.IsEnabled)
        {
            badge.Severity = StatusBadgeSeverity.Info;
            label = CoreTools.Translate("Off");
        }
        else
        {
            badge.Severity = StatusBadgeSeverity.Success;
            label = extension.Status() is { Length: > 0 } status ? status : CoreTools.Translate("On");
        }
        badge.Text = label;
        string name = CoreTools.Translate(extension.Name);
        // The state is part of the name so screen readers announce it with the switch.
        AutomationProperties.SetName(toggle, $"{name}, {label}");
        AutomationProperties.SetItemStatus(toggle, label);
    }
}
