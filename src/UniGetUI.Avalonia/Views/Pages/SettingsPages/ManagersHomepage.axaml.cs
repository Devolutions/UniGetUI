using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.Views.Controls.Settings;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class ManagersHomepage : UserControl, ISettingsPage
{
    public bool CanGoBack => false;
    public string ShortTitle => CoreTools.Translate("Package manager preferences");

    public event EventHandler? RestartRequired { add { } remove { } }
    public event EventHandler<Type>? NavigationRequested { add { } remove { } }
    public event EventHandler<IPackageManager>? ManagerNavigationRequested;

    private readonly List<(ToggleSwitch Toggle, IPackageManager Manager, Border Badge,
        Ellipse BadgeIcon, AvaloniaPath BadgeGlyph, TextBlock BadgeText)> _rows = [];
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
            var badgeText = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetAccessibilityView(badgeText, AccessibilityView.Raw);

            var badgeIcon = new Ellipse
            {
                Width = 12,
                Height = 12,
            };
            var badgeGlyph = new AvaloniaPath
            {
                Width = 5,
                Height = 5,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var iconHost = new Grid
            {
                Width = 12,
                Height = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { badgeIcon, badgeGlyph },
            };
            AutomationProperties.SetAccessibilityView(iconHost, AccessibilityView.Raw);

            var badgeContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { iconHost, badgeText },
            };
            var badge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 3, 6, 3),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = badgeContent,
            };
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
                ApplyStatusBadge(manager, toggle, badge, badgeIcon, badgeGlyph, badgeText);
            };
            toggle.IsCheckedChanged += async (_, _) =>
            {
                if (_isLoadingToggles) return;
                CoreSettings.SetDictionaryItem(CoreSettings.K.DisabledManagers, manager.Name, toggle.IsChecked != true);
                await Task.Run(manager.Initialize);
                ApplyStatusBadge(manager, toggle, badge, badgeIcon, badgeGlyph, badgeText);
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
            _rows.Add((toggle, manager, badge, badgeIcon, badgeGlyph, badgeText));
        }
    }

    /// <summary>Re-sync toggle states after returning from a sub-page.</summary>
    public void RefreshToggles()
    {
        _isLoadingToggles = true;
        foreach (var (toggle, manager, badge, badgeIcon, badgeGlyph, badgeText) in _rows)
        {
            toggle.IsChecked = manager.IsEnabled();
            ApplyStatusBadge(manager, toggle, badge, badgeIcon, badgeGlyph, badgeText);
        }
        _isLoadingToggles = false;
    }

    private void ApplyStatusBadge(
        IPackageManager manager,
        ToggleSwitch toggle,
        Border badge,
        Ellipse icon,
        AvaloniaPath glyph,
        TextBlock text)
    {
        string bgKey, fgKey, label;
        glyph.RenderTransform = null;
        if (!manager.IsEnabled())
        {
            bgKey = "WarningBannerBackground";
            fgKey = "StatusWarningForeground";
            label = CoreTools.Translate("Disabled");
            glyph.Data = Geometry.Parse("M2.5,0.4 L2.5,2.1 M2.1,4.5 L2.9,4.5");
        }
        else if (manager.Status.Found)
        {
            bgKey = "StatusSuccessBackground";
            fgKey = "StatusSuccessForeground";
            label = CoreTools.Translate("Ready");
            glyph.Data = Geometry.Parse("M0.5,2.6 L2,4.1 L4.5,0.9");
            glyph.RenderTransform = new TranslateTransform(0, 0.5);
        }
        else
        {
            bgKey = "StatusErrorBackground";
            fgKey = "StatusErrorForeground";
            label = CoreTools.Translate("Not found");
            glyph.Data = Geometry.Parse("M0.75,0.75 L4.25,4.25 M4.25,0.75 L0.75,4.25");
        }
        IBrush background = LookupBrush(bgKey);
        badge.Background = background;
        icon.Fill = LookupBrush(fgKey);
        glyph.Stroke = background;
        text.Foreground = LookupBrush("TextFillColorPrimaryBrush");
        text.Text = label;
        // Bake state into Name so VoiceOver always announces it on macOS
        AutomationProperties.SetName(toggle, $"{manager.DisplayName}, {label}");
        AutomationProperties.SetItemStatus(toggle, label);
    }

    private IBrush LookupBrush(string key)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var res) && res is IBrush brush)
            return brush;
        return Brushes.Transparent;
    }
}
