using System.Text.Json;
using System.Text.RegularExpressions;
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

    [GeneratedRegex("CoreTools\\.Translate\\(\"(?<key>(?:\\\\.|[^\"\\\\])*)")]
    private static partial Regex CSharpTranslateRegex();

    [GeneratedRegex("\\{t:Translate\\s+(?<key>[^}\\r\\n]+)\\}")]
    private static partial Regex AxamlTranslateRegex();
}
