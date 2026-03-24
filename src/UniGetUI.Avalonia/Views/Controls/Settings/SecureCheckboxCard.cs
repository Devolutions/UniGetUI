using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Controls.Settings;

public partial class SecureCheckboxCard : SettingsCard
{
    public ToggleSwitch _checkbox;
    public TextBlock _textblock;
    public TextBlock _warningBlock;
    public ProgressBar _loading;   // Avalonia has no ProgressRing; use indeterminate ProgressBar
    private bool IS_INVERTED;

    private SecureSettings.K setting_name = SecureSettings.K.Unset;
    public SecureSettings.K SettingName
    {
        set
        {
            _checkbox.IsEnabled = false;
            setting_name = value;
            IS_INVERTED = SecureSettings.ResolveKey(value).StartsWith("Disable");
            _checkbox.IsChecked = SecureSettings.Get(setting_name) ^ IS_INVERTED ^ ForceInversion;
            _textblock.Opacity = (_checkbox.IsChecked ?? false) ? 1 : 0.7;
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
    public bool Checked => _checkbox.IsChecked ?? false;

    public virtual event EventHandler<EventArgs>? StateChanged;

    public string Text
    {
        set => _textblock.Text = CoreTools.Translate(value);
    }

    public string WarningText
    {
        set
        {
            _warningBlock.Text = CoreTools.Translate(value);
            _warningBlock.IsVisible = value.Any();
        }
    }

    public SecureCheckboxCard()
    {
        _checkbox = new ToggleSwitch
        {
            Margin = new Thickness(0, 0, 8, 0),
            OnContent = new TextBlock { Text = CoreTools.Translate("Enabled") },
            OffContent = new TextBlock { Text = CoreTools.Translate("Disabled") },
        };
        _loading = new ProgressBar
        {
            IsIndeterminate = true,
            IsVisible = false,
            Width = 20,
            Height = 20,
            Margin = new Thickness(0, 0, 4, 0),
        };
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
            IsVisible = false,
        };
        _warningBlock.Classes.Add("setting-warning-text");
        IS_INVERTED = false;

        Content = new StackPanel
        {
            Spacing = 4,
            Orientation = Orientation.Horizontal,
            Children = { _loading, _checkbox },
        };
        Header = new StackPanel
        {
            Spacing = 4,
            Orientation = Orientation.Vertical,
            Children = { _textblock, _warningBlock },
        };

        _checkbox.IsCheckedChanged += (s, e) => _ = _checkbox_Toggled();
    }

    protected virtual async Task _checkbox_Toggled()
    {
        try
        {
            if (_checkbox.IsEnabled is false) return;

            _loading.IsVisible = true;
            _checkbox.IsEnabled = false;
            await SecureSettings.TrySet(
                setting_name,
                (_checkbox.IsChecked ?? false) ^ IS_INVERTED ^ ForceInversion
            );
            StateChanged?.Invoke(this, EventArgs.Empty);
            _textblock.Opacity = (_checkbox.IsChecked ?? false) ? 1 : 0.7;
            _checkbox.IsChecked = SecureSettings.Get(setting_name) ^ IS_INVERTED ^ ForceInversion;
            _loading.IsVisible = false;
            _checkbox.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex);
            _checkbox.IsChecked = SecureSettings.Get(setting_name) ^ IS_INVERTED ^ ForceInversion;
            _loading.IsVisible = false;
            _checkbox.IsEnabled = true;
        }
    }
}
