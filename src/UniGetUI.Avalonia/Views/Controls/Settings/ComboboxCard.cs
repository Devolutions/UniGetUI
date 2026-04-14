using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.Views.Controls.Settings;

public sealed partial class ComboboxCard : SettingsCard
{
    public static readonly StyledProperty<ICommand?> ValueChangedCommandProperty =
        AvaloniaProperty.Register<ComboboxCard, ICommand?>(nameof(ValueChangedCommand));

    public ICommand? ValueChangedCommand
    {
        get => GetValue(ValueChangedCommandProperty);
        set => SetValue(ValueChangedCommandProperty, value);
    }

    private readonly ComboBox _combobox = new();
    private readonly ObservableCollection<string> _elements = [];
    private readonly Dictionary<string, string> _values_ref = [];
    private readonly Dictionary<string, string> _inverted_val_ref = [];

    private CoreSettings.K settings_name = CoreSettings.K.Unset;
    public CoreSettings.K SettingName
    {
        set => settings_name = value;
    }

    public string Text
    {
        set
        {
            Header = value;
            ApplyAutomationMetadata(_combobox, value);
        }
    }

    public event EventHandler<EventArgs>? ValueChanged;

    public ComboboxCard()
    {
        _combobox.MinWidth = 200;
        _combobox.ItemsSource = _elements;
        Content = _combobox;
        ApplyAutomationMetadata(_combobox);
    }

    public void AddItem(string name, string value) => AddItem(name, value, true);

    public void AddItem(string name, string value, bool translate)
    {
        if (translate) name = CoreTools.Translate(name);
        _elements.Add(name);
        _values_ref.Add(name, value);
        _inverted_val_ref.Add(value, name);
    }

    public void ShowAddedItems()
    {
        try
        {
            string savedItem = CoreSettings.GetValue(settings_name);
            _combobox.SelectedIndex = _elements.IndexOf(_inverted_val_ref[savedItem]);
        }
        catch
        {
            _combobox.SelectedIndex = 0;
        }
        _combobox.SelectionChanged += (_, _) =>
        {
            try
            {
                string selectedName = _combobox.SelectedItem?.ToString() ?? "";
                CoreSettings.SetValue(
                    settings_name,
                    _values_ref[selectedName]
                );
                ValueChanged?.Invoke(this, EventArgs.Empty);
                var cmd = ValueChangedCommand;
                if (cmd?.CanExecute(null) == true)
                    cmd.Execute(null);
                string headerText = Header?.ToString() ?? "";
                if (!string.IsNullOrEmpty(headerText) && !string.IsNullOrEmpty(selectedName))
                    AccessibilityAnnouncementService.Announce(
                        CoreTools.Translate("{0}: {1}", headerText, selectedName));
            }
            catch (Exception ex)
            {
                Logger.Warn(ex);
            }
        };
    }

    public string SelectedValue() =>
        _combobox.SelectedItem?.ToString() ?? throw new InvalidCastException();

    public void SelectIndex(int index) => _combobox.SelectedIndex = index;
}
