using Devolutions.Now.Policy.Model;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

namespace UniGetUI.Tests.PolicyEditor;

public class PolicyEditorTemplatesTests
{
    [Fact]
    public void CreateNew_FixesTypeFormatVersionAndPrecedence()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");

        Assert.Equal(PolicyEditorPolicyContract.PolicyType, draft.PolicyType);
        Assert.Equal(PolicyFormatVersion.Current, draft.PolicyFormatVersion);
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
    public void CreateNew_RejectsEmptyPublisher(string? publisher)
    {
        Assert.Throws<ArgumentException>(() => PolicyEditorTemplates.CreateNew("id-1", publisher!));
    }

    [Fact]
    public void CreateNew_PreservesWhitespaceOnlyPublisher()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", " ");

        Assert.Equal(" ", draft.Metadata.Publisher);
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

    [Fact]
    public void PolicyFormatVersion_IsStampedForNewDraftsAndReadOnlyInStructuredUi()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("policy", "Publisher");
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            draft);
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            new FakeConfirmationPrompt(),
            new FakeWriteClient());
        var document = new PolicyEditorDocumentUi(viewModel);

        Assert.Equal(PolicyFormatVersion.Current, draft.PolicyFormatVersion);
        Assert.Equal(draft.PolicyFormatVersion.Value, document.PolicyFormatVersion);
        Assert.False(
            typeof(PolicyEditorDocumentUi)
                .GetProperty(nameof(PolicyEditorDocumentUi.PolicyFormatVersion))!
                .CanWrite);
    }

    [Fact]
    public void StructuredUpdate_PreservesCompatibleExistingPolicyFormatVersion()
    {
        Devolutions.Now.Policy.Api.PolicyManagementSnapshot management =
            PolicyEditorTestFixtures.BuildActiveManagement();
        PolicyEditorSession session = PolicyEditorSession.StartUpdate(management);
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            new FakeConfirmationPrompt(),
            new FakeWriteClient());
        var document = new PolicyEditorDocumentUi(viewModel);

        Assert.Equal("1.2.3", viewModel.Draft.PolicyFormatVersion.Value);
        Assert.Equal("1.2.3", document.PolicyFormatVersion);
    }

    [Fact]
    public void CreateReplacementId_AppendsReadableSuffixWhenItFits()
    {
        Assert.Equal("active-policy-new", PolicyEditorTemplates.CreateReplacementId("active-policy"));
    }

    [Fact]
    public void CreateReplacementId_UsesTheFullLimitWithoutChangingTheValidPrefix()
    {
        string activeId = new('a', PolicyEditorTemplates.ResourceIdMaxLength - 4);

        string replacementId = PolicyEditorTemplates.CreateReplacementId(activeId);

        Assert.Equal(PolicyEditorTemplates.ResourceIdMaxLength, replacementId.Length);
        Assert.Equal($"{activeId}-new", replacementId);
        AssertValidReplacement(activeId, replacementId);
    }

    [Fact]
    public void CreateReplacementId_TruncatesAMaximumLengthIdentifier()
    {
        string activeId = new('a', PolicyEditorTemplates.ResourceIdMaxLength);

        string replacementId = PolicyEditorTemplates.CreateReplacementId(activeId);

        Assert.Equal($"{new string('a', PolicyEditorTemplates.ResourceIdMaxLength - 4)}-new", replacementId);
        AssertValidReplacement(activeId, replacementId);
    }

    [Fact]
    public void CreateReplacementId_MaximumLengthAlreadyEndingInSuffixStillChangesIdentity()
    {
        string activeId = $"{new string('a', PolicyEditorTemplates.ResourceIdMaxLength - 4)}-new";

        string replacementId = PolicyEditorTemplates.CreateReplacementId(activeId);

        Assert.Equal($"{activeId[..^1]}0", replacementId);
        AssertValidReplacement(activeId, replacementId);
    }

    private static void AssertValidReplacement(string activeId, string replacementId)
    {
        Assert.NotEqual(activeId, replacementId);
        Assert.InRange(replacementId.Length, 1, PolicyEditorTemplates.ResourceIdMaxLength);
        Assert.Matches("^[A-Za-z0-9][A-Za-z0-9._:\\-]{0,127}$", replacementId);
    }
}
