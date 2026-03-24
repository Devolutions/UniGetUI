using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Controls.Settings;

public sealed partial class ButtonCard : SettingsCard
{
    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<ButtonCard, ICommand?>(nameof(Command));

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<ButtonCard, object?>(nameof(CommandParameter));

    private readonly Button _button = new();

    public string ButtonText
    {
        set => _button.Content = CoreTools.Translate(value);
    }

    public string Text
    {
        set => Header = CoreTools.Translate(value);
    }

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
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
            _button.Command = (ICommand?)change.NewValue;
        else if (change.Property == CommandParameterProperty)
            _button.CommandParameter = change.NewValue;
    }
}
