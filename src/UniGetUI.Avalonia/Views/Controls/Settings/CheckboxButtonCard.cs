using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Core.Tools;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.Views.Controls.Settings;

public sealed partial class CheckboxButtonCard : SettingsCard
{
    public ToggleSwitch _checkbox;
    public TextBlock _textblock;
    public Button Button;
    private bool IS_INVERTED;

    private CoreSettings.K setting_name = CoreSettings.K.Unset;
    public CoreSettings.K SettingName
    {
        set
        {
            setting_name = value;
            IS_INVERTED = CoreSettings.ResolveKey(value).StartsWith("Disable");
            _checkbox.IsChecked = CoreSettings.Get(setting_name) ^ IS_INVERTED ^ ForceInversion;
            _textblock.Opacity = (_checkbox.IsChecked ?? false) ? 1 : 0.7;
            Button.IsEnabled = (_checkbox.IsChecked ?? false) || _buttonAlwaysOn;
        }
    }

    public bool ForceInversion { get; set; }
    public bool Checked => _checkbox.IsChecked ?? false;

    public event EventHandler<EventArgs>? StateChanged;
    public new event EventHandler<RoutedEventArgs>? Click;

    public string CheckboxText
    {
        set => _textblock.Text = value;
    }

    public string ButtonText
    {
        set => Button.Content = value;
    }

    private bool _buttonAlwaysOn;
    public bool ButtonAlwaysOn
    {
        set
        {
            _buttonAlwaysOn = value;
            Button.IsEnabled = (_checkbox.IsChecked ?? false) || _buttonAlwaysOn;
        }
    }

    public CheckboxButtonCard()
    {
        Button = new Button { Margin = new Thickness(0, 8, 0, 0) };
        _checkbox = new ToggleSwitch
        {
            Margin = new Thickness(0, 0, 8, 0),
            OnContent = new TextBlock { Text = CoreTools.Translate("Enabled") },
            OffContent = new TextBlock { Text = CoreTools.Translate("Disabled") },
        };
        _textblock = new TextBlock
        {
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.Medium,
        };
        IS_INVERTED = false;

        Content = _checkbox;
        Header = _textblock;
        Description = Button;

        _checkbox.IsCheckedChanged += (_, _) =>
        {
            CoreSettings.Set(setting_name, (_checkbox.IsChecked ?? false) ^ IS_INVERTED ^ ForceInversion);
            StateChanged?.Invoke(this, EventArgs.Empty);
            Button.IsEnabled = (_checkbox.IsChecked ?? false) ? true : _buttonAlwaysOn;
            _textblock.Opacity = (_checkbox.IsChecked ?? false) ? 1 : 0.7;
        };
        Button.Click += (s, e) => Click?.Invoke(s, e);
    }
}
