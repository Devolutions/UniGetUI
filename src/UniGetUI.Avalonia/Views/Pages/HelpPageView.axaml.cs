using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class HelpPageView : UserControl, IShellPage
{
    private TextBlock LeadTitleText => GetControl<TextBlock>("LeadTitleBlock");
    private TextBlock LeadDescriptionText => GetControl<TextBlock>("LeadDescriptionBlock");
    private TextBlock DocumentationTitleText => GetControl<TextBlock>("DocumentationTitleBlock");
    private TextBlock DocumentationDescriptionText => GetControl<TextBlock>("DocumentationDescriptionBlock");
    private TextBlock CommunityTitleText => GetControl<TextBlock>("CommunityTitleBlock");
    private TextBlock CommunityDescriptionText => GetControl<TextBlock>("CommunityDescriptionBlock");
    private TextBlock AboutTitleText => GetControl<TextBlock>("AboutTitleBlock");
    private TextBlock AboutVersionText => GetControl<TextBlock>("AboutVersionBlock");
    private TextBlock AboutLicenseText => GetControl<TextBlock>("AboutLicenseBlock");
    private Button OpenDocumentationButtonControl => GetControl<Button>("OpenDocumentationButton");
    private Button OpenChangelogButtonControl => GetControl<Button>("OpenChangelogButton");
    private Button OpenIssuesButtonControl => GetControl<Button>("OpenIssuesButton");
    private Button OpenDiscussionsButtonControl => GetControl<Button>("OpenDiscussionsButton");
    private Button OpenLicenseButtonControl => GetControl<Button>("OpenLicenseButton");
    private Button OpenContributorsButtonControl => GetControl<Button>("OpenContributorsButton");

    public string Title { get; } = CoreTools.Translate("Help");
    public string Subtitle { get; } = CoreTools.Translate("Help and documentation");
    public bool SupportsSearch => false;
    public string SearchPlaceholder => string.Empty;

    public HelpPageView()
    {
        InitializeComponent();
        ApplyLocalizedText();
    }

    private void ApplyLocalizedText()
    {
        LeadTitleText.Text = CoreTools.Translate("Help and documentation");
        LeadDescriptionText.Text = CoreTools.Translate(
            "But here are other things you can do to learn about WingetUI even more:"
        );

        DocumentationTitleText.Text = CoreTools.Translate("Help and documentation");
        DocumentationDescriptionText.Text = CoreTools.Translate(
            "But here are other things you can do to learn about WingetUI even more:"
        );
        OpenDocumentationButtonControl.Content = CoreTools.Translate("View page on browser");
        OpenChangelogButtonControl.Content = CoreTools.Translate("Release notes");

        CommunityTitleText.Text = CoreTools.Translate("Support the developer");
        CommunityDescriptionText.Text = CoreTools.Translate(
            "View WingetUI on GitHub"
        );
        OpenIssuesButtonControl.Content = CoreTools.Translate("Open GitHub");
        OpenDiscussionsButtonControl.Content = CoreTools.Translate("View WingetUI on GitHub");

        AboutTitleText.Text = CoreTools.Translate("About WingetUI");
        AboutVersionText.Text = CoreTools.Translate("About WingetUI version {0}", CoreData.VersionName);
        AboutLicenseText.Text = CoreTools.Translate("Using WingetUI implies the acceptation of the MIT License");
        OpenLicenseButtonControl.Content = CoreTools.Translate("WingetUI License");
        OpenContributorsButtonControl.Content = CoreTools.Translate("Contributors");
    }

    public void UpdateSearchQuery(string query) { }

    private void OpenDocumentationButton_OnClick(object? sender, RoutedEventArgs e)
        => OpenDocumentationWindow();

    private async void OpenChangelogButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var win = new ReleaseNotesWindow();
        if (VisualRoot is Window owner)
            await win.ShowDialog(owner);
        else
            win.Show();
    }

    private void OpenIssuesButton_OnClick(object? sender, RoutedEventArgs e)
        => OpenUrl("https://github.com/marticliment/UniGetUI/issues");

    private void OpenDiscussionsButton_OnClick(object? sender, RoutedEventArgs e)
        => OpenUrl("https://github.com/marticliment/UniGetUI/discussions");

    private void OpenLicenseButton_OnClick(object? sender, RoutedEventArgs e)
        => OpenUrl("https://github.com/marticliment/UniGetUI/blob/main/LICENSE");

    private void OpenContributorsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var win = new AboutPageWindow();
        if (VisualRoot is Window owner)
            win.ShowDialog(owner);
        else
            win.Show();
    }

    private async void OpenDocumentationWindow()
    {
        var win = new DocumentationBrowserWindow();
        if (VisualRoot is Window owner)
            await win.ShowDialog(owner);
        else
            win.Show();
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // ignore — best effort
        }
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
