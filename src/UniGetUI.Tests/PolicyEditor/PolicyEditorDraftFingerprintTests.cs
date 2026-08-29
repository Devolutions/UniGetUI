using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

namespace UniGetUI.Tests.PolicyEditor;

public class PolicyEditorDraftFingerprintTests
{
    [Fact]
    public void Compute_SameContent_ProducesEqualFingerprints()
    {
        PolicyEditorDraftDocument a = PolicyEditorTemplates.CreateNew("id-1", "Contoso");
        PolicyEditorDraftDocument b = PolicyEditorTemplates.CreateNew("id-1", "Contoso");

        Assert.Equal(PolicyEditorDraftFingerprint.Compute(a), PolicyEditorDraftFingerprint.Compute(b));
        Assert.True(PolicyEditorDraftFingerprint.Compute(a) == PolicyEditorDraftFingerprint.Compute(b));
    }

    [Fact]
    public void Compute_DifferentContent_ProducesDifferentFingerprints()
    {
        PolicyEditorDraftDocument a = PolicyEditorTemplates.CreateNew("id-1", "Contoso");
        PolicyEditorDraftDocument b = PolicyEditorTemplates.CreateNew("id-2", "Contoso");

        Assert.NotEqual(PolicyEditorDraftFingerprint.Compute(a), PolicyEditorDraftFingerprint.Compute(b));
        Assert.True(PolicyEditorDraftFingerprint.Compute(a) != PolicyEditorDraftFingerprint.Compute(b));
    }

    [Fact]
    public void Compute_IsIndependentOfInstanceIdentity_OnlyContentMatters()
    {
        PolicyEditorDraftDocument original = PolicyEditorTemplates.CreateNew("id-1", "Contoso");
        PolicyEditorDraftDocument clone = original.Clone();

        Assert.Equal(PolicyEditorDraftFingerprint.Compute(original), PolicyEditorDraftFingerprint.Compute(clone));

        clone.Rules.Add(PolicyRuleFactory.CreateBlank("rule-a"));

        Assert.NotEqual(PolicyEditorDraftFingerprint.Compute(original), PolicyEditorDraftFingerprint.Compute(clone));
    }

    [Fact]
    public void Compute_IgnoresRevisionAndPublishedAtBookkeeping()
    {
        // The draft model has no Revision/PublishedAt fields at all, so the fingerprint can only ever
        // reflect editor-owned content; this test documents that guarantee end-to-end through the
        // mapper's placeholder substitution rather than asserting on private fields.
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");

        PolicyEditorDraftFingerprint viaDefaultPlaceholders = PolicyEditorDraftFingerprint.Compute(draft);

        // Independently mapping to a document with different revision/publishedAt values, then back to a
        // draft, must still fingerprint identically because the fingerprint recomputes its own placeholders.
        Devolutions.Now.Policy.Model.PolicyDocument withDifferentBookkeeping =
            PolicyEditorMapper.ToDocument(draft, revision: 42, publishedAt: DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        PolicyEditorDraftDocument redraft = PolicyEditorMapper.ToDraft(withDifferentBookkeeping);

        Assert.Equal(viaDefaultPlaceholders, PolicyEditorDraftFingerprint.Compute(redraft));
    }
}
