using System.Globalization;
using Ali.Modules.Runtime.Models;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Orchestration.Planning;

internal sealed record AliPlanningContextWindowSlice(
    int ReferencedConversationLimit,
    int EvidenceLimit,
    int TotalReferentialConversation,
    int TotalEvidence,
    int InspectedEvidencePageLimit,
    int TotalInspectedEvidencePageItems,
    int InspectedWorkPageLimit,
    int TotalInspectedWorkPageItems)
{
    internal bool IsPaged =>
        ReferencedConversationLimit < TotalReferentialConversation
        || EvidenceLimit < TotalEvidence
        || InspectedEvidencePageLimit < TotalInspectedEvidencePageItems
        || InspectedWorkPageLimit < TotalInspectedWorkPageItems;
}

internal sealed record AliProjectedPlanningInput(
    IReadOnlyList<ChatMessage> Messages,
    AliPlanningInputAdmissionResult Admission,
    AliPlanningContextWindowSlice Slice)
{
    internal string ToUserVisibleFailure()
    {
        if (!Slice.IsPaged)
        {
            return Admission.ToUserVisibleMessage();
        }

        var charge = Admission.CalculatedInputCharge is { } calculated
            ? calculated.ToString("N0", CultureInfo.InvariantCulture) + " tokens"
            : "not calculated safely";
        return "Ali paused this turn before contacting the model because even the smallest useful "
            + "planning projection did not fit the selected context and output limits. Configured "
            + "context: " + Admission.ConfiguredContextTokens.ToString("N0", CultureInfo.InvariantCulture)
            + " tokens; configured output reserve: "
            + Admission.ConfiguredOutputTokenLimit.ToString("N0", CultureInfo.InvariantCulture)
            + " tokens; available input budget: "
            + Admission.InputBudget.ToString("N0", CultureInfo.InvariantCulture)
            + " tokens; calculated mandatory input charge: " + charge
            + "; counter mode: " + Admission.CounterMode + ". The selected settings were not "
            + "changed. Optional conversation and evidence remain in durable local state for typed "
            + "paging; the immutable request, accepted steering, protocol, state, capability, and "
            + "attachment bindings were not truncated.";
    }
}

/// <summary>
/// Fits optional prompt material to the exact user-selected context/output pair. It never edits
/// those settings or text-truncates an included item: whole low-priority records are paged out.
/// </summary>
internal sealed class AliPlanningContextProjector
{
    private readonly AliStateBackedChatHistoryAdapter _history;
    private readonly AliPlanningInputAdmission _admission;

    internal AliPlanningContextProjector(
        AliStateBackedChatHistoryAdapter history,
        AliPlanningInputAdmission admission)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
    }

    internal AliProjectedPlanningInput Project(
        ModelProfile profile,
        string immutableOriginalRequest,
        AliPlanningTurnInput input,
        string capabilityDirectory,
        IReadOnlyList<AIFunctionDeclaration> selectedTools,
        IReadOnlyCollection<string> expandableToolGroupIds,
        AliPlanningAttachmentProjection attachmentProjection,
        AIFunctionDeclaration protocol)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(selectedTools);
        ArgumentNullException.ThrowIfNull(expandableToolGroupIds);
        ArgumentNullException.ThrowIfNull(attachmentProjection);
        ArgumentNullException.ThrowIfNull(protocol);

        var referentialCount = input.AcceptedPriorConversation.Count(static item => !item.IsSteering);
        var evidenceCount = Math.Min(
            input.AcceptedEvidence.Count,
            AliStateBackedChatHistoryAdapter.MaximumEvidenceProjections);
        var evidencePageCount = input.InspectedEvidencePage?.Items.Count ?? 0;
        var workPageCount = input.InspectedWorkPage?.Items.Count ?? 0;

        var fullPage = FitOptionalMaterial(evidencePageCount, workPageCount);
        if (fullPage.Admission.IsAdmitted || !IsContextOverflow(fullPage))
        {
            return fullPage;
        }

        if (evidencePageCount > 0 && workPageCount == 0)
        {
            var smallest = Evaluate(0, 0, 1, 0);
            if (!smallest.Admission.IsAdmitted)
            {
                return smallest;
            }

            var fittedPage = Maximize(
                1,
                evidencePageCount,
                limit => Evaluate(0, 0, limit, 0));
            return FitOptionalMaterial(
                fittedPage.Slice.InspectedEvidencePageLimit,
                0);
        }

        if (workPageCount > 0 && evidencePageCount == 0)
        {
            var smallest = Evaluate(0, 0, 0, 1);
            if (!smallest.Admission.IsAdmitted)
            {
                return smallest;
            }

            var fittedPage = Maximize(
                1,
                workPageCount,
                limit => Evaluate(0, 0, 0, limit));
            return FitOptionalMaterial(
                0,
                fittedPage.Slice.InspectedWorkPageLimit);
        }

        return fullPage;

        AliProjectedPlanningInput FitOptionalMaterial(
            int evidencePageLimit,
            int workPageLimit)
        {
            var full = Evaluate(
                referentialCount,
                evidenceCount,
                evidencePageLimit,
                workPageLimit);
            if (full.Admission.IsAdmitted || !IsContextOverflow(full))
            {
                return full;
            }

            // Referential conversation is lower priority than accepted evidence. Remove it first.
            var evidenceOnly = Evaluate(
                0,
                evidenceCount,
                evidencePageLimit,
                workPageLimit);
            if (evidenceOnly.Admission.IsAdmitted)
            {
                return Maximize(
                    0,
                    referentialCount,
                    limit => Evaluate(
                        limit,
                        evidenceCount,
                        evidencePageLimit,
                        workPageLimit));
            }

            if (!IsContextOverflow(evidenceOnly))
            {
                return evidenceOnly;
            }

            var mandatory = Evaluate(
                0,
                0,
                evidencePageLimit,
                workPageLimit);
            if (!mandatory.Admission.IsAdmitted)
            {
                return mandatory;
            }

            return Maximize(
                0,
                evidenceCount,
                limit => Evaluate(
                    0,
                    limit,
                    evidencePageLimit,
                    workPageLimit));
        }

        AliProjectedPlanningInput Evaluate(
            int conversationLimit,
            int acceptedEvidenceLimit,
            int evidencePageLimit,
            int workPageLimit)
        {
            var slice = new AliPlanningContextWindowSlice(
                conversationLimit,
                acceptedEvidenceLimit,
                referentialCount,
                input.AcceptedEvidence.Count,
                evidencePageLimit,
                evidencePageCount,
                workPageLimit,
                workPageCount);
            var projectedInput = SliceInspectedPages(
                input,
                evidencePageLimit,
                workPageLimit);
            var messages = _history.BuildMessages(
                immutableOriginalRequest,
                projectedInput,
                capabilityDirectory,
                selectedTools,
                attachmentProjection,
                expandableToolGroupIds,
                slice);
            return new AliProjectedPlanningInput(
                messages,
                _admission.Evaluate(profile, messages, selectedTools, protocol),
                slice);
        }
    }

    private static bool IsContextOverflow(AliProjectedPlanningInput input) =>
        string.Equals(
            input.Admission.FailureCode,
            "model-input-exceeds-configured-context",
            StringComparison.Ordinal);

    private static AliPlanningTurnInput SliceInspectedPages(
        AliPlanningTurnInput input,
        int evidencePageLimit,
        int workPageLimit)
    {
        var evidencePage = SliceEvidencePage(input.InspectedEvidencePage, evidencePageLimit);
        var workPage = SliceWorkPage(input.InspectedWorkPage, workPageLimit);
        if (ReferenceEquals(evidencePage, input.InspectedEvidencePage)
            && ReferenceEquals(workPage, input.InspectedWorkPage))
        {
            return input;
        }

        return new AliPlanningTurnInput(
            input.StateRevision,
            input.StateProjection,
            input.AcceptedPriorConversation,
            input.AcceptedEvidence,
            input.KnownWorkItemIds,
            input.ApprovedExternalTicketIds,
            input.WorkGraphRevision,
            input.AuthoritativeWorkGraph,
            evidencePage,
            workPage);
    }

    private static AliPlanningEvidencePage? SliceEvidencePage(
        AliPlanningEvidencePage? page,
        int limit)
    {
        if (page is null || limit >= page.Items.Count)
        {
            return page;
        }

        var items = page.Items.Take(limit).ToArray();
        return page with
        {
            NextCursor = items.Length == 0 ? null : items[^1].Cursor,
            HasMore = page.HasMore || items.Length < page.Items.Count,
            Items = Array.AsReadOnly(items)
        };
    }

    private static AliPlanningWorkPage? SliceWorkPage(
        AliPlanningWorkPage? page,
        int limit)
    {
        if (page is null || limit >= page.Items.Count)
        {
            return page;
        }

        var items = page.Items.Take(limit).ToArray();
        return page with
        {
            NextWorkItemId = items.Length == 0 ? null : items[^1].WorkItemId,
            HasMore = page.HasMore || items.Length < page.Items.Count,
            Items = Array.AsReadOnly(items)
        };
    }

    private static AliProjectedPlanningInput Maximize(
        int minimum,
        int maximum,
        Func<int, AliProjectedPlanningInput> evaluate)
    {
        var best = evaluate(minimum);
        var low = minimum;
        var high = maximum;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var candidate = evaluate(middle);
            if (candidate.Admission.IsAdmitted)
            {
                best = candidate;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best;
    }
}
