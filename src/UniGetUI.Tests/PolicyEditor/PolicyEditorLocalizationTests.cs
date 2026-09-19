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
            "Replace the active policy identity",
        ]);
        keys.UnionWith(Enum.GetNames<Devolutions.Now.Policy.Model.Operation>());
        keys.UnionWith(Enum.GetNames<Devolutions.Now.Policy.Model.ManagerName>());
        keys.UnionWith(Enum.GetNames<Devolutions.Now.Policy.Model.Scope>());
        keys.UnionWith(Enum.GetNames<Devolutions.Now.Policy.Model.Architecture>());
        keys.UnionWith(Enum.GetNames<Devolutions.Now.Policy.Model.Elevation>());
        keys.UnionWith(Enum.GetNames<Devolutions.Now.Policy.Model.Decision>());
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
        keys.UnionWith(AllEnumOptionHelpTexts(includeManagers: false));
        keys.Add("{0} applies this rule to requests handled by that package manager. Leave all managers clear to include every package manager.");

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
        Assert.Contains("IsVisible=\"{Binding HasPolicyChangesReason}\"", view);
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
        Assert.Contains("permanent identifier", PolicyEditorHelp.PolicyId, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read-only", PolicyEditorHelp.PolicyFormatVersion, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Agent configuration", PolicyEditorHelp.ConfiguredPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("least-privilege", PolicyEditorHelp.DefaultDecision, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("integrity", PolicyEditorHelp.AllowSkipHashCheck, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("high-risk", PolicyEditorHelp.AllowPrePostCommands, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dependency", PolicyEditorHelp.Constraints, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agreement", PolicyEditorHelp.Constraints, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart", PolicyEditorHelp.Constraints, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Errors must be corrected", PolicyEditorHelp.Save, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a display name", PolicyEditorHelp.PolicyId, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("contoso-policy", PolicyEditorHelp.PolicyId, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a display name", PolicyEditorHelp.RuleId, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allow-winget-updates", PolicyEditorHelp.RuleId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PolicyHelp_CoversEveryDeclaredHelpPropertyAndAvoidsInternalJargon()
    {
        string root = FindRepositoryRoot();
        string surfaces = string.Join(
            Environment.NewLine,
            File.ReadAllText(Path.Combine(
                root,
                "src",
                "UniGetUI.Avalonia",
                "Views",
                "Pages",
                "SettingsPages",
                "PolicyEditor",
                "PolicyEditorDialog.axaml")),
            File.ReadAllText(Path.Combine(
                root,
                "src",
                "UniGetUI.Avalonia",
                "Views",
                "Pages",
                "SettingsPages",
                "AgentPolicyInspector.axaml")),
            File.ReadAllText(Path.Combine(
                root,
                "src",
                "UniGetUI.Avalonia",
                "ViewModels",
                "Pages",
                "SettingsPages",
                "AgentPolicyInspectorViewModel.cs")));
        System.Reflection.PropertyInfo[] properties = typeof(PolicyEditorHelp)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(string))
            .ToArray();

        Assert.NotEmpty(properties);
        foreach (System.Reflection.PropertyInfo property in properties)
        {
            string help = Assert.IsType<string>(property.GetValue(null));
            Assert.False(string.IsNullOrWhiteSpace(help), $"{property.Name} has no help text.");
            Assert.Contains($"PolicyEditorHelp.{property.Name}", surfaces, StringComparison.Ordinal);
        }

        string[] forbidden =
        [
            "DTO",
            "enum",
            "nullable",
            "array",
            "API endpoint",
            "wire format",
            "validator code",
            "serialization",
            "schema",
            "pointer",
            "SPKI",
            "implementation class",
        ];
        foreach (string help in properties.Select(property => (string)property.GetValue(null)!))
        {
            foreach (string term in forbidden)
            {
                Assert.DoesNotContain(term, help, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void EveryPolicyFieldLabelAndFocusableEditorFieldHasSharedHelp()
    {
        string root = FindRepositoryRoot();
        XDocument[] views =
        [
            XDocument.Load(Path.Combine(
                root,
                "src",
                "UniGetUI.Avalonia",
                "Views",
                "Pages",
                "SettingsPages",
                "PolicyEditor",
                "PolicyEditorDialog.axaml")),
            XDocument.Load(Path.Combine(
                root,
                "src",
                "UniGetUI.Avalonia",
                "Views",
                "Pages",
                "SettingsPages",
                "AgentPolicyInspector.axaml")),
        ];
        XNamespace controls = "using:UniGetUI.Avalonia.Views.Controls";
        XNamespace automation =
            "clr-namespace:Avalonia.Automation;assembly=Avalonia.Controls";

        static bool HasHelp(
            XElement element,
            XNamespace controlsNamespace,
            XNamespace automationNamespace) =>
            !string.IsNullOrWhiteSpace(
                (string?)element.Attribute(controlsNamespace + "PolicyHelp.Text"))
            || !string.IsNullOrWhiteSpace(
                (string?)element.Attribute(
                    automationNamespace + "AutomationProperties.HelpText"));

        XElement[] labels = views
            .SelectMany(view => view.Descendants())
            .Where(element =>
                ((string?)element.Attribute("Classes"))?
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("field-label", StringComparer.Ordinal) is true)
            .ToArray();
        Assert.NotEmpty(labels);
        Assert.All(
            labels,
            label => Assert.True(HasHelp(label, controls, automation)));

        XElement[] taggedEditorFields = views[0]
            .Descendants()
            .Where(element => element.Attribute("Tag") is not null)
            .ToArray();
        Assert.NotEmpty(taggedEditorFields);
        Assert.All(
            taggedEditorFields,
            field => Assert.True(HasHelp(field, controls, automation)));

        XElement auditSelector = Assert.Single(views[0].Descendants(),
            element => (string?)element.Attribute("Tag") == "/Enforcement/AuditMode");
        Assert.Equal(
            "{x:Static pvm:PolicyEditorHelp.AuditMode}",
            (string?)auditSelector.Attribute(controls + "PolicyHelp.Text"));
    }

    [Fact]
    public void EnumOptionHelp_ExplainsAdministratorOutcomeAndUnselectedBehavior()
    {
        IReadOnlyList<string> helpTexts = AllEnumOptionHelpTexts().ToArray();

        Assert.NotEmpty(helpTexts);
        Assert.All(helpTexts, help => Assert.False(string.IsNullOrWhiteSpace(help)));
        Assert.Contains(helpTexts, help =>
            help.Contains("without administrator privileges", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(helpTexts, help =>
            help.Contains("requires administrator privileges", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(helpTexts, help =>
            help.Contains("32-bit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(helpTexts, help =>
            help.Contains("current user", StringComparison.OrdinalIgnoreCase));
        Assert.All(helpTexts, help =>
            Assert.Contains("Leave", help, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BooleanMatchHelp_ExplainsEveryTriStateAndIsBoundToLabelsAndControls()
    {
        string[] helpTexts =
        [
            PolicyEditorHelp.InteractiveMatch,
            PolicyEditorHelp.SkipHashMatch,
            PolicyEditorHelp.PrereleaseMatch,
            PolicyEditorHelp.CustomParametersMatch,
            PolicyEditorHelp.CustomLocationMatch,
            PolicyEditorHelp.PrePostCommandsMatch,
            PolicyEditorHelp.KillBeforeMatch,
            PolicyEditorHelp.UninstallPreviousMatch,
        ];
        foreach (string help in helpTexts)
        {
            Assert.Contains("Does not matter ignores", help, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Yes matches", help, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No matches", help, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("user interaction", PolicyEditorHelp.InteractiveMatch, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unattended", PolicyEditorHelp.InteractiveMatch, StringComparison.OrdinalIgnoreCase);

        string root = FindRepositoryRoot();
        string view = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UniGetUI.Avalonia",
            "Views",
            "Pages",
            "SettingsPages",
            "PolicyEditor",
            "PolicyEditorDialog.axaml"));
        string[] helpProperties =
        [
            nameof(PolicyEditorHelp.InteractiveMatch),
            nameof(PolicyEditorHelp.SkipHashMatch),
            nameof(PolicyEditorHelp.PrereleaseMatch),
            nameof(PolicyEditorHelp.CustomParametersMatch),
            nameof(PolicyEditorHelp.CustomLocationMatch),
            nameof(PolicyEditorHelp.PrePostCommandsMatch),
            nameof(PolicyEditorHelp.KillBeforeMatch),
            nameof(PolicyEditorHelp.UninstallPreviousMatch),
        ];
        foreach (string property in helpProperties)
        {
            Assert.Equal(
                2,
                Regex.Matches(
                    view,
                    $"controls:PolicyHelp.Text=\"{{x:Static pvm:PolicyEditorHelp.{property}}}\"")
                    .Count);
        }
    }

    [Fact]
    public void RequestCharacteristics_UseUserFacingLabelsAndMatchOnlyHelp()
    {
        string root = FindRepositoryRoot();
        string view = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UniGetUI.Avalonia",
            "Views",
            "Pages",
            "SettingsPages",
            "PolicyEditor",
            "PolicyEditorDialog.axaml"));

        Assert.Contains("Text=\"{t:Translate Request characteristics}\"", view);
        Assert.Contains("Text=\"{t:Translate Custom parameters}\"", view);
        Assert.Contains("Text=\"{t:Translate Custom install location}\"", view);
        Assert.Contains("Text=\"{t:Translate Pre/post commands}\"", view);
        Assert.Contains("Text=\"{t:Translate Stop running apps before operation}\"", view);
        Assert.Contains("Text=\"{t:Translate Uninstall previous version}\"", view);
        Assert.DoesNotContain("Text=\"{t:Translate Has custom", view);
        Assert.DoesNotContain("Text=\"{t:Translate Has pre/post", view);
        Assert.Contains("no effect", PolicyEditorHelp.DisabledRuleHint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("every package request", PolicyEditorHelp.UnrestrictedRuleWarning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prohibit", PolicyEditorHelp.CustomParametersMatch, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("allow", PolicyEditorHelp.CustomParametersMatch, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IsEnabled=\"{Binding CanMoveUp}\"", view);
        Assert.Contains("IsEnabled=\"{Binding CanMoveDown}\"", view);
        Assert.Contains("IsEnabled=\"{Binding Session.CanSwitchToStructured}\"", view);
        Assert.Contains("Focusable=\"True\"", view);
        string codeBehind = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UniGetUI.Avalonia",
            "Views",
            "Pages",
            "SettingsPages",
            "PolicyEditor",
            "PolicyEditorDialog.axaml.cs"));
        Assert.Contains("FocusMovedRule", codeBehind);
        string dialogViewModel = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UniGetUI.Avalonia",
            "ViewModels",
            "Pages",
            "SettingsPages",
            "PolicyEditor",
            "PolicyEditorDialogViewModel.cs"));
        Assert.Contains("AnnounceRulePosition", dialogViewModel);
        Assert.Contains("AutomationLiveSetting.Polite", dialogViewModel);
        XDocument editor = XDocument.Parse(view);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement rulesHeader = Assert.Single(editor.Descendants(),
            element => (string?)element.Attribute(x + "Name") == "RulesSectionHeader");
        XElement addRule = Assert.Single(rulesHeader.Descendants(),
            element => (string?)element.Attribute("Click") == "AddRuleButton_Click");
        Assert.Equal("Auto,Auto", (string?)rulesHeader.Attribute("ColumnDefinitions"));
        Assert.Equal("Left", (string?)rulesHeader.Attribute("HorizontalAlignment"));
        Assert.Equal("Center", (string?)addRule.Attribute("VerticalAlignment"));
        Assert.DoesNotContain(
            ((string?)rulesHeader.Attribute("ColumnDefinitions")) ?? "",
            "*",
            StringComparison.Ordinal);
        Assert.Contains("Text=\"{t:Translate Additional safety limits}\"", view);
        Assert.Contains("IsVisible=\"{Binding IsAllowDecision}\"", view);
        Assert.Contains("IsVisible=\"{Binding HasRuleSafetyAdvisories}\"", view);
        Assert.Contains("IsVisible=\"{Binding HasFieldSafetyAdvisories}\"", view);
        Assert.Contains("IsVisible=\"{Binding HasSkipHashCheckAdvisory}\"", view);
        Assert.Contains("IsVisible=\"{Binding HasCustomParametersAdvisory}\"", view);
        Assert.Contains("IsVisible=\"{Binding HasCustomInstallLocationAdvisory}\"", view);
        Assert.Contains("IsVisible=\"{Binding HasPrePostCommandsAdvisory}\"", view);
        Assert.Contains("Content=\"{Binding SkipHashCheckAdvisory}\"", view);
        Assert.Contains("Content=\"{Binding CustomParametersAdvisory}\"", view);
        Assert.Contains("Content=\"{Binding CustomInstallLocationAdvisory}\"", view);
        Assert.Contains("Content=\"{Binding PrePostCommandsAdvisory}\"", view);
        Assert.Contains("Text=\"{Binding FieldSafetyAdvisoryCountText}\"", view);
        Assert.DoesNotContain("ItemsSource=\"{Binding SafetyAdvisories}\"", view);
        Assert.Contains("finding-warning-target", view);
        Assert.Contains("Tag=\"/Rules/*/Constraints/AllowSkipHashCheck\"", view);
        Assert.Contains("Tag=\"/Rules/*/Constraints/AllowCustomParameters\"", view);
        Assert.Contains("Tag=\"/Rules/*/Constraints/AllowCustomInstallLocation\"", view);
        Assert.Contains("Tag=\"/Rules/*/Constraints/AllowPrePostCommands\"", view);
        Assert.Contains("IsVisible=\"{Binding IsAdvisoryVisible}\"", view);
        Assert.DoesNotContain("Command=\"{Binding Session.ValidateCommand}\"", view);
        Assert.DoesNotContain("ItemsSource=\"{Binding Session.Findings}\"", view);
        Assert.Contains("IsVisible=\"{Binding HasFindingSummary}\"", view);
        Assert.Contains("Text=\"{Binding SelectedFindingMessage}\"", view);
        Assert.Contains("Click=\"TopFindingNavigateButton_Click\"", view);
        Assert.Contains("Click=\"PreviousFindingButton_Click\"", view);
        Assert.Contains("Click=\"NextFindingButton_Click\"", view);
    }

    [Fact]
    public void ResourceIdHelp_IsPersistentAndAccessiblyBoundToLabelsAndInputs()
    {
        string root = FindRepositoryRoot();
        string view = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UniGetUI.Avalonia",
            "Views",
            "Pages",
            "SettingsPages",
            "PolicyEditor",
            "PolicyEditorDialog.axaml"));

        Assert.Equal(
            2,
            Regex.Matches(
                view,
                "controls:PolicyHelp.Text=\"{x:Static pvm:PolicyEditorHelp.PolicyId}\"")
                .Count);
        Assert.Equal(
            2,
            Regex.Matches(
                view,
                "controls:PolicyHelp.Text=\"{x:Static pvm:PolicyEditorHelp.RuleId}\"")
                .Count);
        Assert.Contains("Text=\"{x:Static pvm:PolicyEditorHelp.PolicyId}\"", view);
        Assert.Contains("Text=\"{x:Static pvm:PolicyEditorHelp.RuleId}\"", view);
        Assert.Contains("TextWrapping=\"Wrap\"", view);
    }

    [Fact]
    public void AuditMode_IsCollapsedAdvancedTwoStateSettingWithAccessibleWarning()
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
        XNamespace controls = "using:UniGetUI.Avalonia.Views.Controls";
        XNamespace automation =
            "clr-namespace:Avalonia.Automation;assembly=Avalonia.Controls";
        XElement auditSelector = Assert.Single(dialog.Descendants(),
            element => (string?)element.Attribute("Tag") == "/Enforcement/AuditMode");
        XElement defaultSelector = Assert.Single(dialog.Descendants(),
            element => (string?)element.Attribute("Tag") == "/Enforcement/DefaultDecision");
        XElement advanced = Assert.Single(auditSelector.Ancestors(),
            element => element.Name.LocalName == "Expander");

        Assert.Equal(
            "{Binding Document.HasEnforcementAdvisory}",
            (string?)advanced.Attribute("IsExpanded"));
        Assert.Contains(
            advanced.Descendants(),
            element => (string?)element.Attribute("Text")
                == "{Binding Document.EnforcementAdvisoryCountText}");
        Assert.Contains(defaultSelector.Ancestors(), element => element == advanced);
        Assert.Equal(
            "{x:Static pvm:PolicyEditorEnumDisplay.AuditModeDisplayItems}",
            (string?)auditSelector.Attribute("ItemsSource"));
        Assert.Equal(
            "{x:Static pvm:PolicyEditorHelp.AuditMode}",
            (string?)auditSelector.Attribute(controls + "PolicyHelp.Text"));
        XElement warning = Assert.Single(dialog.Descendants(),
            element => (string?)element.Attribute("IsVisible")
                == "{Binding Document.IsAuditModeEnabled}");
        Assert.Equal(
            "{x:Static pvm:PolicyEditorHelp.AuditModeWarning}",
            (string?)warning.Attribute("Content"));
        Assert.Equal(
            "{StaticResource PolicyAdvisoryTemplate}",
            (string?)warning.Attribute("ContentTemplate"));
        Assert.Null(warning.Attribute(automation + "AutomationProperties.Name"));
        Assert.DoesNotContain(dialog.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && (string?)element.Attribute("Text")
                    == "{x:Static pvm:PolicyEditorHelp.AuditMode}");
        Assert.Equal(["No", "Yes"], PolicyEditorEnumDisplay.AuditModeDisplayItems);
        Assert.Contains("permits requests", PolicyEditorHelp.AuditMode, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("set No to enforce", PolicyEditorHelp.AuditMode, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("still permitted", PolicyEditorHelp.AuditModeWarning, StringComparison.OrdinalIgnoreCase);

        XElement advisoryTemplate = Assert.Single(dialog.Descendants(),
            element => (string?)element.Attribute(
                XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Key")
                == "PolicyAdvisoryTemplate");
        XElement advisoryBorder = Assert.Single(advisoryTemplate.Descendants(),
            element => ((string?)element.Attribute("Classes"))?
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("policy-advisory", StringComparer.Ordinal) is true);
        XElement advisoryIcon = Assert.Single(advisoryTemplate.Descendants(),
            element => element.Name.LocalName == "SvgIcon");
        XElement advisoryText = Assert.Single(advisoryTemplate.Descendants(),
            element => element.Name.LocalName == "TextBlock");
        Assert.Equal(
            "avares://UniGetUI/Assets/Symbols/warning_round.svg",
            (string?)advisoryIcon.Attribute("Path"));
        Assert.Equal(
            "Raw",
            (string?)advisoryIcon.Attribute(
                automation + "AutomationProperties.AccessibilityView"));
        Assert.Equal(
            "Polite",
            (string?)advisoryBorder.Attribute(
                automation + "AutomationProperties.LiveSetting"));
        Assert.Equal("Wrap", (string?)advisoryText.Attribute("TextWrapping"));

        XElement advisoryStyle = Assert.Single(dialog.Descendants(),
            element => (string?)element.Attribute("Selector")
                == "Border.policy-advisory");
        string styleText = advisoryStyle.ToString();
        Assert.Contains("{DynamicResource WarningBannerBackground}", styleText);
        Assert.Contains("{DynamicResource WarningBannerBorderBrush}", styleText);
        Assert.Contains("BorderThickness", styleText);
        Assert.Contains("CornerRadius", styleText);

        string confirmationPrompt = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UniGetUI.Avalonia",
            "Views",
            "Pages",
            "SettingsPages",
            "PolicyEditor",
            "PolicyEditorConfirmationPrompt.cs"));
        Assert.Contains("PolicyEditorConfirmationKind.EnableAuditMode", confirmationPrompt);
        Assert.Contains("AccessibilityAnnouncementService.Announce", confirmationPrompt);
        Assert.Contains("requests the policy would deny will be permitted", confirmationPrompt);
        Assert.Contains("PolicyEditorConfirmationKind.EnableDefaultAllow", confirmationPrompt);
        Assert.Contains("PolicyEditorConfirmationKind.RemoveAllowSafetyLimits", confirmationPrompt);
    }

    [Fact]
    public void PolicyInspector_DoesNotExposeInvalidPolicyRepairAction()
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
        string viewModel = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UniGetUI.Avalonia",
            "ViewModels",
            "Pages",
            "SettingsPages",
            "AgentPolicyInspectorViewModel.cs"));

        Assert.DoesNotContain("RepairPolicyButton", view);
        Assert.DoesNotContain("RepairPolicyCommand", view);
        Assert.DoesNotContain("CanRepair", view);
        Assert.DoesNotContain("CanRepair", viewModel);
        Assert.Contains("outside UniGetUI", viewModel);
    }

    [Fact]
    public void ValidityWindow_UsesAccessibleLocalDateTimeControlsAndSharedAdvisory()
    {
        string root = FindRepositoryRoot();
        string view = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UniGetUI.Avalonia",
            "Views",
            "Pages",
            "SettingsPages",
            "PolicyEditor",
            "PolicyEditorDialog.axaml"));

        Assert.Contains("<WrapPanel Orientation=\"Horizontal\"", view);
        Assert.Contains("SelectedDate=\"{Binding Document.ValidFromDate, Mode=TwoWay}\"", view);
        Assert.Contains("SelectedTime=\"{Binding Document.ValidFromTime, Mode=TwoWay}\"", view);
        Assert.Contains("SelectedDate=\"{Binding Document.ValidUntilDate, Mode=TwoWay}\"", view);
        Assert.Contains("SelectedTime=\"{Binding Document.ValidUntilTime, Mode=TwoWay}\"", view);
        Assert.Equal(2, Regex.Matches(view, "MinWidth=\"220\"").Count);
        Assert.Equal(2, Regex.Matches(view, "MinWidth=\"150\"").Count);
        Assert.Equal(4, Regex.Matches(view, "Margin=\"0,0,12,8\"").Count);
        Assert.Contains("Text=\"{Binding Document.LocalTimeZoneText}\"", view);
        Assert.Contains("IsVisible=\"{Binding Document.IsOutsideValidityWindow}\"", view);
        Assert.Contains("ContentTemplate=\"{StaticResource PolicyAdvisoryTemplate}\"", view);
        Assert.Contains("Command=\"{Binding Document.ClearValidFromCommand}\"", view);
        Assert.Contains("Command=\"{Binding Document.ClearValidUntilCommand}\"", view);
        Assert.DoesNotContain("Click=\"ClearValidFromButton_Click\"", view);
        Assert.DoesNotContain("Click=\"ClearValidUntilButton_Click\"", view);
        Assert.DoesNotContain("Text=\"{Binding Document.ValidFromText}\"", view);
        Assert.DoesNotContain("Text=\"{Binding Document.ValidUntilText}\"", view);
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
            element => (string?)element.Attribute("Tag") == "/PolicyFormatVersion");
        Assert.Equal("TextBlock", format.Name.LocalName);
        Assert.Equal(
            "{Binding Document.PolicyFormatVersion}",
            (string?)format.Attribute("Text"));
        Assert.DoesNotContain(editor.Descendants(),
            element => (string?)element.Attribute("Tag") == "/Rules/*/Priority");
        Assert.Contains(editor.Descendants(),
            element => (string?)element.Attribute("Tag") == "/Rules/*/Match/PackageIdentifiers/Exact");
        Assert.Contains(editor.Descendants(),
            element => (string?)element.Attribute("Tag") == "/Rules/*/Match/Version/Exact");
        Assert.Contains(editor.Descendants(),
            element => (string?)element.Attribute("Tag") == "/Rules/*/Match/PackageIdentifiers/Patterns");
        Assert.Contains(editor.Descendants(),
            element => (string?)element.Attribute("Tag") == "/Rules/*/Match/Version/Range/MinVersion");
        Assert.Contains(editor.Descendants(),
            element => (string?)element.Attribute("Tag") == "/Rules/*/Match/ExecutionElevation");
        Assert.Contains(editor.Descendants(),
            element => (string?)element.Attribute("Tag") == "/Rules/*/Match/SourceNames");
        Assert.DoesNotContain(editor.Descendants(),
            element => (string?)element.Attribute("Tag") == "/Rules/*/Match/PackageNames");
        Assert.DoesNotContain(editor.Descendants(),
            element => (string?)element.Attribute("Tag") == "/Rules/*/Match/Sources");
        Assert.DoesNotContain(editor.Descendants(),
            element => (string?)element.Attribute("Tag") == "/Rules/*/Match/Versions");
        Assert.DoesNotContain(editor.Descendants(),
            element => (string?)element.Attribute("Tag") == "/Rules/*/Match/Elevation");
        Assert.DoesNotContain(editor.Descendants(),
            element => (string?)element.Attribute("Tag") == "/Enforcement/RulePrecedence");
        Assert.Contains(editor.Descendants(),
            element => (string?)element.Attribute("Click") == "FindingNavigateButton_Click");
        Assert.Contains(editor.Descendants(),
            element => (string?)element.Attribute("Click") == "RawSyntaxNavigateButton_Click");

        Assert.True(
            UniGetUI.Avalonia.Views.Pages.SettingsPages.PolicyEditor.PolicyEditorDialog
                .TryGetRuleIndex("/Rules/3/Match/Version/Exact/1", out int ruleIndex));
        Assert.Equal(3, ruleIndex);
        string normalized =
            UniGetUI.Avalonia.Views.Pages.SettingsPages.PolicyEditor.PolicyEditorDialog
                .NormalizeRulePointer("/Rules/3/Match/Version/Exact/1");
        Assert.Equal("/Rules/*/Match/Version/Exact/1", normalized);
        Assert.True(
            UniGetUI.Avalonia.Views.Pages.SettingsPages.PolicyEditor.PolicyEditorDialog
                .PointerTargetsTag(normalized, "/Rules/*/Match/Version/Exact"));
        string normalizedRuleId =
            UniGetUI.Avalonia.Views.Pages.SettingsPages.PolicyEditor.PolicyEditorDialog
                .NormalizeRulePointer("/Rules/0/Id");
        Assert.Equal("/Rules/*/Id", normalizedRuleId);
        Assert.True(
            UniGetUI.Avalonia.Views.Pages.SettingsPages.PolicyEditor.PolicyEditorDialog
                .PointerTargetsTag(normalizedRuleId, "/Rules/*/Id"));
    }

    [Fact]
    public void UnifiedPolicyPage_HasOneStatusRefreshAndActiveDetailsSection()
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
            element => (string?)element.Attribute(x + "Name") == "ActivePolicyDetailsSection");

        Assert.Equal(
            "{Binding HasActivePolicyDetails}",
            (string?)section.Attribute("IsVisible"));
        Assert.DoesNotContain(section.Descendants(),
            element => (string?)element.Attribute("Command") == "{Binding RefreshCommand}");
        Assert.DoesNotContain(section.Descendants(),
            element => (string?)element.Attribute("DataContext") == "{Binding Status}");
        Assert.Contains(section.Descendants(),
            element => (string?)element.Attribute("IsVisible") == "{Binding HasPolicy}");
        Assert.Contains(section.Descendants(),
            element => (string?)element.Attribute("Text") == "{Binding RawJson}");

        XElement refresh = Assert.Single(view.Descendants(),
            element => (string?)element.Attribute("Command") == "{Binding RefreshPageCommand}");
        XElement header = Assert.Single(view.Descendants(),
            element => (string?)element.Attribute(x + "Name") == "PolicyPageHeader");
        XElement heading = Assert.Single(view.Descendants(),
            element => (string?)element.Attribute(x + "Name") == "PolicyManagementHeading");
        Assert.Same(header, refresh.Parent);
        Assert.Same(header, heading.Parent);
        Assert.Equal("40,0,40,12", (string?)header.Attribute("Margin"));
        Assert.Equal("Center", (string?)heading.Attribute("VerticalAlignment"));
        Assert.Equal("Center", (string?)refresh.Attribute("VerticalAlignment"));
        Assert.DoesNotContain(refresh.Ancestors(), element => element == section);
        Assert.Null(refresh.Attribute("IsEnabled"));
        Assert.Single(view.Descendants(),
            element => (string?)element.Attribute("Content") == "{t:Translate Refresh}");
        XElement status = Assert.Single(view.Descendants(),
            element => element.Name.LocalName == "InfoBar"
                && (string?)element.Attribute("DataContext") == "{Binding ManagementStatus}");
        Assert.Equal("40,0,40,12", (string?)status.Attribute("Margin"));
        Assert.DoesNotContain(
            view.Root!.DescendantsAndSelf().Attributes("Margin"),
            margin => margin.Value.StartsWith("44,", StringComparison.Ordinal));
        Assert.Contains(view.Descendants(),
            element => ((string?)element.Attribute("Classes"))?
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("policy-section-heading", StringComparer.Ordinal) is true);
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
                && (string?)element.Attribute("DataContext") == "{Binding ManagementStatus}")
            .ToArray();

        Assert.Single(statuses);
        Assert.All(
            statuses,
            status => Assert.Null(
                status.Attribute(automation + "AutomationProperties.LiveSetting")));
        Assert.Empty(inspector.Root!
            .DescendantsAndSelf()
            .Attributes(automation + "AutomationProperties.LiveSetting"));
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

    private static IEnumerable<string> AllEnumOptionHelpTexts(bool includeManagers = true)
    {
        static IEnumerable<string> Build<TEnum>() where TEnum : struct, Enum =>
            PolicyEditorEnumOptionFactory.Build(new List<TEnum>(), () => { })
                .Select(option => option.HelpText);

        IEnumerable<string> help = Build<Devolutions.Now.Policy.Model.Operation>();
        if (includeManagers)
            help = help.Concat(Build<Devolutions.Now.Policy.Model.ManagerName>());

        return help
            .Concat(Build<Devolutions.Now.Policy.Model.Scope>())
            .Concat(Build<Devolutions.Now.Policy.Model.Architecture>())
            .Concat(Build<Devolutions.Now.Policy.Model.Elevation>());
    }

    [GeneratedRegex("CoreTools\\.Translate\\(\\s*\"(?<key>(?:\\\\.|[^\"\\\\])*)")]
    private static partial Regex CSharpTranslateRegex();

    [GeneratedRegex("\\{t:Translate\\s+(?<key>[^}\\r\\n]+)\\}")]
    private static partial Regex AxamlTranslateRegex();
}
