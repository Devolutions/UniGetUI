using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class BundleSecurityReportDialog : UniGetUI.Avalonia.Views.DialogPages.ImmersiveDialog
{
    private readonly bool _gated;

    public bool Accepted { get; private set; }

    public BundleSecurityReportDialog(BundleReport report)
    {
        InitializeComponent();

        _gated = report.HasHighSeverityFindings;
        Accepted = !_gated;

        HeaderText.Text = _gated
            ? CoreTools.Translate("This bundle changes how packages are installed")
            : CoreTools.Translate("Some packages use non-default install settings");

        SubHeaderText.Text = _gated
            ? CoreTools.Translate("Review the findings below. The packages will not be added to the bundle unless you continue.")
            : CoreTools.Translate("Nothing was blocked. These entries are listed so you know what the bundle asked for.");

        IntroIcon.Classes.Add("intro-icon");
        IntroIcon.Classes.Add(_gated ? "high" : "info");
        IntroIcon.Data = Geometry.Parse(_gated ? WarningGlyph : InfoGlyph);

        AddSummaryChip(
            CoreTools.Translate("{0} packages", report.Contents.Count), "neutral");
        if (report.HighSeverityCount > 0)
            AddSummaryChip(
                CoreTools.Translate("{0} high risk", report.HighSeverityCount), "high");
        if (report.InformationalCount > 0)
            AddSummaryChip(
                CoreTools.Translate("{0} informational", report.InformationalCount), "info");

        FootnoteText.Classes.Add("report-footnote");
        FootnoteText.IsVisible = report.HasSettingControlledStripping;
        FootnoteText.Text = CoreTools.Translate(
            "Values marked Removed were stripped during import. You can allow them under Settings > {0}",
            CoreTools.Translate("Administrator rights and other dangerous settings"));

        PopulateFindings(report);

        string primaryLabel = _gated
            ? CoreTools.Translate("Import anyway")
            : CoreTools.Translate("OK");
        PrimaryButton.Content = primaryLabel;
        AutomationProperties.SetName(PrimaryButton, primaryLabel);
        CancelButton.IsVisible = _gated;

        PrimaryButton.Click += (_, _) => Complete(true);
        CancelButton.Click += (_, _) => Complete(false);
    }

    private void PopulateFindings(BundleReport report)
    {
        List<BundleReportPackage> risky = [];
        List<BundleReportPackage> informational = [];
        foreach (var package in report.Contents.Values)
            (package.HasHighSeverityFindings ? risky : informational).Add(package);

        risky.Sort(CompareByDisplayName);
        informational.Sort(CompareByDisplayName);

        foreach (var package in risky)
            FindingsHost.Children.Add(BuildPackageSection(package));

        if (informational.Count is 0)
            return;

        if (risky.Count is 0)
            foreach (var package in informational)
                FindingsHost.Children.Add(BuildPackageSection(package));
        else
            FindingsHost.Children.Add(BuildInformationalGroup(informational));
    }

    private static int CompareByDisplayName(BundleReportPackage left, BundleReportPackage right)
        => StringComparer.OrdinalIgnoreCase.Compare(
            left.Subject.DisplayName, right.Subject.DisplayName);

    private static Control BuildInformationalGroup(List<BundleReportPackage> packages)
    {
        var content = new StackPanel
        {
            Spacing = 16,
            Margin = new global::Avalonia.Thickness(0, 10, 0, 2),
        };
        foreach (var package in packages)
            content.Children.Add(BuildPackageSection(package));

        var header = new TextBlock
        {
            Text = CoreTools.Translate(
                "Other packages with informational findings ({0})", packages.Count),
        };
        header.Classes.Add("group-header");

        return new Expander
        {
            Header = header,
            Content = content,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    private const string WarningGlyph =
        "M12 2 L23 21 L1 21 Z M11 9 h2 v6 h-2 Z M11 17 h2 v2 h-2 Z";

    private const string InfoGlyph =
        "M2 12 A10 10 0 1 1 22 12 A10 10 0 1 1 2 12 Z M11 6 h2 v2 h-2 Z M11 10 h2 v8 h-2 Z";

    private void AddSummaryChip(string text, string variantClass)
    {
        var chip = BuildTag(text, variantClass);
        chip.Margin = new global::Avalonia.Thickness(0, 0, 8, 0);
        SummaryHost.Children.Add(chip);
    }

    private static Control BuildPackageSection(BundleReportPackage package)
    {
        var section = new StackPanel { Spacing = 8 };

        var title = new TextBlock { Text = package.Subject.DisplayName };
        title.Classes.Add("package-header");
        var rule = new Border();
        rule.Classes.Add("package-rule");

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(rule, 1);
        header.Children.Add(title);
        header.Children.Add(rule);
        section.Children.Add(header);

        var identity = new TextBlock { Text = DescribeIdentity(package.Subject) };
        identity.Classes.Add("package-subheader");
        section.Children.Add(identity);

        var ordered = package
            .Entries.OrderByDescending(entry => entry.Severity)
            .ThenBy(entry => entry.Field, StringComparer.Ordinal);

        foreach (var entry in ordered)
            section.Children.Add(BuildFindingCard(entry));

        return section;
    }

    private static string DescribeIdentity(BundleReportSubject subject)
    {
        var parts = new List<string> { subject.Id };
        if (subject.ManagerName.Length > 0)
            parts.Add(subject.ManagerName);
        if (subject.Source.Length > 0)
            parts.Add(subject.Source);
        return string.Join("  ·  ", parts);
    }

    private static Control BuildFindingCard(BundleReportEntry entry)
    {
        bool high = entry.Severity is BundleReportSeverity.High;
        string severityClass = high ? "high" : "info";

        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = CoreTools.Translate(entry.Label) };
        label.Classes.Add("finding-label");
        body.Children.Add(label);

        if (entry.LineCarriesTheValue)
        {
            var value = new TextBlock { Text = entry.Value };
            value.Classes.Add("finding-value");
            body.Children.Add(value);
        }

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 10,
        };

        var severityTag = BuildTag(
            high ? CoreTools.Translate("High risk") : CoreTools.Translate("Info"),
            severityClass);
        Grid.SetColumn(severityTag, 0);
        Grid.SetColumn(body, 1);
        row.Children.Add(severityTag);
        row.Children.Add(body);

        if (!entry.Allowed)
        {
            var removedTag = BuildTag(CoreTools.Translate("Removed"), "neutral");
            Grid.SetColumn(removedTag, 2);
            row.Children.Add(removedTag);
        }

        var card = new Border { Child = row };
        card.Classes.Add("finding");
        return card;
    }

    private static Border BuildTag(string text, string variantClass)
    {
        var caption = new TextBlock { Text = text };
        caption.Classes.Add("tag-text");
        caption.Classes.Add(variantClass);

        var tag = new Border { Child = caption };
        tag.Classes.Add("tag");
        tag.Classes.Add(variantClass);
        return tag;
    }

    private void Complete(bool accepted)
    {
        Accepted = accepted;
        Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(
            () => (_gated ? CancelButton : PrimaryButton).Focus(),
            DispatcherPriority.Background);
    }
}
