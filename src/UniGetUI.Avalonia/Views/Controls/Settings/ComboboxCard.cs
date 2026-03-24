using System.Collections.ObjectModel;
using Avalonia.Controls;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.Views.Controls.Settings;

public sealed partial class ComboboxCard : SettingsCard
{
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
        set => Header = CoreTools.Translate(value);
    }

    public event EventHandler<EventArgs>? ValueChanged;

    public ComboboxCard()
    {
        _combobox.MinWidth = 200;
        _combobox.ItemsSource = _elements;
        Content = _combobox;
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
                CoreSettings.SetValue(
                    settings_name,
                    _values_ref[_combobox.SelectedItem?.ToString() ?? ""]
                );
                ValueChanged?.Invoke(this, EventArgs.Empty);
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
