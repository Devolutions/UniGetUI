using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using Windows.UI;
using Windows.UI.Text;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Pages.SettingsPages.GeneralPages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class Updates : Page, ISettingsPage
    {
        private static readonly HashSet<string> _managersWithoutUpdateDate =
            new(StringComparer.OrdinalIgnoreCase) { "Homebrew", "Scoop", "vcpkg", "WinGet" };

        public Updates()
        {
            this.InitializeComponent();

            Dictionary<string, string> updates_dict = new()
            {
                { CoreTools.Translate("{0} minutes", 10), "600" },
                { CoreTools.Translate("{0} minutes", 30), "1800" },
                { CoreTools.Translate("1 hour"), "3600" },
                { CoreTools.Translate("{0} hours", 2), "7200" },
                { CoreTools.Translate("{0} hours", 4), "14400" },
                { CoreTools.Translate("{0} hours", 8), "28800" },
                { CoreTools.Translate("{0} hours", 12), "43200" },
                { CoreTools.Translate("1 day"), "86400" },
                { CoreTools.Translate("{0} days", 2), "172800" },
                { CoreTools.Translate("{0} days", 3), "259200" },
                { CoreTools.Translate("1 week"), "604800" },
            };

            foreach (KeyValuePair<string, string> entry in updates_dict)
            {
                UpdatesCheckIntervalSelector.AddItem(entry.Key, entry.Value, false);
            }

            UpdatesCheckIntervalSelector.ShowAddedItems();

            // Minimum age for updates
            MinimumUpdateAgeSelector.Description = CoreTools.Translate(
                "Only show updates that are at least the specified number of days old");

            Dictionary<string, string> minimum_age_dict = new()
            {
                { CoreTools.Translate("No minimum age"), "0" },
                { CoreTools.Translate("1 day"), "1" },
                { CoreTools.Translate("{0} days", 3), "3" },
                { CoreTools.Translate("{0} days", 7), "7" },
                { CoreTools.Translate("{0} days", 14), "14" },
                { CoreTools.Translate("{0} days", 30), "30" },
                { CoreTools.Translate("Custom..."), "custom" },
            };

            foreach (KeyValuePair<string, string> entry in minimum_age_dict)
                MinimumUpdateAgeSelector.AddItem(entry.Key, entry.Value, false);

            MinimumUpdateAgeSelector.ShowAddedItems();
            MinimumUpdateAgeSelector.ValueChanged += (_, _) => RefreshMinimumAgeLayout();
            RefreshMinimumAgeLayout();

            MinimumUpdateAgeCustomInput.PlaceholderText = CoreTools.Translate("e.g. 10");
            MinimumUpdateAgeCustomInput.Text = Settings.GetValue(Settings.K.MinimumUpdateAgeCustom);
            MinimumUpdateAgeCustomInput.TextChanged += (_, _) =>
            {
                string current = MinimumUpdateAgeCustomInput.Text ?? "";
                string filtered = new string(current.Where(char.IsDigit).ToArray());
                if (filtered != current)
                {
                    MinimumUpdateAgeCustomInput.Text = filtered;
                    return;
                }
                if (filtered.Length > 0)
                    Settings.SetValue(Settings.K.MinimumUpdateAgeCustom, filtered);
                else
                    Settings.Set(Settings.K.MinimumUpdateAgeCustom, false);
            };

            ReleaseDateCompatTableHolder.Content = BuildReleaseDateCompatTable();
        }

        private void RefreshMinimumAgeLayout()
        {
            bool isCustom = Settings.GetValue(Settings.K.MinimumUpdateAge) == "custom";
            MinimumUpdateAgeCustomCard.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            MinimumUpdateAgeSelector.CornerRadius = isCustom
                ? new CornerRadius(8, 8, 0, 0)
                : new CornerRadius(8);
        }

        private UIElement BuildReleaseDateCompatTable()
        {
            string yesStr = CoreTools.Translate("Yes");
            string noStr = CoreTools.Translate("No");

            var managers = PEInterface.Managers.ToList();

            var table = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int i = 0; i <= managers.Count; i++)
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var h1 = new TextBlock
            {
                Text = CoreTools.Translate("Package manager"),
                FontWeight = new FontWeight(600),
            };
            var h2 = new TextBlock
            {
                Text = CoreTools.Translate("Supports release dates"),
                FontWeight = new FontWeight(600),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
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
                var badge = MakeStatusBadge(supported ? yesStr : noStr, supported);
                Grid.SetRow(badge, row); Grid.SetColumn(badge, 1);
                table.Children.Add(name);
                table.Children.Add(badge);
            }

            var centerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 0),
            };
            centerPanel.Children.Add(table);

            var card = new SettingsCard
            {
                CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            card.Header = CoreTools.Translate("Release date support per package manager");
            card.Description = centerPanel;
            return card;
        }

        private static Border MakeStatusBadge(string text, bool isSupported)
        {
            var bgColor = isSupported
                ? Color.FromArgb(60, 0, 180, 0)
                : Color.FromArgb(60, 224, 82, 82);
            var borderColor = isSupported
                ? Color.FromArgb(120, 0, 180, 0)
                : Color.FromArgb(120, 224, 82, 82);

            return new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 2, 4, 2),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(bgColor),
                BorderBrush = new SolidColorBrush(borderColor),
                Child = new TextBlock { Text = text, TextAlignment = TextAlignment.Center },
            };
        }

        public bool CanGoBack => true;
        public string ShortTitle => CoreTools.Translate("Package update preferences");

        public event EventHandler? RestartRequired;
        public event EventHandler<Type>? NavigationRequested;

        public void ShowRestartBanner(object sender, EventArgs e) =>
            RestartRequired?.Invoke(this, e);

        private void OperationsSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            NavigationRequested?.Invoke(this, typeof(Operations));
        }

        private void AdminButton_Click(object sender, RoutedEventArgs e)
        {
            NavigationRequested?.Invoke(this, typeof(Administrator));
        }
    }
}
