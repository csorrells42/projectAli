using System.Globalization;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Runtime;
using Microsoft.Extensions.AI;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Ali.Modules.Orchestration.Completion;

/// <summary>
/// Fresh, no-tool completion review lane. Preparation freezes and authorizes the exact dispatch;
/// ReviewAsync then acquires a durable review identity before making any model call. A false
/// verdict contains outcomes for the existing planner and never selects tools itself.
/// </summary>
internal sealed class AliCompletionCritic
{
    private readonly Func<BoundModelDispatchSnapshot> _captureDispatch;
    private readonly Func<BoundModelDispatchSnapshot, TurnRuntimeBindings> _buildBindings;
    private readonly Func<long, TurnRuntimeBindings, CancellationToken,
        ValueTask<AliCompletionDispatchAuthorization>> _authorizeDispatch;
    private readonly AliPlanningInputAdmission _inputAdmission;

    internal AliCompletionCritic(
        Func<BoundModelDispatchSnapshot> captureDispatch,
        Func<BoundModelDispatchSnapshot, TurnRuntimeBindings> buildBindings,
        Func<long, TurnRuntimeBindings, CancellationToken,
            ValueTask<AliCompletionDispatchAuthorization>> authorizeDispatch,
        AliPlanningInputAdmission? inputAdmission = null)
    {
        _captureDispatch = captureDispatch
            ?? throw new ArgumentNullException(nameof(captureDispatch));
        _buildBindings = buildBindings
            ?? throw new ArgumentNullException(nameof(buildBindings));
        _authorizeDispatch = authorizeDispatch
            ?? throw new ArgumentNullException(nameof(authorizeDispatch));
        _inputAdmission = inputAdmission ?? new AliPlanningInputAdmission();
    }

    /// <summary>
    /// Captures the exact client/model/settings tuple and performs every admission check whose
    /// message is already known. It deliberately makes no model call and writes no review state.
    /// </summary>
    internal async ValueTask<AliCompletionCriticPreparationAttempt> PrepareAsync(
        AliCompletionCriticRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        BoundModelDispatchSnapshot dispatch;
        TurnRuntimeBindings bindings;
        AliCompletionDispatchAuthorization authorization;
        bool useNativeProtocol;
        try
        {
            dispatch = _captureDispatch()
                ?? throw new InvalidOperationException(
                    "The runtime returned no bound completion-critic dispatch snapshot.");
            ArgumentNullException.ThrowIfNull(dispatch.ChatClient);
            ArgumentNullException.ThrowIfNull(dispatch.Profile);
            ArgumentNullException.ThrowIfNull(dispatch.RuntimeBinding);
            ArgumentNullException.ThrowIfNull(dispatch.ModelBinding);
            ArgumentNullException.ThrowIfNull(dispatch.GenerationSettingsBinding);
            bindings = _buildBindings(dispatch)
                ?? throw new InvalidOperationException(
                    "The completion-critic binding factory returned no exact snapshot.");
            bindings.Validate();
            useNativeProtocol =
                AliOrchestrationPlanningClient.RequireBoundEngineeringProtocol(dispatch);
            authorization = await _authorizeDispatch(
                    request.StateRevision,
                    bindings,
                    cancellationToken)
                .ConfigureAwait(false);
            if (authorization is null)
            {
                throw new InvalidOperationException(
                    "The durable turn returned no completion-critic dispatch authorization.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not OutOfMemoryException)
        {
            return AliCompletionCriticPreparationAttempt.Failed(
                BindingFailure(changedBindings: null));
        }

        if (!authorization.CanCompose
            || authorization.StateRevision != request.StateRevision)
        {
            return AliCompletionCriticPreparationAttempt.Failed(
                BindingFailure(authorization.ChangedBindings));
        }

        var responseProtocol = AliCompletionCriticProtocol.CreateAdmissionDeclaration();
        var pageMessages = AliCompletionCriticProjection.BuildPageAuditMessages(request);
        IReadOnlyList<MeaiChatMessage>? singlePageMessages = null;
        if (request.CommittedDraft.Segments.Count == 1)
        {
            singlePageMessages =
                AliCompletionCriticProjection.BuildSinglePageFinalMessages(request);
            var admission = _inputAdmission.EvaluateCompletion(
                dispatch.Profile,
                singlePageMessages,
                responseProtocol);
            if (!admission.IsAdmitted)
            {
                return AliCompletionCriticPreparationAttempt.Failed(
                    InputNotAdmitted(admission));
            }
        }
        else
        {
            foreach (var messages in pageMessages)
            {
                var admission = _inputAdmission.EvaluateCompletion(
                    dispatch.Profile,
                    messages,
                    responseProtocol);
                if (!admission.IsAdmitted)
                {
                    return AliCompletionCriticPreparationAttempt.Failed(
                        InputNotAdmitted(admission));
                }
            }
        }

        var identity = AliCompletionCriticProjection.BuildIdentity(
            request,
            dispatch,
            bindings);
        return AliCompletionCriticPreparationAttempt.Prepared(
            new AliCompletionCriticPreparedReview(
                request,
                identity,
                dispatch,
                bindings,
                useNativeProtocol,
                pageMessages,
                singlePageMessages));
    }

    internal async ValueTask<AliCompletionCriticAttempt> ReviewAsync(
        AliCompletionCriticPreparedReview prepared,
        IAliCompletionCriticReviewStore reviewStore,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(reviewStore);
        prepared.Identity.Validate();

        AliCompletionCriticReviewLease lease;
        try
        {
            lease = await reviewStore.BeginAsync(prepared.Identity, cancellationToken)
                .ConfigureAwait(false);
            if (lease is null || lease.StoreSnapshot.StateRevision < 0)
            {
                throw new InvalidDataException(
                    "The completion-critic review store returned an invalid lease.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not OutOfMemoryException)
        {
            return AliCompletionCriticAttempt.Failed(
                prepared.Identity,
                StoreFailure(
                    "Ali paused before the completion critic ran because its durable review "
                    + "identity could not be recorded. No critic model call ran."));
        }

        switch (lease.Disposition)
        {
            case AliCompletionCriticReviewDisposition.ReuseCommitted:
                if (lease.CommittedVerdict is null)
                {
                    return AliCompletionCriticAttempt.Failed(
                        prepared.Identity,
                        StoreFailure(
                            "Ali paused because the durable completion-critic receipt did not "
                            + "contain its committed verdict. No critic model call ran."),
                        lease.StoreSnapshot);
                }

                return AliCompletionCriticAttempt.Completed(
                    prepared.Identity,
                    lease.CommittedVerdict,
                    lease.StoreSnapshot,
                    reusedCommittedVerdict: true,
                    modelCallCount: 0);

            case AliCompletionCriticReviewDisposition.IdenticalReviewInProgress:
                return AliCompletionCriticAttempt.Failed(
                    prepared.Identity,
                    new AliCompletionCriticFailure(
                        AliCompletionCriticFailureKind.IdenticalReviewInProgress,
                        "Ali did not rerun the completion critic because this exact answer, "
                        + "evidence, state, and runtime identity is already recorded as prepared. "
                        + "A changed authoritative state is required before another critic call."),
                    lease.StoreSnapshot);

            case AliCompletionCriticReviewDisposition.Start:
                if (lease.CommittedVerdict is not null)
                {
                    return AliCompletionCriticAttempt.Failed(
                        prepared.Identity,
                        StoreFailure(
                            "Ali paused because a new completion-critic lease unexpectedly "
                            + "contained a verdict. No critic model call ran."),
                        lease.StoreSnapshot);
                }

                break;

            default:
                return AliCompletionCriticAttempt.Failed(
                    prepared.Identity,
                    StoreFailure(
                        "Ali paused because the durable completion-critic lease was invalid. "
                        + "No critic model call ran."),
                    lease.StoreSnapshot);
        }

        var modelCallCount = 0;
        AliCompletionCriticVerdict exactVerdict;
        if (prepared.SinglePageFinalMessages is not null)
        {
            var one = await CallAsync(
                    prepared,
                    prepared.SinglePageFinalMessages,
                    cancellationToken)
                .ConfigureAwait(false);
            modelCallCount++;
            if (one.Failure is not null)
            {
                return AliCompletionCriticAttempt.Failed(
                    prepared.Identity,
                    one.Failure,
                    lease.StoreSnapshot,
                    modelCallCount);
            }

            exactVerdict = one.Verdict!;
        }
        else
        {
            var pageVerdicts = new List<AliCompletionCriticVerdict>(
                prepared.PageAuditMessages.Count);
            foreach (var messages in prepared.PageAuditMessages)
            {
                var page = await CallAsync(prepared, messages, cancellationToken)
                    .ConfigureAwait(false);
                modelCallCount++;
                if (page.Failure is not null)
                {
                    return AliCompletionCriticAttempt.Failed(
                        prepared.Identity,
                        page.Failure,
                        lease.StoreSnapshot,
                        modelCallCount);
                }

                pageVerdicts.Add(page.Verdict!);
            }

            var finalMessages = AliCompletionCriticProjection.BuildCrossPageFinalMessages(
                prepared.Request,
                pageVerdicts);
            var finalAdmission = _inputAdmission.EvaluateCompletion(
                prepared.Dispatch.Profile,
                finalMessages,
                AliCompletionCriticProtocol.CreateAdmissionDeclaration());
            if (!finalAdmission.IsAdmitted)
            {
                return AliCompletionCriticAttempt.Failed(
                    prepared.Identity,
                    InputNotAdmitted(finalAdmission),
                    lease.StoreSnapshot,
                    modelCallCount);
            }

            var final = await CallAsync(prepared, finalMessages, cancellationToken)
                .ConfigureAwait(false);
            modelCallCount++;
            if (final.Failure is not null)
            {
                return AliCompletionCriticAttempt.Failed(
                    prepared.Identity,
                    final.Failure,
                    lease.StoreSnapshot,
                    modelCallCount);
            }

            try
            {
                exactVerdict = Aggregate(pageVerdicts, final.Verdict!);
            }
            catch (InvalidDataException)
            {
                return AliCompletionCriticAttempt.Failed(
                    prepared.Identity,
                    OutputFailure(
                        "Ali paused because the page and final completion reviews returned more "
                        + "distinct material outcomes than the typed verdict can preserve without "
                        + "omission. The exact review identity remains recorded and was not rerun."),
                    lease.StoreSnapshot,
                    modelCallCount);
            }
        }

        AliCompletionCriticStoreSnapshot committed;
        try
        {
            committed = await reviewStore.CommitAsync(
                    prepared.Identity,
                    exactVerdict,
                    cancellationToken)
                .ConfigureAwait(false);
            if (committed is null || committed.StateRevision < lease.StoreSnapshot.StateRevision)
            {
                throw new InvalidDataException(
                    "The completion-critic review store returned an invalid commit receipt.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not OutOfMemoryException)
        {
            return AliCompletionCriticAttempt.Failed(
                prepared.Identity,
                StoreFailure(
                    "Ali paused because the completion-critic verdict could not be committed to "
                    + "the durable turn journal. The verdict was not used for publication or "
                    + "replanning, and the exact review identity will not be called again."),
                lease.StoreSnapshot,
                modelCallCount);
        }

        return AliCompletionCriticAttempt.Completed(
            prepared.Identity,
            exactVerdict,
            committed,
            reusedCommittedVerdict: false,
            modelCallCount);
    }

    private static async ValueTask<CallResult> CallAsync(
        AliCompletionCriticPreparedReview prepared,
        IReadOnlyList<MeaiChatMessage> messages,
        CancellationToken cancellationToken)
    {
        ChatResponse response;
        try
        {
            response = await prepared.Dispatch.ChatClient
                .GetResponseAsync(
                    messages,
                    CreateOptions(prepared),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not OutOfMemoryException)
        {
            return CallResult.Failed(OutputFailure(
                "Ali paused because the completion critic did not return a complete typed "
                + "verdict. The exact review identity remains recorded; no answer was published."));
        }

        var decoded = prepared.UseNativeProtocol
            ? AliCompletionCriticProtocol.DecodeNative(response)
            : AliCompletionCriticProtocol.DecodeCompatibility(response);
        return decoded.IsSuccess
            ? CallResult.Completed(decoded.Verdict!)
            : CallResult.Failed(OutputFailure(
                "Ali paused because the completion critic returned an incomplete or malformed "
                + "typed verdict. The exact review identity remains recorded; no answer was "
                + "published and malformed critic prose was discarded."));
    }

    private static ChatOptions CreateOptions(AliCompletionCriticPreparedReview prepared)
    {
        var protocol = AliCompletionCriticProtocol.CreateAdmissionDeclaration();
        var options = new ChatOptions
        {
            Instructions = null,
            AllowMultipleToolCalls = false,
            MaxOutputTokens = prepared.Dispatch.Profile.OutputTokenLimit,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [AliInternalModelRoutingProperties.SuppressInjectedPersona] = true,
                [AliInternalModelRoutingProperties.BoundReasoningEffort] =
                    prepared.Dispatch.GenerationSettingsBinding.ReasoningEffort
            }
        };

        if (prepared.UseNativeProtocol)
        {
            options.Tools = [protocol];
            options.ToolMode = ChatToolMode.RequireSpecific(
                AliCompletionCriticProtocol.SchemaName);
            options.ResponseFormat = null;
        }
        else
        {
            options.Tools = null;
            options.ToolMode = ChatToolMode.None;
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema(
                protocol.JsonSchema,
                AliCompletionCriticProtocol.SchemaName);
        }

        return options;
    }

    private static AliCompletionCriticVerdict Aggregate(
        IReadOnlyList<AliCompletionCriticVerdict> pages,
        AliCompletionCriticVerdict final)
    {
        var outcomes = pages
            .SelectMany(static verdict => verdict.MaterialUnmetOutcomes)
            .Concat(final.MaterialUnmetOutcomes)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (outcomes.Length > 64
            || outcomes.Sum(static value => (long)value.Length)
                > AliCompletionCriticVerdict.MaximumAggregateOutcomeCharacters)
        {
            throw new InvalidDataException(
                "The combined completion-critic verdict exceeds the typed outcome bound.");
        }

        var complete = final.Complete && pages.All(static verdict => verdict.Complete);
        var basis = complete || pages.All(static verdict => verdict.Complete)
            ? final.Basis
            : "One or more exact rendered-page audits rejected completion. The material unmet "
              + "outcomes below combine every page verdict with the final cross-page verdict; "
              + "page-audit prose was not promoted to evidence.";
        return new AliCompletionCriticVerdict(complete, basis, outcomes);
    }

    private static AliCompletionCriticFailure BindingFailure(
        IReadOnlyList<string>? changedBindings)
    {
        var exactChanged = changedBindings?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(9)
            .ToArray();
        var detail = exactChanged is { Length: > 0 }
            ? " Changed bindings: " + string.Join(", ", exactChanged) + "."
            : string.Empty;
        return new AliCompletionCriticFailure(
            AliCompletionCriticFailureKind.DispatchBindingsChanged,
            "Ali paused before contacting the completion critic because the exact accepted "
            + "runtime bindings could not be revalidated." + detail
            + " No critic model call ran, and no answer was approved or published.",
            exactChanged);
    }

    private static AliCompletionCriticFailure InputNotAdmitted(
        AliPlanningInputAdmissionResult admission)
    {
        var charge = admission.CalculatedInputCharge is { } calculated
            ? calculated.ToString("N0", CultureInfo.InvariantCulture) + " tokens"
            : "not calculated safely";
        return new AliCompletionCriticFailure(
            AliCompletionCriticFailureKind.InputNotAdmitted,
            "Ali paused before contacting the completion critic because the complete exact review "
            + "input was not admitted. Configured context: "
            + admission.ConfiguredContextTokens.ToString("N0", CultureInfo.InvariantCulture)
            + " tokens; configured output reserve: "
            + admission.ConfiguredOutputTokenLimit.ToString("N0", CultureInfo.InvariantCulture)
            + " tokens; calculated input charge: " + charge
            + "; counter mode: " + admission.CounterMode
            + ". No review input was truncated, no critic model call ran for that projection, and "
            + "no answer was approved or published.");
    }

    private static AliCompletionCriticFailure OutputFailure(string message) =>
        new(AliCompletionCriticFailureKind.OutputIncomplete, message);

    private static AliCompletionCriticFailure StoreFailure(string message) =>
        new(AliCompletionCriticFailureKind.ReviewStoreFailure, message);

    private sealed record CallResult(
        AliCompletionCriticVerdict? Verdict,
        AliCompletionCriticFailure? Failure)
    {
        internal static CallResult Completed(AliCompletionCriticVerdict verdict) =>
            new(verdict, Failure: null);

        internal static CallResult Failed(AliCompletionCriticFailure failure) =>
            new(Verdict: null, failure);
    }
}
