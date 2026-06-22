using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Avalonia.Extensions;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Controls.Settings;

public partial class SecureCheckboxCard : SettingsCard
{
    public static readonly StyledProperty<ICommand?> StateChangedCommandProperty =
        AvaloniaProperty.Register<SecureCheckboxCard, ICommand?>(nameof(StateChangedCommand));

    public ICommand? StateChangedCommand
    {
        get => GetValue(StateChangedCommandProperty);
        set => SetValue(StateChangedCommandProperty, value);
    }

    public ToggleSwitch _checkbox;
    public TextBlock _textblock;
    public TextBlock _warningBlock;
    public ProgressBar _loading;   // Avalonia has no ProgressRing; use indeterminate ProgressBar
    private readonly TextBlock _stateLabel;
    private static readonly string EnabledLabel = CoreTools.Translate("Enabled");
    private static readonly string DisabledLabel = CoreTools.Translate("Disabled");
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
            UpdateStateLabel();
            _checkbox.IsEnabled = true;
        }
    }

    public bool ForceInversion { get; set; }
    public bool Checked => _checkbox.IsChecked ?? false;

    public virtual event EventHandler<EventArgs>? StateChanged;

    public string Text
    {
        set => _textblock.Text = value;
    }

    public string WarningText
    {
        set
        {
            _warningBlock.Text = FormatTwoLine(value);
            _warningBlock.IsVisible = value.Any();
        }
    }

    // Splits translated warning text at the first sentence boundary so it renders
    // on two readable lines. Handles both Latin (". ") and CJK ("。") separators.
    private static string FormatTwoLine(string text)
    {
        var idx = text.IndexOf(". ", StringComparison.Ordinal);
        if (idx >= 0)
            return text[..(idx + 1)] + "\n" + text[(idx + 2)..];
        idx = text.IndexOf('。');
        if (idx >= 0)
            return text[..(idx + 1)] + "\n" + text[(idx + 1)..];
        return text;
    }

    public SecureCheckboxCard()
    {
        _checkbox = new ToggleSwitch
        {
            // OnContent/OffContent intentionally left null — the state label is
            // a sibling TextBlock placed to the LEFT of the knob below.
            OnContent = null,
            OffContent = null,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _stateLabel = new TextBlock
        {
            Text = DisabledLabel,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
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
            FontSize = 14,
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
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _loading, _stateLabel, _checkbox },
        };
        Header = new StackPanel
        {
            Spacing = 4,
            Orientation = Orientation.Vertical,
            Children = { _textblock, _warningBlock },
        };

        _checkbox.IsCheckedChanged += (s, e) => _ = _checkbox_Toggled();

        this.GetObservable(IsEnabledProperty)
            .SubscribeValue(enabled => _warningBlock.Opacity = enabled ? 1 : 0.2);

        // The Devolutions SettingsCard measures the Header with infinite width, so
        // TextWrapping alone won't constrain the warning block. We fix it by updating
        // MaxWidth after every layout pass, leaving room for the Content (toggle) area.
        SizeChanged += (_, e) =>
        {
            var contentWidth = (Content as Control)?.Bounds.Width ?? 0;
            _warningBlock.MaxWidth = Math.Max(100, e.NewSize.Width - contentWidth - 48);
        };
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
            var cmd = StateChangedCommand;
            if (cmd?.CanExecute(null) == true)
                cmd.Execute(null);
            _textblock.Opacity = (_checkbox.IsChecked ?? false) ? 1 : 0.7;
            _checkbox.IsChecked = SecureSettings.Get(setting_name) ^ IS_INVERTED ^ ForceInversion;
            UpdateStateLabel();
            if (_textblock.Text is not null)
            {
                AccessibilityAnnouncementService.AnnounceToggle(_textblock.Text, _checkbox.IsChecked ?? false);
            }
            _loading.IsVisible = false;
            _checkbox.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex);
            _checkbox.IsChecked = SecureSettings.Get(setting_name) ^ IS_INVERTED ^ ForceInversion;
            UpdateStateLabel();
            _loading.IsVisible = false;
            _checkbox.IsEnabled = true;
        }
    }

    private void UpdateStateLabel()
    {
        _stateLabel.Text = (_checkbox.IsChecked ?? false) ? EnabledLabel : DisabledLabel;
    }
}
