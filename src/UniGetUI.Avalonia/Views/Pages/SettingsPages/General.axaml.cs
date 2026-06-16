using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.Language;
using UniGetUI.Core.Tools;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class General : UserControl, ISettingsPage
{
    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("General preferences");

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested;

    public General()
    {
        DataContext = new GeneralViewModel();
        InitializeComponent();

        var vm = (GeneralViewModel)DataContext;
        vm.RestartRequired += (s, e) => RestartRequired?.Invoke(s, e);
        vm.NavigationRequested += (s, t) => NavigationRequested?.Invoke(s, t);

        // Populate language selector (complex dynamic content)
        var langDict = new Dictionary<string, string>(LanguageData.LanguageReference.AsEnumerable());
        foreach (string key in langDict.Keys.ToList())
        {
            if (key != "en" && LanguageData.TranslationPercentages.TryGetValue(key, out var pct))
                langDict[key] = langDict[key] + " (" + pct + ")";
        }
        // The "System language" entry is shown in the OS language (the language it actually
        // resolves to), not in the currently-selected app language. AutoTranslated keeps the
        // string discoverable by the translation tooling.
        string systemLanguageLabel = CoreTools.TranslateInSystemLanguage(CoreTools.AutoTranslated("System language"));
        foreach (var entry in langDict)
            LanguageSelector.AddItem(
                entry.Key == "default" ? systemLanguageLabel : entry.Value,
                entry.Key, false);
        LanguageSelector.SettingName = CoreSettings.K.PreferredLanguage;
        LanguageSelector.Text = CoreTools.Translate("UniGetUI display language:");
        LanguageSelector.ShowAddedItems();
        LanguageSelector.ValueChanged += (s, e) => RestartRequired?.Invoke(s, e);
        LanguageSelector.Description = BuildTranslatorDescription();
    }

    private static StackPanel BuildTranslatorDescription()
    {
        var label = new TextBlock
        {
            Text = CoreTools.Translate("Is your language missing or incomplete?"),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.8,
        };

        var link = new TextBlock
        {
            Text = CoreTools.Translate("Become a translator"),
            TextDecorations = TextDecorations.Underline,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(4, 0, 0, 0),
        };
        link.Bind(TextBlock.ForegroundProperty, link.GetResourceObservable("SystemControlHighlightAccentBrush"));
        link.PointerPressed += (_, _) =>
            CoreTools.Launch("https://github.com/Devolutions/UniGetUI/wiki#translating-wingetui");

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(label);
        panel.Children.Add(link);
        return panel;
    }
}
