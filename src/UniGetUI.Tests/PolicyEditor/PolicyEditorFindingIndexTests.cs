using System.Text.Json;
using Devolutions.Now.Policy.Api;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

namespace UniGetUI.Tests.PolicyEditor;

public class PolicyEditorFindingIndexTests
{
    private static PolicyValidationFinding Finding(string pointer, string? ruleId, string message = "message") =>
        new(pointer, ruleId, PolicyValidationSeverity.Warning, message);

    [Fact]
    public void Build_Empty_ProducesEmptyLookupsNotThrowing()
    {
        PolicyEditorFindingIndex index = PolicyEditorFindingIndex.Build([]);

        Assert.Empty(index.All);
        Assert.Empty(index.ForPointer("/anything"));
        Assert.Empty(index.ForRule("any-rule"));
    }

    [Fact]
    public void ForPointer_ReturnsAllFindingsAtThatExactPointer()
    {
        PolicyValidationFinding a = Finding("/rules/0/priority", "rule-a", "priority too low");
        PolicyValidationFinding b = Finding("/rules/0/priority", "rule-a", "priority collides");
        PolicyValidationFinding c = Finding("/rules/1/priority", "rule-b");

        PolicyEditorFindingIndex index = PolicyEditorFindingIndex.Build([a, b, c]);

        Assert.Equal([a, b], index.ForPointer("/rules/0/priority"));
        Assert.Equal([c], index.ForPointer("/rules/1/priority"));
        Assert.Empty(index.ForPointer("/rules/2/priority"));
    }

    [Fact]
    public void ForRule_ReturnsAllFindingsForThatRuleId_RegardlessOfPointer()
    {
        PolicyValidationFinding a = Finding("/rules/0/priority", "rule-a");
        PolicyValidationFinding b = Finding("/rules/0/reason", "rule-a");
        PolicyValidationFinding c = Finding("/rules/1/priority", "rule-b");

        PolicyEditorFindingIndex index = PolicyEditorFindingIndex.Build([a, b, c]);

        Assert.Equal([a, b], index.ForRule("rule-a"));
        Assert.Equal([c], index.ForRule("rule-b"));
    }

    [Fact]
    public void ForRule_FindingsWithoutRuleId_AreExcludedFromRuleLookupButPresentInAll()
    {
        PolicyValidationFinding documentLevel = Finding("/enforcement/defaultDecision", null);

        PolicyEditorFindingIndex index = PolicyEditorFindingIndex.Build([documentLevel]);

        Assert.Contains(documentLevel, index.All);
        Assert.Empty(index.ForRule("rule-a"));
        Assert.Equal([documentLevel], index.ForPointer("/enforcement/defaultDecision"));
    }

    [Fact]
    public void All_PreservesInputOrder()
    {
        PolicyValidationFinding a = Finding("/a", null);
        PolicyValidationFinding b = Finding("/b", null);
        PolicyValidationFinding c = Finding("/c", null);

        PolicyEditorFindingIndex index = PolicyEditorFindingIndex.Build([a, b, c]);

        Assert.Equal([a, b, c], index.All);
    }

    [Fact]
    public void RecognizedFinding_UsesLocalizedCodeInsteadOfAgentMessage()
    {
        var shared = new PolicyFinding
        {
            Severity = PolicyFindingSeverity.Warning,
            Code = PolicyFindingCode.DefaultAllow,
            Path = "/Enforcement/DefaultDecision",
            Message = "server-controlled English must not be displayed",
        };

        PolicyValidationFinding finding = PolicyValidationFinding.FromShared(shared);

        Assert.Equal(
            "The default decision is Allow; requests matching no rule are permitted.",
            finding.Message);
        Assert.DoesNotContain("server-controlled", finding.Message);
    }

    [Fact]
    public void SensitiveFinding_UsesStructuredOptionAndRestrictionArguments()
    {
        using JsonDocument option = JsonDocument.Parse("\"AllowCustomParameters\"");
        using JsonDocument restrictions = JsonDocument.Parse("[\"--silent\"]");
        var shared = new PolicyFinding
        {
            Severity = PolicyFindingSeverity.Warning,
            Code = PolicyFindingCode.SensitiveOptionAllowed,
            Path = "/Rules/0/Constraints/AllowCustomParameters",
            RuleId = "allow-tools",
            Message = "untrusted fallback",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["Option"] = option.RootElement.Clone(),
                ["AllowedCustomParameters"] = restrictions.RootElement.Clone(),
            },
        };

        PolicyValidationFinding finding = PolicyValidationFinding.FromShared(shared);

        Assert.Contains("custom command-line parameters", finding.Message);
        Assert.Contains("--silent", finding.Message);
        Assert.DoesNotContain("untrusted fallback", finding.Message);
    }

    [Fact]
    public void AgentControlledFindingLocations_AreControlFreeAndBounded()
    {
        var shared = new PolicyFinding
        {
            Severity = PolicyFindingSeverity.Error,
            Code = PolicyFindingCode.InvalidFieldValue,
            Path = "/" + new string('p', 3000) + "\r\nhidden",
            RuleId = new string('r', 3000) + "\0hidden",
            Message = "ignored",
        };

        PolicyValidationFinding finding = PolicyValidationFinding.FromShared(shared);

        Assert.Equal(2048, finding.Pointer.Length);
        Assert.Equal(2048, finding.RuleId!.Length);
        Assert.DoesNotContain('\r', finding.Pointer);
        Assert.DoesNotContain('\n', finding.Pointer);
        Assert.DoesNotContain('\0', finding.RuleId);
    }

    [Fact]
    public void ScalarTruncation_DoesNotSplitSupplementaryPlaneCharacters()
    {
        string input = new string('a', 2047) + "\U0001F680" + "discarded";

        string result = PolicyFindingPresentation.SanitizeAgentText(input, 2048);

        Assert.Equal(2048, result.EnumerateRunes().Count());
        Assert.EndsWith("\U0001F680", result, StringComparison.Ordinal);
        Assert.True(char.IsSurrogatePair(result, result.Length - 2));
    }

    [Fact]
    public void Build_CapsThousandsOfFindingsAndAddsLocalizedOmissionRecord()
    {
        PolicyValidationFinding[] findings = Enumerable.Range(0, 5000)
            .Select(index => Finding($"/rules/{index}", $"rule-{index}"))
            .ToArray();

        PolicyEditorFindingIndex index = PolicyEditorFindingIndex.Build(findings);

        Assert.Equal(PolicyEditorFindingIndex.MaxDisplayedFindings, index.All.Count);
        Assert.True(index.FindingsTruncated);
        Assert.Equal(4801, index.OmittedFindingCount);
        Assert.Contains("4801", index.All[^1].Message);
        Assert.Empty(index.All[^1].Pointer);
    }

    [Fact]
    public void FromShared_CapsArgumentCountKeysValuesAndRenderedMessage()
    {
        var arguments = new Dictionary<string, JsonElement>();
        using JsonDocument oversized = JsonDocument.Parse($"\"{new string('x', 5000)}\"");
        for (int index = 0; index < 100; index++)
        {
            arguments[$"{index:D3}{new string('k', 500)}"] = oversized.RootElement.Clone();
        }

        var shared = new PolicyFinding
        {
            Severity = PolicyFindingSeverity.Warning,
            Code = PolicyFindingCode.SensitiveOptionAllowed,
            Path = new string('p', 5000),
            RuleId = new string('r', 5000),
            Message = new string('m', 5000),
            Arguments = arguments,
        };

        PolicyValidationFinding finding = PolicyValidationFinding.FromShared(shared);

        Assert.Equal(32, finding.Arguments!.Count);
        Assert.All(finding.Arguments, pair =>
        {
            Assert.True(pair.Key.EnumerateRunes().Count() <= 256);
            Assert.True(pair.Value.EnumerateRunes().Count() <= 256);
        });
        Assert.True(finding.Pointer.EnumerateRunes().Count() <= 2048);
        Assert.True(finding.RuleId!.EnumerateRunes().Count() <= 2048);
        Assert.True(finding.Message.EnumerateRunes().Count() <= 2048);
    }
}
