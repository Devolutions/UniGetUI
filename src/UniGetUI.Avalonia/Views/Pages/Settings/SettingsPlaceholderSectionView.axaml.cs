using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public partial class SettingsPlaceholderSectionView : UserControl, ISettingsSectionView
{
    private TextBlock LeadText => GetControl<TextBlock>("LeadBlock");

    private TextBlock DescriptionText => GetControl<TextBlock>("DescriptionBlock");

    private TextBlock NextStepText => GetControl<TextBlock>("NextStepBlock");

    public SettingsPlaceholderSectionView()
        : this(string.Empty, string.Empty, string.Empty)
    {
    }

    public SettingsPlaceholderSectionView(string title, string description, string nextStep)
    {
        SectionTitle = title;
        SectionSubtitle = description;
        SectionStatus = CoreTools.Translate("Please wait");

        InitializeComponent();
        LeadText.Text = title;
        DescriptionText.Text = description;
        NextStepText.Text = nextStep;
    }

    public string SectionTitle { get; }

    public string SectionSubtitle { get; }

    public string SectionStatus { get; }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private T GetControl<T>(string name)
        where T : Control
    {
        return this.FindControl<T>(name)
            ?? throw new InvalidOperationException($"Control '{name}' was not found.");
    }
}
