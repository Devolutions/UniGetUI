using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Devolutions.Now.Policy.Api;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

namespace UniGetUI.Tests.PolicyEditor;

public partial class PolicyEditorLocalizationTests
{
    [Fact]
    public void PolicyEditorTranslationKeysExistInEnglishCatalog()
    {
        string root = FindRepositoryRoot();
        string languagePath = Path.Combine(root, "src", "Languages", "lang_en.json");
        Dictionary<string, string> language = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(languagePath)) ?? throw new InvalidOperationException();

        var sourceFiles = new List<string>
        {
            Path.Combine(
                root,
                "src",
                "UniGetUI.Avalonia",
                "ViewModels",
                "Pages",
                "SettingsPages",
                "AgentPolicyInspectorViewModel.cs"),
            Path.Combine(
                root,
                "src",
                "UniGetUI.Avalonia",
                "Views",
                "Pages",
                "SettingsPages",
                "AgentPolicyInspector.axaml"),
        };
        sourceFiles.AddRange(Directory.EnumerateFiles(
            Path.Combine(
                root,
                "src",
                "UniGetUI.Avalonia",
                "ViewModels",
                "Pages",
                "SettingsPages",
                "PolicyEditor"),
            "*.cs"));
        sourceFiles.AddRange(Directory.EnumerateFiles(
            Path.Combine(
                root,
                "src",
                "UniGetUI.Avalonia",
                "Views",
                "Pages",
                "SettingsPages",
                "PolicyEditor"),
            "*.*").Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)));

        HashSet<string> keys = [];
        foreach (string sourceFile in sourceFiles)
        {
            string source = File.ReadAllText(sourceFile);
            foreach (Match match in CSharpTranslateRegex().Matches(source))
            {
                string escaped = match.Groups["key"].Value;
                keys.Add(JsonSerializer.Deserialize<string>($"\"{escaped}\"")
                    ?? throw new InvalidOperationException());
            }

            foreach (Match match in AxamlTranslateRegex().Matches(source))
            {
                string key = match.Groups["key"].Value.Trim();
                if (key.StartsWith("Text='", StringComparison.Ordinal) && key.EndsWith('\''))
                    key = key[6..^1];
                keys.Add(key);
            }
        }

        keys.UnionWith(
        [
            "Policy management",
            "Edit the active policy",
            "Create a new policy",
            "Repair the stored policy",
            "Replace the active policy identity",
        ]);
        keys.UnionWith(Enum.GetNames<Devolutions.Now.Policy.Model.Operation>());
        keys.UnionWith(Enum.GetNames<Devolutions.Now.Policy.Model.ManagerName>());
        keys.UnionWith(Enum.GetNames<Devolutions.Now.Policy.Model.Scope>());
        keys.UnionWith(Enum.GetNames<Devolutions.Now.Policy.Model.Architecture>());
        keys.UnionWith(Enum.GetNames<Devolutions.Now.Policy.Model.Elevation>());
        keys.UnionWith(Enum.GetNames<Devolutions.Now.Policy.Model.Decision>());
        keys.UnionWith(Enum.GetNames<Devolutions.Now.Policy.Model.RulePrecedence>());
        keys.UnionWith(Enum.GetNames<ErrorCode>());
        keys.UnionWith(Enum.GetNames<PolicyValidationSeverity>());
        keys.UnionWith(
        [
            "Allowed custom parameters",
            "Allowed custom locations",
            "Allowed pre/post commands",
            "Allowed hash-check skipping",
        ]);
        keys.UnionWith(typeof(PolicyEditorHelp)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => (string)property.GetValue(null)!));

        string[] missing = keys
            .Where(key => !language.TryGetValue(key, out string? value)
                || string.IsNullOrWhiteSpace(value))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(missing.Length == 0, $"Missing English policy translation keys: {string.Join(", ", missing)}");
    }

    [Fact]
    public void CSharpTranslationScanner_ExtractsMultilineLiteralCalls()
    {
        const string methodName = "CoreTools." + "Translate";
        string source = $$"""
            {{methodName}}(
                "Multiline policy key")
            """;

        Match match = Assert.Single(CSharpTranslateRegex().Matches(source).Cast<Match>());

        Assert.Equal("Multiline policy key", match.Groups["key"].Value);
    }

    [Fact]
    public void FindingSeverityPresentation_UsesThemeAwareClasses()
    {
        string root = FindRepositoryRoot();
        string dialog = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UniGetUI.Avalonia",
            "Views",
            "Pages",
            "SettingsPages",
            "PolicyEditor",
            "PolicyEditorDialog.axaml"));
        string structuredUi = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UniGetUI.Avalonia",
            "ViewModels",
            "Pages",
            "SettingsPages",
            "PolicyEditor",
            "PolicyEditorStructuredUi.cs"));

        Assert.Contains("Classes.finding-error=\"{Binding IsError}\"", dialog);
        Assert.Contains("Classes.finding-warning=\"{Binding IsWarning}\"", dialog);
        Assert.Contains("DynamicResource SystemFillColorCriticalBrush", dialog);
        Assert.Contains("DynamicResource SystemFillColorCautionBrush", dialog);
        Assert.DoesNotContain("Firebrick", dialog);
        Assert.DoesNotContain("DarkOrange", dialog);
        Assert.DoesNotContain("PolicyEditorSeverityConverters", structuredUi);
    }

    [Fact]
    public void ManagementWritePresentation_SeparatesAgentAndAppCapabilities()
    {
        string root = FindRepositoryRoot();
        string view = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UniGetUI.Avalonia",
            "Views",
            "Pages",
            "SettingsPages",
            "AgentPolicyInspector.axaml"));

        Assert.Contains("Text=\"{t:Translate Agent write capability}\"", view);
        Assert.Contains("Text=\"{Binding AgentWriteCapabilityText}\"", view);
        Assert.Contains("Text=\"{t:Translate Policy changes from this app}\"", view);
        Assert.Contains("Text=\"{Binding PolicyChangesFromThisAppText}\"", view);
        Assert.Contains("Text=\"{t:Translate Reason}\"", view);
        Assert.Contains("Text=\"{Binding PolicyChangesReasonText}\"", view);
        Assert.Contains(
            "automation:AutomationProperties.Name=\"{t:Translate Policy change availability reason}\"",
            view);
        Assert.Contains("Text=\"{t:Translate Elevation required}\"", view);
        Assert.Contains("Text=\"{Binding ManagementElevationRequiredText}\"", view);
        Assert.DoesNotContain("ManagementCapabilityText", view);
        Assert.DoesNotContain("ManagementReadOnlyReasonText", view);
    }

    [Fact]
    public void PolicyHelp_CoversAuthoredFixedAgentManagedAndDangerousSemantics()
    {
        Assert.Contains("authored identity", PolicyEditorHelp.PolicyId, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("software-managed", PolicyEditorHelp.PolicyFormatVersion, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Agent-managed", PolicyEditorHelp.ConfiguredPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", PolicyEditorHelp.DefaultDecision, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("integrity", PolicyEditorHelp.AllowSkipHashCheck, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dangerous", PolicyEditorHelp.AllowPrePostCommands, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dependencies", PolicyEditorHelp.Constraints, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agreements", PolicyEditorHelp.Constraints, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reboot", PolicyEditorHelp.Constraints, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("warnings require acknowledgement", PolicyEditorHelp.Save, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PolicyViews_UseSharedTooltipAndAccessibleHelpMetadata()
    {
        string root = FindRepositoryRoot();
        string editor = File.ReadAllText(Path.Combine(
            root, "src", "UniGetUI.Avalonia", "Views", "Pages", "SettingsPages",
            "PolicyEditor", "PolicyEditorDialog.axaml"));
        string inspector = File.ReadAllText(Path.Combine(
            root, "src", "UniGetUI.Avalonia", "Views", "Pages", "SettingsPages",
            "AgentPolicyInspector.axaml"));
        string helpControl = File.ReadAllText(Path.Combine(
            root, "src", "UniGetUI.Avalonia", "Views", "Controls", "PolicyHelp.cs"));

        Assert.True(
            Regex.Matches(editor, "controls:PolicyHelp\\.Text=").Count >= 45,
            "Every policy field and non-obvious editor control should expose shared help.");
        Assert.True(
            Regex.Matches(inspector, "controls:PolicyHelp\\.Text=").Count >= 12,
            "Inspector management controls should expose shared help.");
        Assert.Contains("AutomationProperties.SetHelpText(control, text)", helpControl);
        Assert.Contains("TextWrapping = TextWrapping.Wrap", helpControl);
        Assert.Contains("MaxWidth = 420", helpControl);
    }

    [Fact]
    public void PolicyFormatVersion_IsReadOnlyAndFindingNavigationTargetsStructuredFields()
    {
        string root = FindRepositoryRoot();
        XDocument editor = XDocument.Load(Path.Combine(
            root, "src", "UniGetUI.Avalonia", "Views", "Pages", "SettingsPages",
            "PolicyEditor", "PolicyEditorDialog.axaml"));
        XElement format = Assert.Single(editor.Descendants(),
            element => (string?)element.Attribute("Tag") == "/PolicyVersion");
        Assert.Equal("TextBlock", format.Name.LocalName);
        Assert.Equal(
            "{Binding Document.PolicyFormatVersion}",
            (string?)format.Attribute("Text"));
        Assert.Contains(editor.Descendants(),
            element => (string?)element.Attribute("Tag") == "/Rules/*/Priority");
        Assert.Contains(editor.Descendants(),
            element => (string?)element.Attribute("Tag") == "/Rules/*/Match/PackageNames");
        Assert.Contains(editor.Descendants(),
            element => (string?)element.Attribute("Tag") == "/Rules/*/Match/Versions");
        Assert.Contains(editor.Descendants(),
            element => (string?)element.Attribute("Click") == "FindingNavigateButton_Click");
        Assert.Contains(editor.Descendants(),
            element => (string?)element.Attribute("Click") == "RawSyntaxNavigateButton_Click");

        Assert.True(
            UniGetUI.Avalonia.Views.Pages.SettingsPages.PolicyEditor.PolicyEditorDialog
                .TryGetRuleIndex("/Rules/3/Match/Versions/1", out int ruleIndex));
        Assert.Equal(3, ruleIndex);
        string normalized =
            UniGetUI.Avalonia.Views.Pages.SettingsPages.PolicyEditor.PolicyEditorDialog
                .NormalizeRulePointer("/Rules/3/Match/Versions/1");
        Assert.Equal("/Rules/*/Match/Versions/1", normalized);
        Assert.True(
            UniGetUI.Avalonia.Views.Pages.SettingsPages.PolicyEditor.PolicyEditorDialog
                .PointerTargetsTag(normalized, "/Rules/*/Match/Versions"));
    }

    [Fact]
    public void ActivePolicyInspectionSection_OwnsItsRefreshStatusAndDetails()
    {
        string root = FindRepositoryRoot();
        XDocument view = XDocument.Load(Path.Combine(
            root,
            "src",
            "UniGetUI.Avalonia",
            "Views",
            "Pages",
            "SettingsPages",
            "AgentPolicyInspector.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement section = Assert.Single(view.Descendants(),
            element => (string?)element.Attribute(x + "Name") == "ActivePolicyInspectionSection");

        Assert.Equal(
            "{Binding IsActivePolicyInspectionVisible}",
            (string?)section.Attribute("IsVisible"));
        Assert.DoesNotContain(section.Descendants(),
            element => (string?)element.Attribute("Command") == "{Binding RefreshCommand}");
        Assert.Contains(section.Descendants(),
            element => (string?)element.Attribute("DataContext") == "{Binding Status}");
        Assert.Contains(section.Descendants(),
            element => (string?)element.Attribute("IsVisible") == "{Binding HasPolicy}");
        Assert.Contains(section.Descendants(),
            element => (string?)element.Attribute("Text") == "{Binding RawJson}");

        XElement refresh = Assert.Single(view.Descendants(),
            element => (string?)element.Attribute("Command") == "{Binding RefreshPageCommand}");
        Assert.DoesNotContain(refresh.Ancestors(), element => element == section);
        Assert.Null(refresh.Attribute("IsEnabled"));
        Assert.Single(view.Descendants(),
            element => (string?)element.Attribute("Content") == "{t:Translate Refresh}");
    }

    [Fact]
    public void RawSyntaxError_HasOneAssertiveLiveRegionAndStatusHasNoFixedLiveSetting()
    {
        string root = FindRepositoryRoot();
        XDocument dialog = XDocument.Load(Path.Combine(
            root,
            "src",
            "UniGetUI.Avalonia",
            "Views",
            "Pages",
            "SettingsPages",
            "PolicyEditor",
            "PolicyEditorDialog.axaml"));
        XNamespace automation =
            "clr-namespace:Avalonia.Automation;assembly=Avalonia.Controls";
        XElement status = Assert.Single(dialog.Descendants(),
            element => element.Name.LocalName == "InfoBar"
                && (string?)element.Attribute("DataContext") == "{Binding Status}");
        XElement rawSyntaxError = Assert.Single(dialog.Descendants(),
            element => (string?)element.Attribute("Text")
                == "{Binding Session.SyntaxErrorMessage}");

        Assert.Null(status.Attribute(automation + "AutomationProperties.LiveSetting"));
        Assert.Equal(
            "Assertive",
            (string?)rawSyntaxError.Attribute(
                automation + "AutomationProperties.LiveSetting"));
    }

    [Fact]
    public void InspectorStatuses_RelyOnCentralizedSeverityAwareAnnouncements()
    {
        string root = FindRepositoryRoot();
        XDocument inspector = XDocument.Load(Path.Combine(
            root,
            "src",
            "UniGetUI.Avalonia",
            "Views",
            "Pages",
            "SettingsPages",
            "AgentPolicyInspector.axaml"));
        XNamespace automation =
            "clr-namespace:Avalonia.Automation;assembly=Avalonia.Controls";
        XElement[] statuses = inspector.Descendants()
            .Where(element => element.Name.LocalName == "InfoBar"
                && ((string?)element.Attribute("DataContext") == "{Binding ManagementStatus}"
                    || (string?)element.Attribute("DataContext") == "{Binding Status}"))
            .ToArray();

        Assert.Equal(2, statuses.Length);
        Assert.All(
            statuses,
            status => Assert.Null(
                status.Attribute(automation + "AutomationProperties.LiveSetting")));
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "Languages", "lang_en.json")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    [GeneratedRegex("CoreTools\\.Translate\\(\\s*\"(?<key>(?:\\\\.|[^\"\\\\])*)")]
    private static partial Regex CSharpTranslateRegex();

    [GeneratedRegex("\\{t:Translate\\s+(?<key>[^}\\r\\n]+)\\}")]
    private static partial Regex AxamlTranslateRegex();
}
