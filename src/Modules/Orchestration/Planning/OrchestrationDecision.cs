using System.Collections.ObjectModel;
using System.Text.Json;

namespace Ali.Modules.Orchestration.Planning;

public enum OrchestrationWorkStatus
{
    Pending,
    Active,
    Satisfied,
    Impossible,
    Superseded
}

public enum MaterialClaimKind
{
    CurrentFact,
    PerformedAction,
    Artifact,
    Permission,
    Code,
    File,
    Completion,
    Other
}

public enum CompletionKind
{
    Succeeded,
    ProvenImpossible
}

public sealed record OrchestrationWorkItemUpdate
{
    public OrchestrationWorkItemUpdate(
        string workItemId,
        string outcome,
        OrchestrationWorkStatus status,
        string? parentId = null,
        string? supersededById = null,
        IEnumerable<string>? dependencyIds = null,
        IEnumerable<string>? evidenceIds = null)
    {
        WorkItemId = workItemId ?? string.Empty;
        Outcome = outcome ?? string.Empty;
        Status = status;
        ParentId = string.IsNullOrWhiteSpace(parentId) ? null : parentId;
        SupersededById = string.IsNullOrWhiteSpace(supersededById) ? null : supersededById;
        DependencyIds = PlanningSnapshots.Strings(dependencyIds);
        EvidenceIds = PlanningSnapshots.Strings(evidenceIds);
    }

    public string WorkItemId { get; }

    public string Outcome { get; }

    public OrchestrationWorkStatus Status { get; }

    public string? ParentId { get; }

    public string? SupersededById { get; }

    public IReadOnlyList<string> DependencyIds { get; }

    public IReadOnlyList<string> EvidenceIds { get; }
}

public sealed record OrchestrationWorkUpdate
{
    public OrchestrationWorkUpdate(
        long baseRevision,
        IEnumerable<OrchestrationWorkItemUpdate>? items)
    {
        BaseRevision = baseRevision;
        Items = PlanningSnapshots.Items(items);
    }

    public long BaseRevision { get; }

    public IReadOnlyList<OrchestrationWorkItemUpdate> Items { get; }
}

public sealed record OrchestrationMaterialClaim
{
    public OrchestrationMaterialClaim(
        string claimId,
        string statement,
        MaterialClaimKind kind,
        IEnumerable<string>? evidenceIds = null)
    {
        ClaimId = claimId ?? string.Empty;
        Statement = statement ?? string.Empty;
        Kind = kind;
        EvidenceIds = PlanningSnapshots.Strings(evidenceIds);
    }

    public string ClaimId { get; }

    public string Statement { get; }

    public MaterialClaimKind Kind { get; }

    public IReadOnlyList<string> EvidenceIds { get; }
}

public sealed record CompletionEvidenceBinding
{
    public CompletionEvidenceBinding(string subjectId, IEnumerable<string>? evidenceIds)
    {
        SubjectId = subjectId ?? string.Empty;
        EvidenceIds = PlanningSnapshots.Strings(evidenceIds);
    }

    public string SubjectId { get; }

    public IReadOnlyList<string> EvidenceIds { get; }
}

public sealed record CompletionPlan
{
    public CompletionPlan(
        string answerId,
        CompletionKind completionKind,
        IEnumerable<string>? requiredOutcomeIds,
        IEnumerable<string>? requiredClaimIds,
        IEnumerable<CompletionEvidenceBinding>? bindings,
        string? requestedFormat = null,
        IEnumerable<string>? requestedSections = null)
    {
        AnswerId = answerId ?? string.Empty;
        CompletionKind = completionKind;
        RequiredOutcomeIds = PlanningSnapshots.Strings(requiredOutcomeIds);
        RequiredClaimIds = PlanningSnapshots.Strings(requiredClaimIds);
        Bindings = PlanningSnapshots.Items(bindings);
        RequestedFormat = requestedFormat?.Trim() ?? string.Empty;
        RequestedSections = PlanningSnapshots.Strings(requestedSections);
    }

    public string AnswerId { get; }

    public CompletionKind CompletionKind { get; }

    public IReadOnlyList<string> RequiredOutcomeIds { get; }

    public IReadOnlyList<string> RequiredClaimIds { get; }

    public IReadOnlyList<CompletionEvidenceBinding> Bindings { get; }

    public string RequestedFormat { get; }

    public IReadOnlyList<string> RequestedSections { get; }
}

public abstract record OrchestrationNextAction;

public sealed record CallToolAction : OrchestrationNextAction
{
    public CallToolAction(
        string toolName,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        string need,
        string expectedProgress)
    {
        ToolName = toolName ?? string.Empty;
        Arguments = PlanningSnapshots.Arguments(arguments);
        Need = need ?? string.Empty;
        ExpectedProgress = expectedProgress ?? string.Empty;
    }

    public string ToolName { get; }

    public IReadOnlyDictionary<string, JsonElement> Arguments { get; }

    public string Need { get; }

    public string ExpectedProgress { get; }
}

public sealed record ExpandToolsAction : OrchestrationNextAction
{
    public ExpandToolsAction(string need)
    {
        Need = need ?? string.Empty;
    }

    public string Need { get; }
}

public sealed record InspectEvidencePageAction : OrchestrationNextAction
{
    public InspectEvidencePageAction(long afterCursor, long? snapshotCursor, int pageSize)
    {
        AfterCursor = afterCursor;
        SnapshotCursor = snapshotCursor;
        PageSize = pageSize;
    }

    public long AfterCursor { get; }

    public long? SnapshotCursor { get; }

    public int PageSize { get; }
}

public sealed record InspectWorkPageAction : OrchestrationNextAction
{
    public InspectWorkPageAction(string? afterWorkItemId, long? snapshotRevision, int pageSize)
    {
        AfterWorkItemId = string.IsNullOrWhiteSpace(afterWorkItemId)
            ? null
            : afterWorkItemId;
        SnapshotRevision = snapshotRevision;
        PageSize = pageSize;
    }

    public string? AfterWorkItemId { get; }

    public long? SnapshotRevision { get; }

    public int PageSize { get; }
}

public sealed record AnswerDirectlyAction : OrchestrationNextAction
{
    public AnswerDirectlyAction(string answer)
    {
        Answer = answer ?? string.Empty;
    }

    public string Answer { get; }
}

public sealed record RequestUserInputAction : OrchestrationNextAction
{
    public RequestUserInputAction(string question, string missingInformation)
    {
        Question = question ?? string.Empty;
        MissingInformation = missingInformation ?? string.Empty;
    }

    public string Question { get; }

    public string MissingInformation { get; }
}

public sealed record AwaitExternalEventAction : OrchestrationNextAction
{
    public AwaitExternalEventAction(string ticketId, string waitingFor)
    {
        TicketId = ticketId ?? string.Empty;
        WaitingFor = waitingFor ?? string.Empty;
    }

    public string TicketId { get; }

    public string WaitingFor { get; }
}

public sealed record BeginCompletionAction : OrchestrationNextAction
{
    public BeginCompletionAction(CompletionPlan plan)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
    }

    public CompletionPlan Plan { get; }
}

public sealed record OrchestrationDecision
{
    public OrchestrationDecision(
        OrchestrationWorkUpdate? workUpdate,
        IEnumerable<OrchestrationMaterialClaim>? materialClaims,
        OrchestrationNextAction nextAction)
    {
        WorkUpdate = workUpdate;
        MaterialClaims = PlanningSnapshots.Items(materialClaims);
        NextAction = nextAction ?? throw new ArgumentNullException(nameof(nextAction));
    }

    public OrchestrationWorkUpdate? WorkUpdate { get; }

    public IReadOnlyList<OrchestrationMaterialClaim> MaterialClaims { get; }

    public OrchestrationNextAction NextAction { get; }
}

internal static class PlanningSnapshots
{
    internal static IReadOnlyList<string> Strings(IEnumerable<string>? source) =>
        Array.AsReadOnly((source ?? []).Select(value => value ?? string.Empty).ToArray());

    internal static IReadOnlyList<T> Items<T>(IEnumerable<T>? source) =>
        Array.AsReadOnly((source ?? []).ToArray());

    internal static IReadOnlyDictionary<string, JsonElement> Arguments(
        IReadOnlyDictionary<string, JsonElement>? source)
    {
        var copy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (source is not null)
        {
            foreach (var pair in source)
            {
                copy[pair.Key] = pair.Value.Clone();
            }
        }

        return new ReadOnlyDictionary<string, JsonElement>(copy);
    }
}
