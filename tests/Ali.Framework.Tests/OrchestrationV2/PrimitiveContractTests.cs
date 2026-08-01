using Ali.Modules.Orchestration.Activity;
using Ali.Modules.Orchestration.Contracts;
using System.Text.Json;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class PrimitiveContractTests
{
    [Fact]
    public void TurnIdentity_UsesTheExactUserConversationAndAssistantMessageBoundary()
    {
        var identity = new TurnIdentity("user-a", "conversation", "message-1");

        Assert.Equal(new TurnIdentity("user-a", "conversation", "message-1"), identity);
        Assert.NotEqual(new TurnIdentity("user-b", "conversation", "message-1"), identity);
        Assert.NotEqual(new TurnIdentity("user-a", "other-conversation", "message-1"), identity);
        Assert.NotEqual(new TurnIdentity("user-a", "conversation", "message-2"), identity);
    }

    [Fact]
    public void TurnEventDraft_OwnsAnImmutableCloneOfModelIndependentData()
    {
        TurnEventDraft draft;
        using (var document = JsonDocument.Parse("{\"status\":\"accepted\"}"))
        {
            draft = new TurnEventDraft("decision-accepted", document.RootElement);
        }

        Assert.Equal("accepted", draft.Data.GetProperty("status").GetString());
    }

    [Fact]
    public void ReturnedSuccessFalse_RemainsACompletedInvocationWithAFailedDomainOutcome()
    {
        var outcome = ToolInvocationOutcome.Returned("redacted"u8, reportedSuccess: false);

        Assert.Equal(InvocationStatus.Returned, outcome.InvocationStatus);
        Assert.Equal(DomainOutcome.Failed, outcome.DomainOutcome);
    }

    [Fact]
    public void ExceptionFingerprint_DoesNotPersistTheExceptionMessage()
    {
        var outcome = ToolInvocationOutcome.Threw(new InvalidOperationException("secret detail"));

        Assert.Equal(InvocationStatus.Threw, outcome.InvocationStatus);
        Assert.Equal(DomainOutcome.Unreported, outcome.DomainOutcome);
        Assert.DoesNotContain("secret detail", System.Text.Json.JsonSerializer.Serialize(outcome), StringComparison.Ordinal);
    }

    [Fact]
    public void ActivityNarrator_RendersOnlyTheSuppliedTransitionDescriptions()
    {
        var rendered = ActivityNarrator.Render(
            new ActivityTransition("Inspected the project", "Apply the selected Roslyn action"));

        Assert.Equal("Inspected the project -> Apply the selected Roslyn action", rendered);
    }
}
