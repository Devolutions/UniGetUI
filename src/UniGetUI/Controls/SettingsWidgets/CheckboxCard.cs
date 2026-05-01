using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Interface.Widgets
{
    public partial class CheckboxCard : SettingsCard
    {
        public ToggleSwitch _checkbox;
        public TextBlock _textblock;
        public TextBlock _warningBlock;
        protected bool IS_INVERTED;

        private Settings.K setting_name = Settings.K.Unset;
        public Settings.K SettingName
        {
            set
            {
                setting_name = value;
                IS_INVERTED = Settings.ResolveKey(value).StartsWith("Disable");
                _checkbox.IsOn = Settings.Get(setting_name) ^ IS_INVERTED ^ ForceInversion;
                _textblock.Opacity = _checkbox.IsOn ? 1 : 0.7;
            }
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

        public Brush WarningForeground
        {
            set => _warningBlock.Foreground = value;
        }

        public double WarningOpacity
        {
            set => _warningBlock.Opacity = value;
        }

        public CheckboxCard()
        {
            _checkbox = new ToggleSwitch()
            {
                Margin = new Thickness(0, 0, 8, 0),
                OnContent = new TextBlock() { Text = CoreTools.Translate("Enabled") },
                OffContent = new TextBlock() { Text = CoreTools.Translate("Disabled") },
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
                FontSize = 12,
                Opacity = 0.7,
                Visibility = Visibility.Collapsed,
            };
            IS_INVERTED = false;
            Content = _checkbox;
            Header = new StackPanel()
            {
                Spacing = 4,
                Orientation = Orientation.Vertical,
                Children = { _textblock, _warningBlock },
            };

            _checkbox.HorizontalAlignment = HorizontalAlignment.Stretch;
            _checkbox.Toggled += _checkbox_Toggled;

            Loaded += (s, e) =>
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, _textblock.Text);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetLocalizedControlType(this, "grouping");
            };
        }

        protected virtual void _checkbox_Toggled(object sender, RoutedEventArgs e)
        {
            Settings.Set(setting_name, _checkbox.IsOn ^ IS_INVERTED ^ ForceInversion);
            StateChanged?.Invoke(this, EventArgs.Empty);
            _textblock.Opacity = _checkbox.IsOn ? 1 : 0.7;
        }
    }

    public partial class CheckboxCard_Dict : CheckboxCard
    {
        public override event EventHandler<EventArgs>? StateChanged;

        private Settings.K _dictName = Settings.K.Unset;
        private bool _disableStateChangedEvent;

        private string _keyName = "";
        public string KeyName
        {
            set
            {
                _keyName = value;
                if (_dictName != Settings.K.Unset && _keyName.Any())
                {
                    _disableStateChangedEvent = true;
                    _checkbox.IsOn =
                        Settings.GetDictionaryItem<string, bool>(_dictName, _keyName)
                        ^ IS_INVERTED
                        ^ ForceInversion;
                    _textblock.Opacity = _checkbox.IsOn ? 1 : 0.7;
                    _disableStateChangedEvent = false;
                }
            }
        }

        public Settings.K DictionaryName
        {
            set
            {
                _dictName = value;
                IS_INVERTED = Settings.ResolveKey(value).StartsWith("Disable");
                if (_dictName != Settings.K.Unset && _keyName.Any())
                {
                    _checkbox.IsOn =
                        Settings.GetDictionaryItem<string, bool>(_dictName, _keyName)
                        ^ IS_INVERTED
                        ^ ForceInversion;
                    _textblock.Opacity = _checkbox.IsOn ? 1 : 0.7;
                }
            }
        }

        public CheckboxCard_Dict()
            : base() { }

        protected override void _checkbox_Toggled(object sender, RoutedEventArgs e)
        {
            if (_disableStateChangedEvent)
                return;
            Settings.SetDictionaryItem(
                _dictName,
                _keyName,
                _checkbox.IsOn ^ IS_INVERTED ^ ForceInversion
            );
            StateChanged?.Invoke(this, EventArgs.Empty);
            _textblock.Opacity = _checkbox.IsOn ? 1 : 0.7;
        }
    }
}
