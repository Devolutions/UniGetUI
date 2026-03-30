using Avalonia.Markup.Xaml;
using Avalonia.Metadata;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.MarkupExtensions;

/// <summary>
/// Markup extension that translates a string at load time.
/// Usage: Text="{t:Translate Some text}"
/// For strings with commas use named-property form: Text="{t:Translate Text='A, B, C'}"
/// </summary>
public class TranslateExtension : MarkupExtension
{
    public TranslateExtension() { }
    public TranslateExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
        => CoreTools.Translate(Text);
}
