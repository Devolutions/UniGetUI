using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class SimplePageView : UserControl, IShellPage
{
    private TextBlock BodyLeadText => GetControl<TextBlock>("BodyLeadBlock");

    private TextBlock BodyDescriptionText => GetControl<TextBlock>("BodyDescriptionBlock");

    public SimplePageView()
        : this(string.Empty, string.Empty, string.Empty, string.Empty)
    {
    }

    public SimplePageView(string title, string subtitle, string lead, string description)
    {
        Title = title;
        Subtitle = subtitle;
        SupportsSearch = false;
        SearchPlaceholder = string.Empty;

        InitializeComponent();
        BodyLeadText.Text = lead;
        BodyDescriptionText.Text = description;
    }

    public string Title { get; }

    public string Subtitle { get; }

    public bool SupportsSearch { get; }

    public string SearchPlaceholder { get; }

    public void UpdateSearchQuery(string query)
    {
    }

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
