using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Interface.Widgets
{
    public partial class SecureCheckboxCard : SettingsCard
    {
        public ToggleSwitch _checkbox;
        public TextBlock _textblock;
        public TextBlock _warningBlock;
        public ProgressRing _loading;
        private bool IS_INVERTED;

        private SecureSettings.K setting_name = SecureSettings.K.Unset;
        public SecureSettings.K SettingName
        {
            set
            {
                _checkbox.IsEnabled = false;
                setting_name = value;
                IS_INVERTED = SecureSettings.ResolveKey(value).StartsWith("Disable");
                _checkbox.IsOn = SecureSettings.Get(setting_name) ^ IS_INVERTED ^ ForceInversion;
                _textblock.Opacity = _checkbox.IsOn ? 1 : 0.7;
                _checkbox.IsEnabled = true;
            }
        }

        public new bool IsEnabled
        {
            set
            {
                base.IsEnabled = value;
                _warningBlock.Opacity = value ? 1 : 0.2;
            }
            get => base.IsEnabled;
        }

        public bool ForceInversion { get; set; }

        public bool Checked
        {
            get => _checkbox.IsOn;
        }
        public virtual event EventHandler<EventArgs>? StateChanged;

        public string Text
        {
            set
            {
                _textblock.Text = CoreTools.Translate(value);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, _textblock.Text);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetLocalizedControlType(this, "grouping");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_checkbox, _textblock.Text);
            }
        }

        public string WarningText
        {
            set
            {
                _warningBlock.Text = CoreTools.Translate(value);
                _warningBlock.Visibility = value.Any() ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public SecureCheckboxCard()
        {
            _checkbox = new ToggleSwitch()
            {
                Margin = new Thickness(0, 0, 8, 0),
                OnContent = new TextBlock() { Text = CoreTools.Translate("Enabled") },
                OffContent = new TextBlock() { Text = CoreTools.Translate("Disabled") },
            };

            _loading = new ProgressRing()
            {
                IsIndeterminate = true,
                Visibility = Visibility.Collapsed,
            };
            _textblock = new TextBlock()
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            _warningBlock = new TextBlock()
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (SolidColorBrush)
                    Application.Current.Resources["SystemControlErrorTextForegroundBrush"],
                FontSize = 12,
                Visibility = Visibility.Collapsed,
            };
            IS_INVERTED = false;
            Content = new StackPanel()
            {
                Spacing = 4,
                Orientation = Orientation.Horizontal,
                Children = { _loading, _checkbox },
            };
            //Header = _textblock;
            Header = new StackPanel()
            {
                Spacing = 4,
                Orientation = Orientation.Vertical,
                Children = { _textblock, _warningBlock },
            };

            _checkbox.HorizontalAlignment = HorizontalAlignment.Stretch;
            _checkbox.Toggled += (s, e) => _ = _checkbox_Toggled();

            Loaded += (s, e) =>
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, _textblock.Text);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetLocalizedControlType(this, "grouping");
            };
        }

        protected virtual async Task _checkbox_Toggled()
        {
            try
            {
                if (_checkbox.IsEnabled is false)
                    return;

                _loading.Visibility = Visibility.Visible;
                _checkbox.IsEnabled = false;
                await SecureSettings.TrySet(
                    setting_name,
                    _checkbox.IsOn ^ IS_INVERTED ^ ForceInversion
                );
                StateChanged?.Invoke(this, EventArgs.Empty);
                _textblock.Opacity = _checkbox.IsOn ? 1 : 0.7;
                _checkbox.IsOn = SecureSettings.Get(setting_name) ^ IS_INVERTED ^ ForceInversion;
                _loading.Visibility = Visibility.Collapsed;
                _checkbox.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex);
                _checkbox.IsOn = SecureSettings.Get(setting_name) ^ IS_INVERTED ^ ForceInversion;
                _loading.Visibility = Visibility.Collapsed;
                _checkbox.IsEnabled = true;
            }
        }
    }
}
