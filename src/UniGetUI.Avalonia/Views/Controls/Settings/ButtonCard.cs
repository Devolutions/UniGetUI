using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Controls.Settings;

public sealed partial class ButtonCard : SettingsCard
{
    private readonly Button _button = new();

    public string ButtonText
    {
        set => _button.Content = value;
    }

    public string Text
    {
        set => Header = value;
    }

    public new event EventHandler<EventArgs>? Click;

    public ButtonCard()
    {
        _button.MinWidth = 200;
        _button.Click += (_, _) => Click?.Invoke(this, EventArgs.Empty);
        Content = _button;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CommandProperty)
            _button.Command = Command;
        else if (change.Property == CommandParameterProperty)
            _button.CommandParameter = CommandParameter;
    }
}
