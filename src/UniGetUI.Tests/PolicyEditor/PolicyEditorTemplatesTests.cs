using Devolutions.Now.Policy.Model;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

namespace UniGetUI.Tests.PolicyEditor;

public class PolicyEditorTemplatesTests
{
    [Fact]
    public void CreateNew_FixesSchemaTypeVersionAndPrecedence()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");

        Assert.Equal(PolicyEditorPolicyContract.Schema, draft.Schema);
        Assert.Equal(PolicyEditorPolicyContract.PolicyType, draft.PolicyType);
        Assert.Equal(PolicyEditorPolicyContract.InitialPolicyVersion, draft.PolicyVersion);
        Assert.Equal(RulePrecedence.PriorityThenDeny, draft.Enforcement.RulePrecedence);
    }

    [Fact]
    public void CreateNew_DefaultsToDenyAndNoRules()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");

        Assert.Equal(Decision.Deny, draft.Enforcement.DefaultDecision);
        Assert.Empty(draft.Rules);
    }

    [Fact]
    public void CreateNew_UsesSuppliedIdAndPublisher()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("my-id", "My Publisher");

        Assert.Equal("my-id", draft.Metadata.Id);
        Assert.Equal("My Publisher", draft.Metadata.Publisher);
    }

    [Fact]
    public void CreateNew_HasNoValidityWindowOrDescriptionByDefault()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");

        Assert.Null(draft.Metadata.ValidFrom);
        Assert.Null(draft.Metadata.ValidUntil);
        Assert.Null(draft.Metadata.Description);
        Assert.Null(draft.Metadata.SupportUrl);
        Assert.Null(draft.Enforcement.AuditMode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateNew_RejectsEmptyId(string? id)
    {
        Assert.Throws<ArgumentException>(() => PolicyEditorTemplates.CreateNew(id!, "Contoso"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateNew_RejectsEmptyPublisher(string? publisher)
    {
        Assert.Throws<ArgumentException>(() => PolicyEditorTemplates.CreateNew("id-1", publisher!));
    }

    [Fact]
    public void CreateNew_DoesNotExposeRevisionOrPublishedAt()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");

        // PolicyEditorDraftMetadata deliberately has no Revision/PublishedAt members; this test
        // documents that contract by asserting the type surface, via reflection, excludes them.
        System.Reflection.PropertyInfo[] props = draft.Metadata.GetType().GetProperties();
        Assert.DoesNotContain(props, p => p.Name == "Revision");
        Assert.DoesNotContain(props, p => p.Name == "PublishedAt");
    }
}
