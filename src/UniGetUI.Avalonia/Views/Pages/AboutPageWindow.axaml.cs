using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UniGetUI.Core.Data;
using UniGetUI.Core.Language;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages;

/// <summary>View model for a third-party library license row.</summary>
public sealed class LibraryLicenseModel
{
    public string Name { get; init; } = string.Empty;
    public string License { get; init; } = string.Empty;
    public Uri? LicenseURL { get; init; }
    public string HomepageText { get; init; } = string.Empty;
    public Uri? HomepageUrl { get; init; }
    public string LicenseButtonText { get; init; } = string.Empty;
}

/// <summary>View model for a contributor (GitHub username → link button).</summary>
public sealed class ContributorItem
{
    public string Name { get; init; } = string.Empty;
    public Uri? GitHubUrl { get; init; }
}

/// <summary>View model for a translator row (name + language).</summary>
public sealed class TranslatorItem
{
    public string Name { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
}

/// <summary>
/// About dialog window with four tabs: About, Third-party licenses, Contributors, Translators.
/// </summary>
public partial class AboutPageWindow : Window
{
    private readonly ObservableCollection<LibraryLicenseModel> _licenses = [];
    private readonly ObservableCollection<ContributorItem> _contributors = [];
    private readonly ObservableCollection<TranslatorItem> _translators = [];

    public AboutPageWindow()
    {
        InitializeComponent();

        Title = CoreTools.Translate("About WingetUI");

        // Populate About tab
        VersionBlock.Text = CoreTools.Translate("About WingetUI version {0}", CoreData.VersionName);
        LicenseBlock.Text = CoreTools.Translate("Using WingetUI implies the acceptation of the MIT License");
        DisclaimerBlock.Text = CoreTools.Translate(
            "UniGetUI is not related to any of the compatible package managers. UniGetUI is an independent project."
        );

        // Apply translations to tab buttons
        TabAboutButton.Content = CoreTools.Translate("About");
        TabLicensesButton.Content = CoreTools.Translate("Third-party licenses");
        TabContributorsButton.Content = CoreTools.Translate("Contributors");
        TabTranslatorsButton.Content = CoreTools.Translate("Translators");

        // Populate licenses
        foreach (var name in LicenseData.LicenseNames.Keys)
        {
            _licenses.Add(new LibraryLicenseModel
            {
                Name = name,
                License = LicenseData.LicenseNames[name],
                LicenseURL = LicenseData.LicenseURLs.GetValueOrDefault(name),
                HomepageUrl = LicenseData.HomepageUrls.GetValueOrDefault(name),
                HomepageText = CoreTools.Translate("{0} homepage", name),
                LicenseButtonText = CoreTools.Translate("License"),
            });
        }
        LicensesPanel.ItemsSource = _licenses;

        // Populate contributors (string[] of GitHub handles)
        foreach (var handle in ContributorsData.Contributors)
        {
            _contributors.Add(new ContributorItem
            {
                Name = "@" + handle,
                GitHubUrl = new Uri("https://github.com/" + handle),
            });
        }
        ContributorsPanel.ItemsSource = _contributors;

        // Populate translators — Person is a struct with fields, so map to TranslatorItem
        foreach (var person in LanguageData.TranslatorsList)
        {
            _translators.Add(new TranslatorItem
            {
                Name = person.Name,
                Language = person.Language,
            });
        }
        TranslatorsPanel.ItemsSource = _translators;

        // Default tab
        SwitchTab(0);
    }

    // ── Tab switching ─────────────────────────────────────────────────────────

    private void SwitchTab(int tab)
    {
        AboutPanel.IsVisible = tab == 0;
        LicensesPanel.IsVisible = tab == 1;
        ContributorsPanel.IsVisible = tab == 2;
        TranslatorsPanel.IsVisible = tab == 3;

        TabAboutButton.Classes.Set("accent", tab == 0);
        TabAboutButton.Classes.Set("toolbar-primary", tab == 0);
        TabAboutButton.Classes.Set("toolbar-secondary", tab != 0);

        TabLicensesButton.Classes.Set("accent", tab == 1);
        TabLicensesButton.Classes.Set("toolbar-primary", tab == 1);
        TabLicensesButton.Classes.Set("toolbar-secondary", tab != 1);

        TabContributorsButton.Classes.Set("accent", tab == 2);
        TabContributorsButton.Classes.Set("toolbar-primary", tab == 2);
        TabContributorsButton.Classes.Set("toolbar-secondary", tab != 2);

        TabTranslatorsButton.Classes.Set("accent", tab == 3);
        TabTranslatorsButton.Classes.Set("toolbar-primary", tab == 3);
        TabTranslatorsButton.Classes.Set("toolbar-secondary", tab != 3);
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void TabButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out int tab))
            SwitchTab(tab);
    }

    private void LinkButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Uri url }) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url.ToString(), UseShellExecute = true });
        }
        catch
        {
            // best effort
        }
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e) => Close();
}
