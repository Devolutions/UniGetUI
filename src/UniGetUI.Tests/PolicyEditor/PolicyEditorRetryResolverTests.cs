using Devolutions.Now.Policy.Api;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

namespace UniGetUI.Tests.PolicyEditor;

public class PolicyEditorRetryResolverTests
{
    private static PolicyManagementSnapshot Active(string policyId, string token) =>
        PolicyEditorTestFixtures.BuildActiveManagement(
            PolicyEditorTestFixtures.BuildDocument(id: policyId),
            token);

    [Fact]
    public void Resolve_ActiveSameIdentity_ProducesUpdateWithExactToken()
    {
        PolicyManagementSnapshot management = Active("local-id", "etag-123");

        PolicyEditorRetryDecision decision = PolicyEditorRetryResolver.Resolve("local-id", management);

        Assert.Equal(PolicyReplacementOperation.Update, decision.Operation);
        Assert.Equal("etag-123", decision.Token);
        Assert.Equal("local-id", decision.ActivePolicyId);
    }

    [Fact]
    public void Resolve_ActiveDifferentIdentity_ProducesReplaceIdentityWithExactToken()
    {
        PolicyManagementSnapshot management = Active("other-id", "etag-999");

        PolicyEditorRetryDecision decision = PolicyEditorRetryResolver.Resolve("local-id", management);

        Assert.Equal(PolicyReplacementOperation.ReplaceIdentity, decision.Operation);
        Assert.Equal("etag-999", decision.Token);
        Assert.Equal("other-id", decision.ActivePolicyId);
    }

    [Fact]
    public void Resolve_Missing_ProducesCreateWithNullActivePolicyId()
    {
        PolicyManagementSnapshot management = PolicyEditorTestFixtures.BuildMissingManagement("etag-missing");

        PolicyEditorRetryDecision decision = PolicyEditorRetryResolver.Resolve("local-id", management);

        Assert.Equal(PolicyReplacementOperation.Create, decision.Operation);
        Assert.Equal("etag-missing", decision.Token);
        Assert.Null(decision.ActivePolicyId);
    }

    [Fact]
    public void Resolve_Invalid_ProducesRepairWithExactToken()
    {
        PolicyManagementSnapshot management = PolicyEditorTestFixtures.BuildInvalidManagement("etag-broken");

        PolicyEditorRetryDecision decision = PolicyEditorRetryResolver.Resolve("local-id", management);

        Assert.Equal(PolicyReplacementOperation.Repair, decision.Operation);
        Assert.Equal("etag-broken", decision.Token);
        Assert.Null(decision.ActivePolicyId);
    }

    [Fact]
    public void Resolve_NullManagement_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PolicyEditorRetryResolver.Resolve("local-id", null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_EmptyDraftId_Throws(string draftId)
    {
        PolicyManagementSnapshot management = Active("local-id", "etag-1");
        Assert.Throws<ArgumentException>(() => PolicyEditorRetryResolver.Resolve(draftId, management));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_EmptyStoreToken_Throws(string token)
    {
        PolicyManagementSnapshot management = Active("local-id", "placeholder");
        management.StoreToken = token;
        Assert.Throws<ArgumentException>(() => PolicyEditorRetryResolver.Resolve("local-id", management));
    }

    [Fact]
    public void RequiresFreshConfirmation_NoExistingConfirmation_IsTrue()
    {
        var decision = new PolicyEditorRetryDecision(
            PolicyReplacementOperation.Create,
            "token",
            PolicyManagementState.Missing,
            null);

        Assert.True(PolicyEditorRetryResolver.RequiresFreshConfirmation(null, decision, "local-id"));
    }

    [Fact]
    public void RequiresFreshConfirmation_ExactMatchingConfirmation_IsFalse()
    {
        var decision = new PolicyEditorRetryDecision(
            PolicyReplacementOperation.Update,
            "etag-1",
            PolicyManagementState.Active,
            "local-id");
        PolicyEditorConfirmationContext granted = PolicyEditorConfirmationContext.For(decision, "local-id");

        Assert.False(PolicyEditorRetryResolver.RequiresFreshConfirmation(granted, decision, "local-id"));
    }

    [Fact]
    public void RequiresFreshConfirmation_TokenChangedSinceGrant_IsTrue_NoBlindForce()
    {
        var firstDecision = new PolicyEditorRetryDecision(
            PolicyReplacementOperation.Update,
            "etag-1",
            PolicyManagementState.Active,
            "local-id");
        PolicyEditorConfirmationContext granted = PolicyEditorConfirmationContext.For(firstDecision, "local-id");

        var secondDecision = firstDecision with { Token = "etag-2" };

        Assert.True(PolicyEditorRetryResolver.RequiresFreshConfirmation(granted, secondDecision, "local-id"));
    }

    [Fact]
    public void RequiresFreshConfirmation_OperationChangedSinceGrant_IsTrue()
    {
        var firstDecision = new PolicyEditorRetryDecision(
            PolicyReplacementOperation.Update,
            "etag-1",
            PolicyManagementState.Active,
            "local-id");
        PolicyEditorConfirmationContext granted = PolicyEditorConfirmationContext.For(firstDecision, "local-id");

        var secondDecision = firstDecision with { Operation = PolicyReplacementOperation.Repair };

        Assert.True(PolicyEditorRetryResolver.RequiresFreshConfirmation(granted, secondDecision, "local-id"));
    }

    [Fact]
    public void RequiresFreshConfirmation_IdentityChangedSinceGrant_IsTrue()
    {
        var decision = new PolicyEditorRetryDecision(
            PolicyReplacementOperation.ReplaceIdentity,
            "etag-1",
            PolicyManagementState.Active,
            "old-remote-id");
        PolicyEditorConfirmationContext granted = PolicyEditorConfirmationContext.For(decision, "old-local-id");

        Assert.True(PolicyEditorRetryResolver.RequiresFreshConfirmation(granted, decision, "new-local-id"));
    }
}
