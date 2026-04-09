using System.Diagnostics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using UniGetUI.Core.Tools;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.Views.Controls.Settings;

public sealed partial class TextboxCard : SettingsCard
{
    public static readonly StyledProperty<ICommand?> ValueChangedCommandProperty =
        AvaloniaProperty.Register<TextboxCard, ICommand?>(nameof(ValueChangedCommand));

    public ICommand? ValueChangedCommand
    {
        get => GetValue(ValueChangedCommandProperty);
        set => SetValue(ValueChangedCommandProperty, value);
    }

    private readonly TextBox _textbox;
    private readonly Button _helpbutton;   // WinUI HyperlinkButton → plain Button + Process.Start

    private CoreSettings.K setting_name = CoreSettings.K.Unset;
    private Uri? _helpUri;

    public CoreSettings.K SettingName
    {
        set
        {
            setting_name = value;
            _textbox.Text = CoreSettings.GetValue(setting_name);
            _textbox.TextChanged += (_, _) => SaveValue();
        }
    }

    public bool IsNumericOnly { get; set; }

    public string Placeholder
    {
        set => _textbox.Watermark = value;
    }

    public string Text
    {
        set => Header = value;
    }

    public Uri HelpUrl
    {
        set
        {
            _helpUri = value;
            _helpbutton.IsVisible = true;
            _helpbutton.Content = CoreTools.Translate("More info");
        }
    }

    public event EventHandler<EventArgs>? ValueChanged;

    public TextboxCard()
    {
        _helpbutton = new Button
        {
            IsVisible = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _helpbutton.Click += (_, _) =>
        {
            if (_helpUri is not null)
                Process.Start(new ProcessStartInfo(_helpUri.ToString()) { UseShellExecute = true });
        };

        _textbox = new TextBox { MinWidth = 200, MaxWidth = 300 };

        var s = new StackPanel { Orientation = Orientation.Horizontal };
        s.Children.Add(_helpbutton);
        s.Children.Add(_textbox);

        Content = s;
    }

    public void SaveValue()
    {
        string sanitizedText = _textbox.Text ?? "";

        if (IsNumericOnly)
        {
            string filtered = string.Concat(sanitizedText.Where(char.IsDigit));
            if (filtered != sanitizedText)
            {
                _textbox.Text = filtered; // triggers TextChanged → SaveValue again with clean text
                return;
            }
            sanitizedText = filtered;
        }

        if (CoreSettings.ResolveKey(setting_name).Contains("File"))
            sanitizedText = CoreTools.MakeValidFileName(sanitizedText);

        if (sanitizedText != "")
            CoreSettings.SetValue(setting_name, sanitizedText);
        else
            CoreSettings.Set(setting_name, false);

        ValueChanged?.Invoke(this, EventArgs.Empty);
        var cmd = ValueChangedCommand;
        if (cmd?.CanExecute(null) == true)
            cmd.Execute(null);
    }
}
