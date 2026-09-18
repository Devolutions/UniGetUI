using Avalonia.Automation;
using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Avalonia.Views.Pages.SettingsPages.PolicyEditor;

namespace UniGetUI.Tests.PolicyEditor;

public class PolicyEditorFocusNavigationTests
{
    [Fact]
    public void GroupedFindingTargetSelectsFirstEnabledFocusableDescendant()
    {
        var group = new StackPanel();
        group.Children.Add(new Button
        {
            IsEnabled = false,
            Focusable = true,
        });
        var expected = new CheckBox
        {
            IsEnabled = true,
            IsVisible = true,
            Focusable = true,
        };
        group.Children.Add(expected);

        Control? target = PolicyEditorDialog.FindFocusableTarget(group);

        Assert.Same(expected, target);
    }

    [Fact]
    public void DirectFocusableFindingTargetRemainsPreferred()
    {
        var expected = new TextBox
        {
            IsEnabled = true,
            IsVisible = true,
            Focusable = true,
        };
        expected.Text = "value";

        Control? target = PolicyEditorDialog.FindFocusableTarget(expected);

        Assert.Same(expected, target);
    }

    [Fact]
    public void FindingNavigationExpandsAllCollapsedAncestorSections()
    {
        var first = new Expander { IsExpanded = false };
        var second = new Expander { IsExpanded = true };

        bool changed = PolicyEditorDialog.ExpandCollapsedAncestors([first, second]);

        Assert.True(changed);
        Assert.True(first.IsExpanded);
        Assert.True(second.IsExpanded);
        Assert.False(PolicyEditorDialog.ExpandCollapsedAncestors([first, second]));
    }

    [Fact]
    public void WarningNavigationAppliesVisibleAccessibleTargetAssociation()
    {
        var target = new CheckBox();

        PolicyEditorDialog.HighlightWarningTarget(
            target,
            "Rule allow-tools allows skipping hash verification.");

        Assert.Contains("finding-warning-target", target.Classes);
        Assert.Equal(
            "Rule allow-tools allows skipping hash verification.",
            AutomationProperties.GetHelpText(target));
    }

    [Theory]
    [InlineData("/Rules/0/Match/SkipHashCheck", "SkipHashCheck")]
    [InlineData("/Rules/0/Constraints/AllowedCustomParameters", "AllowedCustomParameters")]
    [InlineData("/Rules/0", "Rules")]
    public void RawNavigationDerivesBestPropertyToken(
        string pointer,
        string expectedProperty)
    {
        Assert.Equal(
            expectedProperty,
            PolicyJsonEditor.LastPropertySegment(pointer));
    }

    [Fact]
    public void RawNavigationResolvesDocumentAndReorderedRulePropertiesExactly()
    {
        const string json =
            """
            {
              "Enforcement": { "AuditMode": true },
              "Rules": [
                {
                  "Constraints": { "AllowSkipHashCheck": true },
                  "Id": "first"
                },
                {
                  "Constraints": {
                    "AllowSkipHashCheck": true,
                    "AllowPrePostCommands": true
                  }
                }
              ]
            }
            """;

        Assert.True(PolicyJsonEditor.TryFindJsonPointerSelection(
            json,
            "/Enforcement/AuditMode",
            out int auditOffset,
            out int auditLength));
        Assert.Equal("\"AuditMode\"", json.Substring(auditOffset, auditLength));

        Assert.True(PolicyJsonEditor.TryFindJsonPointerSelection(
            json,
            "/Rules/1/Constraints/AllowPrePostCommands",
            out int commandOffset,
            out int commandLength));
        Assert.Equal(
            "\"AllowPrePostCommands\"",
            json.Substring(commandOffset, commandLength));
        Assert.True(
            commandOffset > json.IndexOf("\"first\"", StringComparison.Ordinal));

        Assert.True(PolicyJsonEditor.TryFindJsonPointerSelection(
            json,
            "/Rules/0/Constraints/AllowSkipHashCheck",
            out int hashOffset,
            out int hashLength));
        Assert.Equal(
            "\"AllowSkipHashCheck\"",
            json.Substring(hashOffset, hashLength));
        Assert.True(
            hashOffset < json.IndexOf("\"first\"", StringComparison.Ordinal));
        Assert.False(PolicyJsonEditor.TryFindJsonPointerSelection(
            json,
            "/Rules/1/Constraints/Missing",
            out _,
            out _));
    }

    [Fact]
    public void RawNavigationReturnsFalseForMalformedDocument()
    {
        Assert.False(PolicyJsonEditor.TryFindJsonPointerSelection(
            """{"Rules":[""",
            "/Rules/0",
            out _,
            out _));
    }

    [Fact]
    public void RawNavigationSelectsTerminalObjectAndScalarArrayElements()
    {
        const string json =
            """
            {
              "Rules": [
                {
                  "Match": {
                    "Package": {
                      "Identifiers": {
                        "Exact": [ "Contoso.App", "Contoso.Tool" ]
                      }
                    }
                  }
                }
              ]
            }
            """;

        Assert.True(PolicyJsonEditor.TryFindJsonPointerSelection(
            json,
            "/Rules/0",
            out int ruleOffset,
            out int ruleLength));
        Assert.Equal("{", json.Substring(ruleOffset, ruleLength));

        Assert.True(PolicyJsonEditor.TryFindJsonPointerSelection(
            json,
            "/Rules/0/Match/Package/Identifiers/Exact/1",
            out int identifierOffset,
            out int identifierLength));
        Assert.Equal(
            "\"Contoso.Tool\"",
            json.Substring(identifierOffset, identifierLength));
    }

    [Fact]
    public void MissingConstraintsTargetFallsBackToVisibleAddSafetyLimitsControl()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorTemplates.CreateNew("policy", "Contoso"));
        PolicyEditorDraftRule draftRule = PolicyRuleFactory.CreateBlank("allow-rule");
        draftRule.Decision = Devolutions.Now.Policy.Model.Decision.Allow;
        draftRule.Match.Operations.Add(
            Devolutions.Now.Policy.Model.Operation.Install);
        session.AddRule(draftRule);
        using var sessionViewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            new FakeConfirmationPrompt(),
            new FakeWriteClient());
        using var rule = new PolicyEditorRuleUi(draftRule, sessionViewModel);
        var finding = new PolicyValidationFinding(
            "/Rules/0/Match/SkipHashCheck",
            "allow-rule",
            PolicyValidationSeverity.Warning,
            "warning",
            Devolutions.Now.Policy.Api.PolicyFindingCode.SensitiveOptionAllowed,
            new Dictionary<string, string>
            {
                ["option"] = "\"SkipHashCheck\"",
            });

        Assert.Equal(
            "/Rules/0/Constraints",
            PolicyEditorDialog.GetVisibleNavigationPointer(finding, 0, rule));

        draftRule.Constraints = new PolicyEditorDraftConstraints();
        Assert.Equal(
            "/Rules/0/Constraints/AllowSkipHashCheck",
            PolicyEditorDialog.GetVisibleNavigationPointer(finding, 0, rule));
    }
}
