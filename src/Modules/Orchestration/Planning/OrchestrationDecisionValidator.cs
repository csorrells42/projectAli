using System.Text.Json;
using System.Collections.Immutable;
using Ali.Modules.Orchestration.Work;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Orchestration.Planning;

public sealed record OrchestrationValidationContext
{
    private static readonly IReadOnlySet<string> NoWorkItemIds =
        ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    public OrchestrationValidationContext(
        long stateRevision,
        IEnumerable<AIFunctionDeclaration>? selectedTools,
        IEnumerable<string>? acceptedEvidenceIds = null,
        IEnumerable<string>? knownWorkItemIds = null,
        IEnumerable<string>? approvedExternalTicketIds = null,
        IReadOnlyDictionary<string, PlanningToolDomainOutcome>? evidenceOutcomes = null,
        long? workGraphRevision = null,
        WorkGraphSnapshot? authoritativeWorkGraph = null,
        IReadOnlyDictionary<string, AcceptedEvidenceProjection>? evidenceProjections = null)
    {
        StateRevision = stateRevision;
        WorkGraphRevision = workGraphRevision ?? stateRevision;
        AuthoritativeWorkGraph = authoritativeWorkGraph;
        SelectedTools = (selectedTools ?? [])
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
            .GroupBy(tool => tool.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        AcceptedEvidenceIds = SnapshotSet(acceptedEvidenceIds);
        KnownWorkItemIds = authoritativeWorkGraph is null
            ? SnapshotSet(knownWorkItemIds)
            : NoWorkItemIds;
        ApprovedExternalTicketIds = SnapshotSet(approvedExternalTicketIds);
        EvidenceOutcomes = new Dictionary<string, PlanningToolDomainOutcome>(
            evidenceOutcomes ?? new Dictionary<string, PlanningToolDomainOutcome>(),
            StringComparer.Ordinal);
        EvidenceProjections = new Dictionary<string, AcceptedEvidenceProjection>(
            evidenceProjections ?? new Dictionary<string, AcceptedEvidenceProjection>(),
            StringComparer.Ordinal);
    }

    public long StateRevision { get; }

    public long WorkGraphRevision { get; }

    public WorkGraphSnapshot? AuthoritativeWorkGraph { get; }

    public IReadOnlyDictionary<string, AIFunctionDeclaration> SelectedTools { get; }

    public IReadOnlySet<string> AcceptedEvidenceIds { get; }

    public IReadOnlySet<string> KnownWorkItemIds { get; }

    public IReadOnlySet<string> ApprovedExternalTicketIds { get; }

    public IReadOnlyDictionary<string, PlanningToolDomainOutcome> EvidenceOutcomes { get; }

    public IReadOnlyDictionary<string, AcceptedEvidenceProjection> EvidenceProjections { get; }

    internal bool IsKnownWorkItem(string workItemId) =>
        AuthoritativeWorkGraph is not null
            ? AuthoritativeWorkGraph.Nodes.ContainsKey(workItemId)
            : KnownWorkItemIds.Contains(workItemId);

    private static IReadOnlySet<string> SnapshotSet(IEnumerable<string>? source) =>
        new HashSet<string>(source ?? [], StringComparer.Ordinal);
}

public sealed record OrchestrationDecisionValidationResult
{
    internal OrchestrationDecisionValidationResult(IEnumerable<string> errors)
    {
        Errors = Array.AsReadOnly(errors.ToArray());
    }

    public bool IsValid => Errors.Count == 0;

    public IReadOnlyList<string> Errors { get; }
}

public sealed class OrchestrationDecisionValidator
{
    private const int MaximumIdentifierCharacters = 256;
    private const int MaximumOutcomeCharacters = 2_000;
    private const int MaximumClaimCharacters = 2_000;
    private const int MaximumDirectAnswerCharacters = 32_000;
    private const int MaximumActivityFieldCharacters = 1_000;
    internal const int MaximumCompletionEvidenceProjections = 32;
    internal const int MaximumCompletionEvidenceProjectionCharacters = 48_000;

    public OrchestrationDecisionValidationResult Validate(
        OrchestrationDecision decision,
        OrchestrationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(context);
        var errors = new List<string>();
        var updatedWorkIds = new HashSet<string>(StringComparer.Ordinal);
        var claimIds = new HashSet<string>(StringComparer.Ordinal);

        ValidateWorkUpdate(decision.WorkUpdate, context, updatedWorkIds, errors);
        var candidateWorkGraph = ValidateAuthoritativeWorkGraph(
            decision.WorkUpdate,
            context,
            errors);
        ValidateClaims(decision.MaterialClaims, context, claimIds, errors);
        ValidateAction(
            decision,
            context,
            updatedWorkIds,
            claimIds,
            candidateWorkGraph,
            errors);
        return new OrchestrationDecisionValidationResult(errors);
    }

    private static void ValidateWorkUpdate(
        OrchestrationWorkUpdate? update,
        OrchestrationValidationContext context,
        HashSet<string> updatedWorkIds,
        List<string> errors)
    {
        if (update is null)
        {
            return;
        }

        if (update.BaseRevision != context.WorkGraphRevision)
        {
            errors.Add(
                $"workUpdate.baseRevision must equal authoritative work-graph revision {context.WorkGraphRevision}.");
        }

        foreach (var item in update.Items)
        {
            if (!ValidIdentifier(item.WorkItemId))
            {
                errors.Add("Every work item must have a bounded non-empty workItemId.");
            }
            else if (!updatedWorkIds.Add(item.WorkItemId))
            {
                errors.Add($"workItemId '{item.WorkItemId}' appears more than once.");
            }

            if (!BoundedText(item.Outcome, MaximumOutcomeCharacters))
            {
                errors.Add($"Work item '{item.WorkItemId}' must have a bounded non-empty outcome.");
            }

            ValidateUniqueIdentifiers(item.DependencyIds, $"Work item '{item.WorkItemId}' dependencyIds", errors);
            ValidateEvidenceIds(item.EvidenceIds, context, $"Work item '{item.WorkItemId}'", errors);
            var allEvidenceIsExactlyBound = HasOnlyExactWorkEvidenceBindings(
                item.EvidenceIds,
                item.WorkItemId,
                context);
            if (item.EvidenceIds.Count > 0 && !allEvidenceIsExactlyBound)
            {
                errors.Add(
                    $"Work item '{item.WorkItemId}' requires only evidence bound to that exact work-item ID using ordinal comparison; every cited evidence ID must resolve to an accepted projection.");
            }

            var isTerminal = item.Status is OrchestrationWorkStatus.Satisfied
                or OrchestrationWorkStatus.Impossible
                or OrchestrationWorkStatus.Superseded;
            if (isTerminal && item.EvidenceIds.Count == 0)
            {
                errors.Add($"Terminal work item '{item.WorkItemId}' must bind accepted evidence.");
            }
            else if (isTerminal
                     && allEvidenceIsExactlyBound
                     && !HasSucceededWorkEvidenceProjection(item.EvidenceIds, context))
            {
                errors.Add(
                    $"Terminal work item '{item.WorkItemId}' requires at least one explicit succeeded evidence record.");
            }
        }

        foreach (var item in update.Items)
        {
            if (item.ParentId is not null)
            {
                if (!KnownAfterUpdate(item.ParentId))
                {
                    errors.Add($"Work item '{item.WorkItemId}' refers to unknown parent '{item.ParentId}'.");
                }
                else if (string.Equals(item.WorkItemId, item.ParentId, StringComparison.Ordinal))
                {
                    errors.Add($"Work item '{item.WorkItemId}' cannot be its own parent.");
                }
            }

            if (item.Status == OrchestrationWorkStatus.Superseded)
            {
                if (item.SupersededById is null || !KnownAfterUpdate(item.SupersededById))
                {
                    errors.Add($"Superseded work item '{item.WorkItemId}' must identify its replacement.");
                }
                else if (string.Equals(item.WorkItemId, item.SupersededById, StringComparison.Ordinal))
                {
                    errors.Add($"Work item '{item.WorkItemId}' cannot supersede itself.");
                }
            }
            else if (item.SupersededById is not null)
            {
                errors.Add($"Only a superseded work item may set supersededById ('{item.WorkItemId}').");
            }

            foreach (var dependencyId in item.DependencyIds)
            {
                if (!KnownAfterUpdate(dependencyId))
                {
                    errors.Add(
                        $"Work item '{item.WorkItemId}' refers to unknown dependency '{dependencyId}'.");
                }
                else if (string.Equals(item.WorkItemId, dependencyId, StringComparison.Ordinal))
                {
                    errors.Add($"Work item '{item.WorkItemId}' cannot depend on itself.");
                }
            }
        }

        bool KnownAfterUpdate(string workItemId) =>
            updatedWorkIds.Contains(workItemId) || context.IsKnownWorkItem(workItemId);
    }

    private static WorkGraphSnapshot? ValidateAuthoritativeWorkGraph(
        OrchestrationWorkUpdate? update,
        OrchestrationValidationContext context,
        List<string> errors)
    {
        var current = context.AuthoritativeWorkGraph;
        if (current is null)
        {
            return null;
        }

        if (current.Revision != context.WorkGraphRevision)
        {
            errors.Add("The authoritative work graph does not match its advertised revision.");
            return current;
        }

        if (update is null)
        {
            return current;
        }

        var delta = new Ali.Modules.Orchestration.Work.WorkGraphDelta(
            update.BaseRevision,
            update.Items.Select(item => new WorkNode(
                    item.WorkItemId,
                    item.Outcome,
                    item.ParentId,
                    item.Status switch
                    {
                        OrchestrationWorkStatus.Pending => WorkNodeStatus.Pending,
                        OrchestrationWorkStatus.Active => WorkNodeStatus.Active,
                        OrchestrationWorkStatus.Satisfied => WorkNodeStatus.Satisfied,
                        OrchestrationWorkStatus.Impossible => WorkNodeStatus.Impossible,
                        OrchestrationWorkStatus.Superseded => WorkNodeStatus.Superseded,
                        _ => (WorkNodeStatus)(-1)
                    },
                    ImmutableArray.CreateRange(item.DependencyIds),
                    ImmutableArray.CreateRange(item.EvidenceIds),
                    item.SupersededById))
                .ToImmutableArray());
        var applied = WorkGraphApplier.Apply(
            current,
            delta,
            context.AcceptedEvidenceIds);
        if (!applied.Accepted)
        {
            foreach (var error in applied.Errors)
            {
                errors.Add("Authoritative work graph: " + error);
            }

            return current;
        }

        return applied.Snapshot;
    }

    private static void ValidateClaims(
        IReadOnlyList<OrchestrationMaterialClaim> claims,
        OrchestrationValidationContext context,
        HashSet<string> claimIds,
        List<string> errors)
    {
        foreach (var claim in claims)
        {
            if (!ValidIdentifier(claim.ClaimId))
            {
                errors.Add("Every material claim must have a bounded non-empty claimId.");
            }
            else if (!claimIds.Add(claim.ClaimId))
            {
                errors.Add($"claimId '{claim.ClaimId}' appears more than once.");
            }

            if (!BoundedText(claim.Statement, MaximumClaimCharacters))
            {
                errors.Add($"Material claim '{claim.ClaimId}' must have a bounded non-empty statement.");
            }

            ValidateEvidenceIds(claim.EvidenceIds, context, $"Material claim '{claim.ClaimId}'", errors);
            if (claim.EvidenceIds.Count == 0)
            {
                errors.Add($"Material claim '{claim.ClaimId}' must bind accepted evidence.");
            }
        }
    }

    private static void ValidateAction(
        OrchestrationDecision decision,
        OrchestrationValidationContext context,
        HashSet<string> updatedWorkIds,
        HashSet<string> claimIds,
        WorkGraphSnapshot? candidateWorkGraph,
        List<string> errors)
    {
        switch (decision.NextAction)
        {
            case CallToolAction call:
                ValidateCallTool(call, context, candidateWorkGraph, errors);
                break;
            case ExpandToolsAction expand:
                if (!BoundedText(expand.Need, MaximumOutcomeCharacters))
                {
                    errors.Add("ExpandTools.need must be bounded and non-empty.");
                }

                break;
            case AnswerDirectlyAction answer:
                if (decision.MaterialClaims.Count != 0)
                {
                    errors.Add("AnswerDirectly is allowed only when materialClaims is empty.");
                }

                if (candidateWorkGraph is { Revision: > 0 }
                    || candidateWorkGraph?.Nodes.Count > 0)
                {
                    errors.Add(
                        "AnswerDirectly is allowed only before an authoritative work graph has been established.");
                }

                if (!BoundedText(answer.Answer, MaximumDirectAnswerCharacters))
                {
                    errors.Add("AnswerDirectly.answer must be bounded and non-empty.");
                }

                break;
            case RequestUserInputAction request:
                if (!BoundedText(request.Question, MaximumOutcomeCharacters))
                {
                    errors.Add("RequestUserInput.question must be bounded and non-empty.");
                }

                if (!BoundedText(request.MissingInformation, MaximumOutcomeCharacters))
                {
                    errors.Add("RequestUserInput.missingInformation must be bounded and non-empty.");
                }

                break;
            case AwaitExternalEventAction wait:
                if (!ValidIdentifier(wait.TicketId)
                    || !context.ApprovedExternalTicketIds.Contains(wait.TicketId))
                {
                    errors.Add("AwaitExternalEvent.ticketId must identify an approved external ticket.");
                }

                if (!BoundedText(wait.WaitingFor, MaximumOutcomeCharacters))
                {
                    errors.Add("AwaitExternalEvent.waitingFor must be bounded and non-empty.");
                }

                break;
            case BeginCompletionAction completion:
                ValidateCompletion(
                    completion.Plan,
                    context,
                    updatedWorkIds,
                    claimIds,
                    candidateWorkGraph,
                    decision.MaterialClaims,
                    errors);
                break;
            default:
                errors.Add("The orchestration action type is not recognized.");
                break;
        }
    }

    private static void ValidateCallTool(
        CallToolAction call,
        OrchestrationValidationContext context,
        WorkGraphSnapshot? candidateWorkGraph,
        List<string> errors)
    {
        if (!context.SelectedTools.TryGetValue(call.ToolName, out var tool))
        {
            errors.Add($"CallTool.toolName '{call.ToolName}' is not selected for this pass.");
            return;
        }

        if (!BoundedText(call.Need, MaximumActivityFieldCharacters))
        {
            errors.Add("CallTool.need must be bounded and non-empty.");
        }

        if (!BoundedText(call.ExpectedProgress, MaximumActivityFieldCharacters))
        {
            errors.Add("CallTool.expectedProgress must be bounded and non-empty.");
        }

        var arguments = JsonSerializer.SerializeToElement(call.Arguments);
        ValidateJsonAgainstSchema(arguments, tool.JsonSchema, tool.JsonSchema, "CallTool.arguments", errors);

        if (candidateWorkGraph is null)
        {
            errors.Add("CallTool requires the authoritative work graph for exact work-item binding.");
            return;
        }

        var activeWorkItemCount = candidateWorkGraph.Nodes.Values.Count(
            item => item.Status == WorkNodeStatus.Active);
        if (activeWorkItemCount != 1)
        {
            errors.Add(
                "CallTool requires exactly one Active work item after applying its work update; "
                + $"found {activeWorkItemCount}.");
        }
    }

    private static void ValidateCompletion(
        CompletionPlan plan,
        OrchestrationValidationContext context,
        HashSet<string> updatedWorkIds,
        HashSet<string> claimIds,
        WorkGraphSnapshot? candidateWorkGraph,
        IReadOnlyList<OrchestrationMaterialClaim> claims,
        List<string> errors)
    {
        if (!ValidIdentifier(plan.AnswerId))
        {
            errors.Add("BeginCompletion.plan.answerId must be bounded and non-empty.");
        }

        ValidateUniqueIdentifiers(plan.RequiredOutcomeIds, "Completion requiredOutcomeIds", errors);
        ValidateUniqueIdentifiers(plan.RequiredClaimIds, "Completion requiredClaimIds", errors);
        if (candidateWorkGraph is null && plan.RequiredOutcomeIds.Count > 0)
        {
            errors.Add(
                "BeginCompletion requires the authoritative work graph for exact outcome projection.");
        }

        if (candidateWorkGraph is not null)
        {
            var authoritativeOutcomes = candidateWorkGraph.Nodes.Values
                .Where(static outcome => outcome.Status != WorkNodeStatus.Superseded)
                .ToArray();
            var invalidReadiness = plan.CompletionKind switch
            {
                CompletionKind.Succeeded => authoritativeOutcomes.FirstOrDefault(
                    static outcome => outcome.Status != WorkNodeStatus.Satisfied),
                CompletionKind.ProvenImpossible => authoritativeOutcomes.FirstOrDefault(
                    static outcome => outcome.Status is not (
                        WorkNodeStatus.Satisfied or WorkNodeStatus.Impossible)),
                _ => null
            };
            if (invalidReadiness is not null)
            {
                errors.Add(plan.CompletionKind == CompletionKind.Succeeded
                    ? $"Successful completion requires every non-superseded authoritative outcome to be Satisfied; '{invalidReadiness.Id}' is {invalidReadiness.Status}."
                    : $"Proven-impossible completion requires every non-superseded authoritative outcome to be terminal; '{invalidReadiness.Id}' is {invalidReadiness.Status}.");
            }

            if (plan.CompletionKind == CompletionKind.ProvenImpossible
                && !authoritativeOutcomes.Any(
                    static outcome => outcome.Status == WorkNodeStatus.Impossible))
            {
                errors.Add(
                    "Proven-impossible completion requires at least one authoritative Impossible outcome.");
            }

            var unprovedTerminal = authoritativeOutcomes.FirstOrDefault(outcome =>
                (outcome.Status is WorkNodeStatus.Satisfied or WorkNodeStatus.Impossible)
                && !HasSupportingWorkEvidence(
                    outcome.EvidenceIds,
                    outcome.Id,
                    context));
            if (unprovedTerminal is not null)
            {
                errors.Add(
                    $"Authoritative terminal outcome '{unprovedTerminal.Id}' lacks exact succeeded evidence.");
            }
        }

        foreach (var outcomeId in plan.RequiredOutcomeIds)
        {
            var knownOutcome = candidateWorkGraph is not null
                ? candidateWorkGraph.Nodes.ContainsKey(outcomeId)
                : updatedWorkIds.Contains(outcomeId) || context.IsKnownWorkItem(outcomeId);
            if (!knownOutcome)
            {
                errors.Add($"Completion refers to unknown outcome '{outcomeId}'.");
            }
            else if (candidateWorkGraph is not null
                     && candidateWorkGraph.Nodes.TryGetValue(outcomeId, out var outcome))
            {
                if (plan.CompletionKind == CompletionKind.Succeeded
                    && outcome.Status != WorkNodeStatus.Satisfied)
                {
                    errors.Add(
                        $"Successful completion requires outcome '{outcomeId}' to be Satisfied, not {outcome.Status}.");
                }
                else if (plan.CompletionKind == CompletionKind.ProvenImpossible
                         && outcome.Status is not (WorkNodeStatus.Satisfied or WorkNodeStatus.Impossible))
                {
                    errors.Add(
                        $"Proven-impossible completion requires outcome '{outcomeId}' to be terminal, not {outcome.Status}.");
                }
            }
        }

        var requiredClaimIds = plan.RequiredClaimIds.ToHashSet(StringComparer.Ordinal);
        foreach (var claimId in plan.RequiredClaimIds)
        {
            if (!claimIds.Contains(claimId))
            {
                errors.Add($"Completion refers to claim '{claimId}' not present in this decision.");
            }
        }

        foreach (var claim in claims)
        {
            if (!requiredClaimIds.Contains(claim.ClaimId))
            {
                errors.Add(
                    $"Completion omits material claim '{claim.ClaimId}' from requiredClaimIds.");
            }

            if (!HasSupportingSucceededEvidence(claim.EvidenceIds, context))
            {
                errors.Add(
                    $"Material claim '{claim.ClaimId}' requires exact succeeded evidence before completion.");
            }
        }

        var requiredSubjects = new HashSet<string>(plan.RequiredOutcomeIds, StringComparer.Ordinal);
        requiredSubjects.UnionWith(plan.RequiredClaimIds);
        var requiredOutcomeIds = plan.RequiredOutcomeIds.ToHashSet(StringComparer.Ordinal);
        var claimsById = claims
            .Where(claim => ValidIdentifier(claim.ClaimId))
            .GroupBy(claim => claim.ClaimId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var boundSubjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in plan.Bindings)
        {
            if (!requiredSubjects.Contains(binding.SubjectId))
            {
                errors.Add($"Completion binding refers to unrequired subject '{binding.SubjectId}'.");
            }
            else if (!boundSubjects.Add(binding.SubjectId))
            {
                errors.Add($"Completion subject '{binding.SubjectId}' is bound more than once.");
            }

            ValidateEvidenceIds(binding.EvidenceIds, context, $"Completion subject '{binding.SubjectId}'", errors);
            if (binding.EvidenceIds.Count == 0)
            {
                errors.Add($"Completion subject '{binding.SubjectId}' must bind accepted evidence.");
            }
            else if (requiredOutcomeIds.Contains(binding.SubjectId)
                     && !HasSupportingWorkEvidence(
                         binding.EvidenceIds,
                         binding.SubjectId,
                         context))
            {
                errors.Add(
                    $"Completion outcome '{binding.SubjectId}' requires only evidence bound to that exact work-item ID, including explicit succeeded evidence.");
            }
            else if (claimsById.ContainsKey(binding.SubjectId)
                     && !HasSupportingSucceededEvidence(binding.EvidenceIds, context))
            {
                errors.Add(
                    $"Completion claim '{binding.SubjectId}' requires explicit succeeded evidence.");
            }

            if (candidateWorkGraph is not null
                && candidateWorkGraph.Nodes.TryGetValue(binding.SubjectId, out var workItem)
                && binding.EvidenceIds.Any(id => !workItem.EvidenceIds.Contains(id, StringComparer.Ordinal)))
            {
                errors.Add(
                    $"Completion subject '{binding.SubjectId}' cites evidence not accepted by that work item.");
            }
            else if (claimsById.TryGetValue(binding.SubjectId, out var claim)
                     && binding.EvidenceIds.Any(id => !claim.EvidenceIds.Contains(id, StringComparer.Ordinal)))
            {
                errors.Add(
                    $"Completion subject '{binding.SubjectId}' cites evidence not declared by that claim.");
            }
        }

        foreach (var requiredSubject in requiredSubjects)
        {
            if (!boundSubjects.Contains(requiredSubject))
            {
                errors.Add($"Completion subject '{requiredSubject}' has no evidence binding.");
            }
        }

        if (plan.RequiredOutcomeIds.Count == 0 && plan.RequiredClaimIds.Count == 0)
        {
            errors.Add("BeginCompletion must name at least one required outcome or claim.");
        }

        ValidateCompletionDossier(plan, context, errors);

        if (plan.RequestedFormat.Length > MaximumActivityFieldCharacters)
        {
            errors.Add("Completion requestedFormat exceeds the bounded planning limit.");
        }

        ValidateUniqueIdentifiers(plan.RequestedSections, "Completion requestedSections", errors);
    }

    private static void ValidateCompletionDossier(
        CompletionPlan plan,
        OrchestrationValidationContext context,
        List<string> errors)
    {
        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in plan.Bindings)
        {
            evidenceIds.UnionWith(binding.EvidenceIds);
        }

        if (evidenceIds.Count > MaximumCompletionEvidenceProjections)
        {
            errors.Add(
                $"Completion dossier contains {evidenceIds.Count} unique evidence projections; the fail-closed limit is {MaximumCompletionEvidenceProjections}.");
        }

        long projectedCharacters = 0;
        foreach (var evidenceId in evidenceIds)
        {
            if (!context.EvidenceProjections.TryGetValue(evidenceId, out var evidence))
            {
                errors.Add(
                    $"Completion dossier evidence '{evidenceId}' has no exact accepted projection.");
                continue;
            }

            projectedCharacters += evidence.Projection.Length;
        }

        if (projectedCharacters > MaximumCompletionEvidenceProjectionCharacters)
        {
            errors.Add(
                $"Completion dossier contains {projectedCharacters} projected characters; the fail-closed limit is {MaximumCompletionEvidenceProjectionCharacters}.");
        }
    }

    private static void ValidateEvidenceIds(
        IReadOnlyList<string> evidenceIds,
        OrchestrationValidationContext context,
        string owner,
        List<string> errors)
    {
        ValidateUniqueIdentifiers(evidenceIds, $"{owner} evidenceIds", errors);
        foreach (var evidenceId in evidenceIds)
        {
            if (!context.AcceptedEvidenceIds.Contains(evidenceId))
            {
                errors.Add($"{owner} refers to unaccepted evidence '{evidenceId}'.");
            }
        }
    }

    private static bool HasSupportingSucceededEvidence(
        IReadOnlyList<string> evidenceIds,
        OrchestrationValidationContext context) =>
        evidenceIds.Any(evidenceId =>
            context.EvidenceOutcomes.TryGetValue(evidenceId, out var outcome)
            && outcome == PlanningToolDomainOutcome.Succeeded);

    private static bool HasSupportingWorkEvidence(
        IReadOnlyList<string> evidenceIds,
        string workItemId,
        OrchestrationValidationContext context)
    {
        return HasOnlyExactWorkEvidenceBindings(evidenceIds, workItemId, context)
               && evidenceIds.Any(evidenceId =>
                   context.EvidenceProjections[evidenceId].DomainOutcome
                   == PlanningToolDomainOutcome.Succeeded);
    }

    private static bool HasOnlyExactWorkEvidenceBindings(
        IReadOnlyList<string> evidenceIds,
        string workItemId,
        OrchestrationValidationContext context) =>
        evidenceIds.All(evidenceId =>
            context.EvidenceProjections.TryGetValue(evidenceId, out var evidence)
            && string.Equals(evidence.WorkItemId, workItemId, StringComparison.Ordinal));

    private static bool HasSucceededWorkEvidenceProjection(
        IReadOnlyList<string> evidenceIds,
        OrchestrationValidationContext context) =>
        evidenceIds.Any(evidenceId =>
            context.EvidenceProjections.TryGetValue(evidenceId, out var evidence)
            && evidence.DomainOutcome == PlanningToolDomainOutcome.Succeeded);

    private static void ValidateUniqueIdentifiers(
        IReadOnlyList<string> values,
        string owner,
        List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!ValidIdentifier(value))
            {
                errors.Add($"{owner} contains an invalid identifier.");
            }
            else if (!seen.Add(value))
            {
                errors.Add($"{owner} contains duplicate identifier '{value}'.");
            }
        }
    }

    private static bool ValidIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumIdentifierCharacters;

    private static bool BoundedText(string value, int maximumCharacters) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumCharacters;

    private static void ValidateJsonAgainstSchema(
        JsonElement value,
        JsonElement schema,
        JsonElement rootSchema,
        string path,
        List<string> errors)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path} has an invalid registered JSON schema.");
            return;
        }

        if (schema.TryGetProperty("$ref", out var reference)
            && reference.ValueKind == JsonValueKind.String)
        {
            if (!TryResolveLocalReference(rootSchema, reference.GetString(), out var referencedSchema))
            {
                errors.Add($"{path} uses an unresolved registered schema reference.");
                return;
            }

            ValidateJsonAgainstSchema(value, referencedSchema, rootSchema, path, errors);
            return;
        }

        if (schema.TryGetProperty("allOf", out var allOf) && allOf.ValueKind == JsonValueKind.Array)
        {
            foreach (var branch in allOf.EnumerateArray())
            {
                ValidateJsonAgainstSchema(value, branch, rootSchema, path, errors);
            }
        }

        if (schema.TryGetProperty("oneOf", out var oneOf) && oneOf.ValueKind == JsonValueKind.Array)
        {
            var validBranches = CountValidBranches(value, oneOf, rootSchema, path);
            if (validBranches != 1)
            {
                errors.Add($"{path} must match exactly one registered schema branch.");
                return;
            }
        }

        if (schema.TryGetProperty("anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array)
        {
            if (CountValidBranches(value, anyOf, rootSchema, path) == 0)
            {
                errors.Add($"{path} must match a registered schema branch.");
                return;
            }
        }

        if (schema.TryGetProperty("enum", out var enumValues)
            && enumValues.ValueKind == JsonValueKind.Array
            && !enumValues.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
        {
            errors.Add($"{path} is not one of the registered enum values.");
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value))
        {
            errors.Add($"{path} does not match the registered constant.");
        }

        if (schema.TryGetProperty("type", out var typeElement))
        {
            if (!MatchesType(value, typeElement))
            {
                errors.Add($"{path} does not match the registered JSON type.");
                return;
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            ValidateObject(value, schema, rootSchema, path, errors);
        }
        else if (value.ValueKind == JsonValueKind.Array
                 && schema.TryGetProperty("items", out var itemSchema))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                ValidateJsonAgainstSchema(item, itemSchema, rootSchema, $"{path}[{index++}]", errors);
            }
        }
    }

    private static void ValidateObject(
        JsonElement value,
        JsonElement schema,
        JsonElement rootSchema,
        string path,
        List<string> errors)
    {
        var required = schema.TryGetProperty("required", out var requiredElement)
                       && requiredElement.ValueKind == JsonValueKind.Array
            ? requiredElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var requiredName in required)
        {
            if (!value.TryGetProperty(requiredName, out _))
            {
                errors.Add($"{path}.{requiredName} is required by the registered tool schema.");
            }
        }

        var hasProperties = schema.TryGetProperty("properties", out var properties)
                            && properties.ValueKind == JsonValueKind.Object;
        var disallowAdditional = schema.TryGetProperty("additionalProperties", out var additional)
                                 && additional.ValueKind == JsonValueKind.False;
        foreach (var property in value.EnumerateObject())
        {
            if (hasProperties && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                ValidateJsonAgainstSchema(
                    property.Value,
                    propertySchema,
                    rootSchema,
                    $"{path}.{property.Name}",
                    errors);
            }
            else if (disallowAdditional)
            {
                errors.Add($"{path}.{property.Name} is not allowed by the registered tool schema.");
            }
        }
    }

    private static int CountValidBranches(
        JsonElement value,
        JsonElement branches,
        JsonElement rootSchema,
        string path)
    {
        var count = 0;
        foreach (var branch in branches.EnumerateArray())
        {
            var branchErrors = new List<string>();
            ValidateJsonAgainstSchema(value, branch, rootSchema, path, branchErrors);
            if (branchErrors.Count == 0)
            {
                count++;
            }
        }

        return count;
    }

    private static bool MatchesType(JsonElement value, JsonElement typeElement)
    {
        if (typeElement.ValueKind == JsonValueKind.Array)
        {
            return typeElement.EnumerateArray().Any(type => MatchesType(value, type));
        }

        if (typeElement.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        return typeElement.GetString() switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true
        };
    }

    private static bool TryResolveLocalReference(
        JsonElement rootSchema,
        string? reference,
        out JsonElement schema)
    {
        schema = default;
        if (string.IsNullOrWhiteSpace(reference) || !reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return false;
        }

        var current = rootSchema;
        foreach (var encodedSegment in reference[2..].Split('/'))
        {
            var segment = encodedSegment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        schema = current;
        return true;
    }
}
