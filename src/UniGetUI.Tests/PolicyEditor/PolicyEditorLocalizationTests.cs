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
