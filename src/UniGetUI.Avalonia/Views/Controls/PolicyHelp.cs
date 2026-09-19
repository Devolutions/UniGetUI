using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace UniGetUI.Avalonia.Views.Controls;

/// <summary>Applies the same localized policy help to a wrapped tooltip and accessibility help text.</summary>
public static class PolicyHelp
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Text", typeof(PolicyHelp));

    public static void SetText(Control control, string? value) =>
        control.SetValue(TextProperty, value);

    public static string? GetText(Control control) =>
        control.GetValue(TextProperty);

    static PolicyHelp()
    {
        TextProperty.Changed.AddClassHandler<Control>((control, change) =>
        {
            string? text = change.GetNewValue<string?>();
            AutomationProperties.SetHelpText(control, text);
            ToolTip.SetTip(
                control,
                string.IsNullOrWhiteSpace(text)
                    ? null
                    : new TextBlock
                    {
                        Text = text,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 420,
                    });
        });
    }
}
