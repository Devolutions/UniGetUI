using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Tools;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.Views.Controls.Settings;

public partial class CheckboxCard : SettingsCard
{
    public static readonly StyledProperty<ICommand?> StateChangedCommandProperty =
        AvaloniaProperty.Register<CheckboxCard, ICommand?>(nameof(StateChangedCommand));

    public ICommand? StateChangedCommand
    {
        get => GetValue(StateChangedCommandProperty);
        set => SetValue(StateChangedCommandProperty, value);
    }

    public ToggleSwitch _checkbox;
    public TextBlock _textblock;
    public TextBlock _warningBlock;
    protected bool IS_INVERTED;

    private CoreSettings.K setting_name = CoreSettings.K.Unset;
    public CoreSettings.K SettingName
    {
        set
        {
            _checkbox.IsCheckedChanged -= _checkbox_Toggled;
            setting_name = value;
            IS_INVERTED = CoreSettings.ResolveKey(value).StartsWith("Disable");
            _checkbox.IsChecked = CoreSettings.Get(setting_name) ^ IS_INVERTED ^ ForceInversion;
            _textblock.Opacity = (_checkbox.IsChecked ?? false) ? 1 : 0.7;
            _checkbox.IsCheckedChanged += _checkbox_Toggled;
            SyncToggleItemStatus();
        }
    }

    public bool ForceInversion { get; set; }

    public bool Checked => _checkbox.IsChecked ?? false;

    public virtual event EventHandler<EventArgs>? StateChanged;

    public string Text
    {
        set
        {
            _textblock.Text = value;
            ApplyAutomationMetadata(_checkbox, value, _warningBlock.IsVisible ? _warningBlock.Text : null);
        }
    }

    public string WarningText
    {
        set
        {
            _warningBlock.Text = value;
            _warningBlock.IsVisible = value.Any();
            ApplyAutomationMetadata(_checkbox, _textblock.Text, _warningBlock.IsVisible ? value : null);
        }
    }

    public double WarningOpacity
    {
        set => _warningBlock.Opacity = value;
    }

    public CheckboxCard()
    {
        _checkbox = new ToggleSwitch
        {
            Margin = new Thickness(0, 0, 8, 0),
            OnContent = new TextBlock { Text = CoreTools.Translate("Enabled") },
            OffContent = new TextBlock { Text = CoreTools.Translate("Disabled") },
        };
        // Force CheckBox role so macOS VoiceOver exposes checked/unchecked state
        AutomationProperties.SetControlTypeOverride(_checkbox, AutomationControlType.CheckBox);
        _textblock = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        _warningBlock = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
            IsVisible = false,
        };
        _warningBlock.Classes.Add("setting-warning-text");
        IS_INVERTED = false;
        AutomationProperties.SetAccessibilityView(_warningBlock, AccessibilityView.Raw);

        Content = _checkbox;
        Header = new StackPanel
        {
            Spacing = 4,
            Orientation = Orientation.Vertical,
            Children = { _textblock, _warningBlock },
        };

        _checkbox.IsCheckedChanged += _checkbox_Toggled;
        ApplyAutomationMetadata(_checkbox, _textblock.Text);
    }

    protected virtual void _checkbox_Toggled(object? sender, RoutedEventArgs e)
    {
        CoreSettings.Set(setting_name, (_checkbox.IsChecked ?? false) ^ IS_INVERTED ^ ForceInversion);
        StateChanged?.Invoke(this, EventArgs.Empty);
        _textblock.Opacity = (_checkbox.IsChecked ?? false) ? 1 : 0.7;
        SyncToggleItemStatus();
        AccessibilityAnnouncementService.AnnounceToggle(_textblock.Text, _checkbox.IsChecked ?? false);
        var cmd = StateChangedCommand;
        if (cmd?.CanExecute(null) == true)
            cmd.Execute(null);
    }

    protected void SyncToggleItemStatus()
    {
        string state = (_checkbox.IsChecked ?? false)
            ? CoreTools.Translate("Enabled")
            : CoreTools.Translate("Disabled");
        // ItemStatus: some screen readers read this separately
        AutomationProperties.SetItemStatus(_checkbox, state);
        // Name with state suffix: guarantees VoiceOver announces state on macOS
        // where ToggleSwitch AX role may not expose IsChecked natively
        string baseName = _textblock.Text;
        if (!string.IsNullOrEmpty(baseName))
            AutomationProperties.SetName(_checkbox, $"{baseName}, {state}");
    }
}

public partial class CheckboxCard_Dict : CheckboxCard
{
    public override event EventHandler<EventArgs>? StateChanged;

    private CoreSettings.K _dictName = CoreSettings.K.Unset;
    private bool _disableStateChangedEvent;

    private string _keyName = "";
    public string KeyName
    {
        set
        {
            _keyName = value;
            if (_dictName != CoreSettings.K.Unset && _keyName.Any())
            {
                _disableStateChangedEvent = true;
                _checkbox.IsChecked =
                    CoreSettings.GetDictionaryItem<string, bool>(_dictName, _keyName)
                    ^ IS_INVERTED
                    ^ ForceInversion;
                _textblock.Opacity = (_checkbox.IsChecked ?? false) ? 1 : 0.7;
                _disableStateChangedEvent = false;
                SyncToggleItemStatus();
            }
        }
    }

    public CoreSettings.K DictionaryName
    {
        set
        {
            _dictName = value;
            IS_INVERTED = CoreSettings.ResolveKey(value).StartsWith("Disable");
            if (_dictName != CoreSettings.K.Unset && _keyName.Any())
            {
                _checkbox.IsChecked =
                    CoreSettings.GetDictionaryItem<string, bool>(_dictName, _keyName)
                    ^ IS_INVERTED
                    ^ ForceInversion;
                _textblock.Opacity = (_checkbox.IsChecked ?? false) ? 1 : 0.7;
                SyncToggleItemStatus();
            }
        }
    }

    public CheckboxCard_Dict() : base() { }

    protected override void _checkbox_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_disableStateChangedEvent) return;
        CoreSettings.SetDictionaryItem(
            _dictName,
            _keyName,
            (_checkbox.IsChecked ?? false) ^ IS_INVERTED ^ ForceInversion
        );
        StateChanged?.Invoke(this, EventArgs.Empty);
        _textblock.Opacity = (_checkbox.IsChecked ?? false) ? 1 : 0.7;
        SyncToggleItemStatus();
        AccessibilityAnnouncementService.AnnounceToggle(_textblock.Text, _checkbox.IsChecked ?? false);
    }
}
