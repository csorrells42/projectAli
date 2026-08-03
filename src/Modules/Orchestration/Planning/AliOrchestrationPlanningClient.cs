using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.Runtime;
using Ali.Modules.Runtime.Models;
using Ali.Modules.ToolDiscovery;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Modules.Orchestration.Planning;

internal sealed record PlanningWorkGraphConsumerDiagnostics(
    long FingerprintReads,
    long AnalysisCacheMisses,
    long FullDigestConstructionPasses,
    long FullDigestNodesVisited);

/// <summary>
/// A state-projection planner. Framework chat history is used only to observe correlated tool
/// terminals; every model pass is rebuilt from the immutable request and accepted state.
/// </summary>
internal sealed class AliOrchestrationPlanningClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly Func<bool> _supportsNativeToolCalls;
    private readonly Func<ModelProfile> _modelProfileAccessor;
    private readonly Func<BoundModelDispatchSnapshot>? _boundDispatchAccessor;
    private readonly Func<BoundModelDispatchSnapshot, TurnRuntimeBindings>? _dispatchBindingsFactory;
    private readonly AliPlanningInputAdmission _inputAdmission;
    private readonly ISemanticToolCatalog _semanticToolCatalog;
    private readonly AliStateBackedChatHistoryAdapter _historyAdapter;
    private readonly OrchestrationDecisionValidator _validator;
    private readonly TemporaryCompletionBridge? _completionBridge;
    private readonly Func<string, Dictionary<string, object?>, Dictionary<string, object?>> _toolArgumentNormalizer;
    private readonly Func<AliCompletedToolOutcomeRequest, PlanningToolDomainOutcome>?
        _completedToolOutcomeClassifier;
    private readonly Func<CoordinatorTurnContext, string, string> _finalAnswerRenderer;
    private readonly SemaphoreSlim _planningGate = new(1, 1);
    private ActivePlanningTurn? _activeTurn;

    internal AliOrchestrationPlanningClient(
        IChatClient inner,
        Func<bool> supportsNativeToolCalls,
        Func<ModelProfile> modelProfileAccessor,
        ISemanticToolCatalog? semanticToolCatalog = null,
        AliStateBackedChatHistoryAdapter? historyAdapter = null,
        OrchestrationDecisionValidator? validator = null,
        TemporaryCompletionBridge? completionBridge = null,
        Func<string, Dictionary<string, object?>, Dictionary<string, object?>>? toolArgumentNormalizer = null,
        Func<AliCompletedToolOutcomeRequest, PlanningToolDomainOutcome>?
            completedToolOutcomeClassifier = null,
        Func<CoordinatorTurnContext, string, string>? finalAnswerRenderer = null,
        AliPlanningInputAdmission? inputAdmission = null,
        Func<BoundModelDispatchSnapshot>? boundDispatchAccessor = null,
        Func<BoundModelDispatchSnapshot, TurnRuntimeBindings>? dispatchBindingsFactory = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _supportsNativeToolCalls = supportsNativeToolCalls
            ?? throw new ArgumentNullException(nameof(supportsNativeToolCalls));
        _modelProfileAccessor = modelProfileAccessor
            ?? throw new ArgumentNullException(nameof(modelProfileAccessor));
        if ((boundDispatchAccessor is null) != (dispatchBindingsFactory is null))
        {
            throw new ArgumentException(
                "A bound planning dispatch accessor and its exact binding factory must be configured together.");
        }

        _boundDispatchAccessor = boundDispatchAccessor;
        _dispatchBindingsFactory = dispatchBindingsFactory;
        _inputAdmission = inputAdmission ?? new AliPlanningInputAdmission();
        _semanticToolCatalog = semanticToolCatalog ?? new RegistryOnlySemanticToolCatalog();
        _historyAdapter = historyAdapter ?? new AliStateBackedChatHistoryAdapter();
        _validator = validator ?? new OrchestrationDecisionValidator();
        _completionBridge = completionBridge;
        _toolArgumentNormalizer = toolArgumentNormalizer ?? ((_, arguments) => arguments);
        _completedToolOutcomeClassifier = completedToolOutcomeClassifier;
        _finalAnswerRenderer = finalAnswerRenderer ?? ((_, answer) => answer);
    }

    internal IDisposable BeginTurn(
        CoordinatorTurnContext turn,
        AliPlanningTurnInput authoritativeInput,
        IAliPlanningTransitionObserver transitionObserver,
        AliPlanningAttachmentProjection? attachmentProjection = null,
        TurnIdentity? durableIdentity = null,
        string? immutableOriginalRequest = null)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(authoritativeInput);
        ArgumentNullException.ThrowIfNull(transitionObserver);
        var exactOriginalRequest = immutableOriginalRequest ?? turn.OriginalUserText;
        ArgumentException.ThrowIfNullOrWhiteSpace(exactOriginalRequest);
        var active = new ActivePlanningTurn(
            turn,
            authoritativeInput,
            transitionObserver,
            attachmentProjection ?? AliPlanningAttachmentProjection.Empty,
            durableIdentity ?? turn.ObservationIdentity ?? new TurnIdentity(
                "ali-local-planning-context",
                turn.ConversationId,
                turn.AssistantMessageId),
            exactOriginalRequest);
        var existing = Interlocked.CompareExchange(ref _activeTurn, active, null);
        if (existing is not null)
        {
            throw new InvalidOperationException(
                "Ali's orchestration planning client already has an active visible turn.");
        }

        return new ActiveTurnScope(this, active);
    }

    internal AliPreparedFinalPublication RequirePreparedFinalPublication() =>
        CurrentTurn().RequirePreparedFinalPublication();

    internal AliPreparedInterimResponse? PreparedInterimResponse =>
        CurrentTurn().PreparedInterimResponse;

    internal PlanningWorkGraphConsumerDiagnostics CaptureWorkGraphConsumerDiagnostics() =>
        CurrentTurn().CaptureWorkGraphConsumerDiagnostics();

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var frameworkMessages = messages.ToArray();
        await _planningGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var active = CurrentTurn();
            var liveTools = SnapshotTaskTools(options);
            active.SetLiveTools(liveTools);
            await ObserveCorrelatedToolResultsAsync(
                active,
                frameworkMessages,
                cancellationToken).ConfigureAwait(false);

            var allowNativeProtocol = true;
            var invalidDraftCount = 0;
            var compatibilityFailureFingerprints = new HashSet<string>(StringComparer.Ordinal);
            var unchangedExpansionFingerprints = new HashSet<string>(StringComparer.Ordinal);
            var unchangedBlockedActionFingerprints = new HashSet<string>(StringComparer.Ordinal);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var passExpectedRevision = active.StateRevision;
                PlanningPassDispatch? dispatch = null;
                string? dispatchCaptureFailure = null;
                try
                {
                    dispatch = CapturePlanningPassDispatch();
                }
                catch (Exception exception) when (exception is not OperationCanceledException
                                                  and not OutOfMemoryException)
                {
                    dispatchCaptureFailure = "dispatch envelope unavailable ("
                        + exception.GetType().Name + ")";
                }

                var passAuthorization = await active.Observer.OnPlanningPassStartingAsync(
                    new AliPlanningPassStartingEvent(
                        active.DurableIdentity.ConversationId,
                        active.DurableIdentity.AssistantMessageId,
                        passExpectedRevision,
                        dispatch?.Bindings),
                    cancellationToken).ConfigureAwait(false);
                if (passAuthorization.StateRevision < passExpectedRevision
                    || (passAuthorization.CanPlan
                        && passAuthorization.StateRevision != passExpectedRevision))
                {
                    throw new InvalidOperationException(
                        "The durable planning-pass authorization returned an invalid state revision.");
                }
                active.ApplyPlanningPassAuthorization(passAuthorization);
                if (dispatch is null || !passAuthorization.CanPlan)
                {
                    var changed = passAuthorization.ChangedBindings is { Count: > 0 }
                        ? string.Join(", ", passAuthorization.ChangedBindings.Take(9))
                        : dispatchCaptureFailure ?? "unknown binding";
                    return await PrepareInterimResponseAsync(
                        active,
                        new ChatResponse(new ChatMessage(
                            ChatRole.Assistant,
                            "Ali paused this turn because its runtime bindings changed: " + changed
                            + ". The request was preserved and no further action ran.")),
                        "Ali paused this turn because its runtime bindings changed: " + changed
                        + ". The request was preserved and no further action ran.",
                        AliPlanningInterimKind.RuntimeSuspended,
                        cancellationToken).ConfigureAwait(false);
                }

                var useNative = allowNativeProtocol && dispatch.SupportsNativeToolCalls;
                var selectedTools = active.SelectedTools();
                var protocol = AliOrchestrationProtocol.CreateDeclaration(selectedTools);
                var planningMessages = _historyAdapter.BuildMessages(
                    active.ImmutableOriginalRequest,
                    active.SnapshotInput(),
                    active.CapabilityDirectory,
                    selectedTools,
                    active.AttachmentProjection);
                var planningOptions = CreatePlanningOptions(
                    options,
                    protocol,
                    useNative,
                    dispatch.Profile.OutputTokenLimit,
                    dispatch.BoundReasoningEffort);
                var admission = _inputAdmission.Evaluate(
                    dispatch.Profile,
                    planningMessages,
                    selectedTools,
                    protocol);
                if (!admission.IsAdmitted)
                {
                    var message = admission.ToUserVisibleMessage();
                    return await PrepareInterimResponseAsync(
                        active,
                        new ChatResponse(new ChatMessage(ChatRole.Assistant, message)),
                        message,
                        AliPlanningInterimKind.ModelInputNotAdmitted,
                        cancellationToken).ConfigureAwait(false);
                }

                ChatResponse response;
                try
                {
                    response = await dispatch.ChatClient
                        .GetResponseAsync(planningMessages, planningOptions, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var failureFingerprint = FailureFingerprint(
                        useNative ? "native-transport" : "json-transport",
                        exception.GetType().FullName ?? exception.GetType().Name);
                    if (useNative)
                    {
                        allowNativeProtocol = false;
                        continue;
                    }

                    return await SuspendPlanningAsync(
                        active,
                        source: null,
                        failureFingerprint,
                        cancellationToken).ConfigureAwait(false);
                }

                if (!IsPlanningResponseComplete(response, useNative))
                {
                    var failureFingerprint = FailureFingerprint(
                        useNative ? "native-incomplete-output" : "json-incomplete-output",
                        response.FinishReason?.ToString() ?? "null",
                        SafeResponseFingerprintMaterial(response));
                    return await SuspendPlanningAsync(
                        active,
                        source: null,
                        failureFingerprint,
                        cancellationToken,
                        reasonCode: "planner-output-incomplete",
                        visibleMessage:
                        "Ali paused this turn because the model returned an incomplete orchestration response. The partial response was discarded, the request was preserved, and no proposed action ran.")
                        .ConfigureAwait(false);
                }

                var decoded = useNative
                    ? AliOrchestrationDecisionDecoder.DecodeNative(response)
                    : AliOrchestrationDecisionDecoder.DecodeCompatibility(response);
                if (!decoded.IsSuccess)
                {
                    // Rejected drafts never enter authoritative state, model history, or Activity.
                    var fingerprint = FailureFingerprint(
                        useNative ? "native-decode" : "json-decode",
                        SafeResponseFingerprintMaterial(response),
                        decoded.Error ?? string.Empty);
                    invalidDraftCount++;
                    if (useNative)
                    {
                        allowNativeProtocol = false;
                        continue;
                    }

                    if (!compatibilityFailureFingerprints.Add(fingerprint)
                        || invalidDraftCount >= 4)
                    {
                        return await SuspendPlanningAsync(
                            active,
                            response,
                            fingerprint,
                            cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                var decision = NormalizeCallToolDecision(decoded.Decision!);
                var resolvedEvidence = await active.ResolveEvidenceAsync(
                    decision,
                    cancellationToken).ConfigureAwait(false);
                var validation = _validator.Validate(
                    decision,
                    active.ValidationContext(selectedTools, resolvedEvidence));
                if (!validation.IsValid)
                {
                    // Rebuild the same authoritative projection. Do not append the rejected draft
                    // or a model-visible repair transcript.
                    var fingerprint = FailureFingerprint(
                        useNative ? "native-validation" : "json-validation",
                        SafeResponseFingerprintMaterial(response),
                        string.Join("|", validation.Errors));
                    invalidDraftCount++;
                    if (useNative)
                    {
                        allowNativeProtocol = false;
                        continue;
                    }

                    if (!compatibilityFailureFingerprints.Add(fingerprint)
                        || invalidDraftCount >= 4)
                    {
                        return await SuspendPlanningAsync(
                            active,
                            response,
                            fingerprint,
                            cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                if (decision.NextAction is ExpandToolsAction expand)
                {
                    var beforeSelection = active.SelectedToolFingerprint();
                    var beforeMaterial = active.AuthoritativeMaterialFingerprint();
                    await AcceptDecisionAsync(
                        active,
                        decision,
                        callId: null,
                        toolName: null,
                        requireRevisionAdvance: false,
                        cancellationToken).ConfigureAwait(false);
                    var selection = await _semanticToolCatalog.SelectAsync(
                        expand.Need,
                        liveTools,
                        active.RetainedToolNames(),
                        cancellationToken).ConfigureAwait(false);
                    active.ApplySelection(selection);
                    var selectionChanged = !string.Equals(
                        beforeSelection,
                        active.SelectedToolFingerprint(),
                        StringComparison.Ordinal);
                    var materialChanged = !string.Equals(
                        beforeMaterial,
                        active.AuthoritativeMaterialFingerprint(),
                        StringComparison.Ordinal);
                    if (!selectionChanged && !materialChanged)
                    {
                        var fingerprint = FailureFingerprint(
                            "expand-tools-no-change",
                            expand.Need,
                            beforeSelection,
                            beforeMaterial);
                        if (!unchangedExpansionFingerprints.Add(fingerprint))
                        {
                            return await SuspendPlanningAsync(
                                active,
                                response,
                                fingerprint,
                                cancellationToken,
                                reasonCode: "planner-expansion-made-no-change",
                                visibleMessage:
                                "Ali paused this turn because the model repeatedly opened the same tool group without adding a usable tool or changing the accepted work. The request was preserved and no further action ran.")
                                .ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        unchangedExpansionFingerprints.Clear();
                        unchangedBlockedActionFingerprints.Clear();
                        invalidDraftCount = 0;
                        compatibilityFailureFingerprints.Clear();
                    }

                    allowNativeProtocol = true;
                    continue;
                }

                var beforeActionSelection = active.SelectedToolFingerprint();
                var beforeActionMaterial = active.AuthoritativeMaterialFingerprint();
                var decisionFingerprint = StableDecisionFingerprint(decision);
                var completionDossier = decision.NextAction is BeginCompletionAction
                    ? active.BuildCompletionDossier(decision, resolvedEvidence)
                    : null;
                var translated = await TranslateAcceptedDecisionAsync(
                    active,
                    decision,
                    response,
                    completionDossier,
                    cancellationToken).ConfigureAwait(false);
                if (translated is null)
                {
                    var selectionChanged = !string.Equals(
                        beforeActionSelection,
                        active.SelectedToolFingerprint(),
                        StringComparison.Ordinal);
                    var materialChanged = !string.Equals(
                        beforeActionMaterial,
                        active.AuthoritativeMaterialFingerprint(),
                        StringComparison.Ordinal);
                    if (!selectionChanged
                        && !materialChanged
                        && decision.NextAction is CallToolAction)
                    {
                        var fingerprint = FailureFingerprint(
                            "blocked-call-no-change",
                            decisionFingerprint,
                            beforeActionSelection,
                            beforeActionMaterial);
                        if (!unchangedBlockedActionFingerprints.Add(fingerprint))
                        {
                            return await SuspendPlanningAsync(
                                active,
                                response,
                                fingerprint,
                                cancellationToken,
                                reasonCode: "planner-blocked-action-repeated",
                                visibleMessage:
                                "Ali paused this turn because the model repeatedly proposed the same blocked action without changing the accepted work or tool selection. The request was preserved and the blocked action was not repeated.")
                                .ConfigureAwait(false);
                        }
                    }
                    else if (selectionChanged || materialChanged)
                    {
                        unchangedExpansionFingerprints.Clear();
                        unchangedBlockedActionFingerprints.Clear();
                        invalidDraftCount = 0;
                        compatibilityFailureFingerprints.Clear();
                    }

                    allowNativeProtocol = true;
                    continue;
                }

                return translated;
            }
        }
        finally
        {
            _planningGate.Release();
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null && serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return _inner.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
        _planningGate.Dispose();
        // The composition root owns the shared model client.
    }

    private async Task<ChatResponse?> TranslateAcceptedDecisionAsync(
        ActivePlanningTurn active,
        OrchestrationDecision decision,
        ChatResponse source,
        CompletionDossier? completionDossier,
        CancellationToken cancellationToken)
    {
        switch (decision.NextAction)
        {
            case CallToolAction call:
            {
                var callId = $"call_{Guid.NewGuid():N}";
                var receipt = await AcceptDecisionAsync(
                    active,
                    decision,
                    callId,
                    call.ToolName,
                    requireRevisionAdvance: true,
                    cancellationToken).ConfigureAwait(false);
                if (receipt.RequiresFreshPlanningPass)
                {
                    return null;
                }
                var tool = active.RequireLiveTool(call.ToolName);
                RegisterToolPlanAndReportAcceptedSelection(active.Turn, tool, callId, call);
                var arguments = call.Arguments.ToDictionary(
                    pair => pair.Key,
                    pair => (object?)pair.Value.Clone(),
                    StringComparer.Ordinal);
                active.RegisterAcceptedCall(callId, tool.Name, arguments, DateTimeOffset.UtcNow);
                return CopyMetadata(source, new FunctionCallContent(callId, tool.Name, arguments));
            }
            case AnswerDirectlyAction answer:
                await AcceptDecisionAsync(
                    active,
                    decision,
                    callId: null,
                    toolName: null,
                    requireRevisionAdvance: false,
                    cancellationToken).ConfigureAwait(false);
                return await PreparePublicationAsync(
                    active,
                    CopyMetadata(source, new TextContent(answer.Answer)),
                    answer.Answer,
                    cancellationToken).ConfigureAwait(false);
            case RequestUserInputAction request:
                await AcceptDecisionAsync(
                    active,
                    decision,
                    callId: null,
                    toolName: null,
                    requireRevisionAdvance: true,
                    cancellationToken).ConfigureAwait(false);
                return await PrepareInterimResponseAsync(
                    active,
                    CopyMetadata(source, new TextContent(request.Question)),
                    request.Question,
                    AliPlanningInterimKind.AwaitingUser,
                    cancellationToken).ConfigureAwait(false);
            case AwaitExternalEventAction wait:
                await AcceptDecisionAsync(
                    active,
                    decision,
                    callId: null,
                    toolName: null,
                    requireRevisionAdvance: true,
                    cancellationToken).ConfigureAwait(false);
                return await PrepareInterimResponseAsync(
                    active,
                    CopyMetadata(source, new TextContent(wait.WaitingFor)),
                    wait.WaitingFor,
                    AliPlanningInterimKind.AwaitingExternalEvent,
                    cancellationToken).ConfigureAwait(false);
            case BeginCompletionAction:
                if (_completionBridge is null)
                {
                    throw new InvalidOperationException(
                        "BeginCompletion requires the explicitly configured TemporaryCompletionBridge until the state-native answer composer is installed.");
                }

                var dossier = completionDossier
                    ?? throw new InvalidOperationException(
                        "An accepted completion requires its exact bounded evidence dossier.");
                var completionReceipt = await AcceptDecisionAsync(
                    active,
                    decision,
                    callId: null,
                    toolName: null,
                    requireRevisionAdvance: true,
                    cancellationToken).ConfigureAwait(false);
                if (completionReceipt.RequiresFreshPlanningPass)
                {
                    return null;
                }

                var completionAttempt = await _completionBridge.CompleteAsync(
                    new TemporaryCompletionRequest(
                        active.ImmutableOriginalRequest,
                        active.SnapshotInput(),
                        decision,
                        source,
                        dossier.RequiredOutcomes,
                        dossier.RequiredClaims,
                        dossier.CitedEvidence),
                    cancellationToken).ConfigureAwait(false);
                if (!completionAttempt.IsSuccessful)
                {
                    var failure = completionAttempt.Failure
                        ?? throw new InvalidOperationException(
                            "The failed completion attempt did not provide its typed reason.");
                    return await PrepareInterimResponseAsync(
                        active,
                        new ChatResponse(new ChatMessage(
                            ChatRole.Assistant,
                            failure.UserVisibleMessage)),
                        failure.UserVisibleMessage,
                        MapCompletionFailure(failure.Kind),
                        cancellationToken).ConfigureAwait(false);
                }

                var completed = completionAttempt.Response
                    ?? throw new InvalidOperationException(
                        "The successful completion attempt did not provide a response.");
                return await PreparePublicationAsync(
                    active,
                    completed,
                    completed.Text ?? string.Empty,
                    cancellationToken).ConfigureAwait(false);
            default:
                throw new InvalidOperationException(
                    "An accepted orchestration action did not have a translation boundary.");
        }
    }

    private OrchestrationDecision NormalizeCallToolDecision(OrchestrationDecision decision)
    {
        if (decision.NextAction is not CallToolAction call)
        {
            return decision;
        }

        var input = call.Arguments.ToDictionary(
            pair => pair.Key,
            pair => (object?)pair.Value.Clone(),
            StringComparer.Ordinal);
        var normalized = _toolArgumentNormalizer(call.ToolName, input)
            ?? throw new InvalidOperationException("The tool argument normalizer returned null.");
        var normalizedElements = normalized.ToDictionary(
            pair => pair.Key,
            pair => JsonSerializer.SerializeToElement(pair.Value),
            StringComparer.Ordinal);
        return new OrchestrationDecision(
            decision.WorkUpdate,
            decision.MaterialClaims,
            new CallToolAction(
                call.ToolName,
                normalizedElements,
                call.Need,
                call.ExpectedProgress));
    }

    private async Task<ChatResponse> SuspendPlanningAsync(
        ActivePlanningTurn active,
        ChatResponse? source,
        string failureFingerprint,
        CancellationToken cancellationToken,
        string reasonCode = "planner-protocol-invalid",
        string? visibleMessage = null)
    {
        var expectedRevision = active.StateRevision;
        var receipt = await active.Observer.OnPlanningSuspendedAsync(
            new AliPlanningSuspendedEvent(
                active.DurableIdentity.ConversationId,
                active.DurableIdentity.AssistantMessageId,
                expectedRevision,
                reasonCode,
                failureFingerprint),
            cancellationToken).ConfigureAwait(false);
        if (receipt.StateRevision < expectedRevision)
        {
            throw new InvalidOperationException(
                "The durable transition observer did not commit the suspended-runtime state.");
        }

        active.ApplySuspensionReceipt(receipt);
        var message = visibleMessage
            ?? "Ali paused this turn because the local model did not return a valid orchestration decision in either supported protocol mode. The request was preserved and no rejected action ran.";
        var suspendedResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, message));
        if (source is not null)
        {
            suspendedResponse.FinishReason = source.FinishReason;
            suspendedResponse.ModelId = source.ModelId;
            suspendedResponse.Usage = source.Usage;
        }

        return await PrepareInterimResponseAsync(
            active,
            suspendedResponse,
            message,
            AliPlanningInterimKind.ProtocolSuspended,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ChatResponse> PrepareInterimResponseAsync(
        ActivePlanningTurn active,
        ChatResponse response,
        string responseText,
        AliPlanningInterimKind kind,
        CancellationToken cancellationToken)
    {
        var renderedResponse = _finalAnswerRenderer(active.Turn, responseText);
        if (string.IsNullOrWhiteSpace(renderedResponse))
        {
            throw new InvalidOperationException(
                "A user-visible orchestration pause response cannot be empty.");
        }

        var digest = AliPlanningProjectionSafety.Digest(renderedResponse);
        var publicationIdentity = AliPlanningProjectionSafety.Digest(
            active.DurableIdentity.AssistantMessageId + "\0" + kind);
        var publicationId = "interim_" + publicationIdentity[..32];
        var expectedRevision = active.StateRevision;
        var receipt = await active.Observer.OnInterimResponsePreparedAsync(
            new AliPlanningInterimPreparedEvent(
                active.DurableIdentity.ConversationId,
                active.DurableIdentity.AssistantMessageId,
                expectedRevision,
                publicationId,
                kind,
                renderedResponse,
                digest),
            cancellationToken).ConfigureAwait(false);
        if (receipt.StateRevision <= expectedRevision)
        {
            throw new InvalidOperationException(
                "The durable transition observer did not prepare the exact interim response.");
        }

        active.ApplySuspensionReceipt(receipt);
        active.ApplyInterimResponse(new AliPreparedInterimResponse(
            active.DurableIdentity,
            publicationId,
            renderedResponse,
            digest,
            kind));
        return CopyMetadata(
            response,
            new TextContent(renderedResponse),
            ChatFinishReason.Stop);
    }

    private async Task<ChatResponse> PreparePublicationAsync(
        ActivePlanningTurn active,
        ChatResponse response,
        string answerText,
        CancellationToken cancellationToken)
    {
        var renderedAnswer = _finalAnswerRenderer(active.Turn, answerText);
        if (string.IsNullOrWhiteSpace(renderedAnswer))
        {
            throw new InvalidOperationException(
                "A user-visible orchestration response cannot be journaled as an empty publication.");
        }

        var expectedRevision = active.StateRevision;
        var answerDigest = AliPlanningProjectionSafety.Digest(renderedAnswer);
        var publicationId = $"publication_{active.Turn.AssistantMessageId}";
        var receipt = await active.Observer.OnFinalAnswerPreparedAsync(
            new AliPlanningPublicationPreparedEvent(
                active.DurableIdentity.ConversationId,
                active.DurableIdentity.AssistantMessageId,
                expectedRevision,
                publicationId,
                answerDigest,
                renderedAnswer,
                PublicationAssistantMessageId: active.Turn.AssistantMessageId),
            cancellationToken).ConfigureAwait(false);
        if (receipt.StateRevision <= expectedRevision
            || !string.Equals(receipt.PublicationId, publicationId, StringComparison.Ordinal)
            || !string.Equals(receipt.AnswerDigest, answerDigest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The durable transition observer did not prepare the exact final publication.");
        }

        active.ApplyPublicationReceipt(receipt, renderedAnswer);
        return CopyMetadata(
            response,
            new TextContent(renderedAnswer),
            ChatFinishReason.Stop);
    }

    private static string FailureFingerprint(params string[] components) =>
        AliPlanningProjectionSafety.Digest(string.Join("\n", components));

    private static string StableDecisionFingerprint(OrchestrationDecision decision)
    {
        var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(decision);
        try
        {
            return TurnStateIntegrity.Digest(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string SafeResponseFingerprintMaterial(ChatResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            return response.Text;
        }

        try
        {
            return JsonSerializer.Serialize(response.Messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>()
                .Select(call => new { call.Name, call.Arguments })
                .ToArray());
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return "unserializable-response:" + response.Messages.Count;
        }
    }

    private static ChatOptions CreatePlanningOptions(
        ChatOptions? source,
        AIFunctionDeclaration protocol,
        bool native,
        int outputTokenLimit,
        string? boundReasoningEffort)
    {
        var options = source?.Clone() ?? new ChatOptions();
        options.Instructions = null;
        options.AllowMultipleToolCalls = false;
        options.MaxOutputTokens = outputTokenLimit;
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        options.AdditionalProperties[
            AliInternalModelRoutingProperties.SuppressInjectedPersona] = true;
        if (!string.IsNullOrWhiteSpace(boundReasoningEffort))
        {
            options.AdditionalProperties[
                AliInternalModelRoutingProperties.BoundReasoningEffort] = boundReasoningEffort;
        }
        if (native)
        {
            options.Tools = [protocol];
            options.ToolMode = ChatToolMode.RequireSpecific(OrchestrationProtocolCapability.ToolName);
            options.ResponseFormat = null;
        }
        else
        {
            options.Tools = null;
            options.ToolMode = ChatToolMode.None;
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema(
                protocol.JsonSchema,
                "ali_orchestration_decision");
        }

        return options;
    }

    private PlanningPassDispatch CapturePlanningPassDispatch()
    {
        if (_boundDispatchAccessor is null)
        {
            var legacyProfile = _modelProfileAccessor()
                ?? throw new InvalidOperationException(
                    "The live model profile accessor returned no configured profile.");
            return new PlanningPassDispatch(
                _inner,
                legacyProfile with { },
                _supportsNativeToolCalls(),
                Bindings: null,
                BoundReasoningEffort: null);
        }

        var exact = _boundDispatchAccessor()
            ?? throw new InvalidOperationException(
                "The runtime returned no bound planning dispatch snapshot.");
        ArgumentNullException.ThrowIfNull(exact.ChatClient);
        ArgumentNullException.ThrowIfNull(exact.Profile);
        ArgumentNullException.ThrowIfNull(exact.RuntimeBinding);
        ArgumentNullException.ThrowIfNull(exact.ModelBinding);
        ArgumentNullException.ThrowIfNull(exact.GenerationSettingsBinding);
        var bindings = _dispatchBindingsFactory!(exact)
            ?? throw new InvalidOperationException(
                "The planning binding factory returned no exact binding snapshot.");
        bindings.Validate();
        var supportsNativeToolCalls = RequireBoundEngineeringProtocol(exact);
        return new PlanningPassDispatch(
            exact.ChatClient,
            exact.Profile,
            supportsNativeToolCalls,
            bindings,
            exact.GenerationSettingsBinding.ReasoningEffort);
    }

    internal static bool RequireBoundEngineeringProtocol(BoundModelDispatchSnapshot exact)
    {
        ArgumentNullException.ThrowIfNull(exact);
        var protocol = exact.GenerationSettingsBinding.ProtocolIdentity;
        if (!string.Equals(protocol, exact.RuntimeBinding.ProtocolIdentity, StringComparison.Ordinal)
            || !string.Equals(protocol, exact.Profile.ProtocolIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The bound runtime, model profile, and generation settings disagree about the engineering protocol identity.");
        }

        var capabilityIdentity = exact.RuntimeBinding.CapabilityProfileIdentity;
        if (string.Equals(capabilityIdentity, "unprobed", StringComparison.Ordinal)
            || !string.Equals(
                capabilityIdentity,
                exact.ModelBinding.CapabilityProfileIdentity,
                StringComparison.Ordinal)
            || !string.Equals(
                capabilityIdentity,
                exact.Profile.CapabilityProfileIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Autonomous engineering requires one exact, functionally probed capability profile bound across the runtime, model, and generation settings.");
        }

        if (string.Equals(protocol, RuntimeProtocolIdentities.NativeOpenAiTools, StringComparison.Ordinal))
        {
            if (!exact.Profile.SupportsToolCalls || !exact.ModelBinding.SupportsToolCalls)
            {
                throw new InvalidOperationException(
                    "The bound native-tool protocol is not enabled consistently by the exact model profile.");
            }
            return true;
        }

        if (string.Equals(protocol, RuntimeProtocolIdentities.StructuredDecision, StringComparison.Ordinal))
        {
            return false;
        }

        throw new InvalidOperationException(
            "Autonomous engineering is disabled because neither native tools nor Ali's validated structured-decision protocol was functionally proven for the exact endpoint/model binding.");
    }

    private static bool IsPlanningResponseComplete(ChatResponse response, bool native)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.FinishReason == ChatFinishReason.Stop)
        {
            return true;
        }

        if (!native || response.FinishReason != ChatFinishReason.ToolCalls)
        {
            return false;
        }

        var calls = response.Messages
            .SelectMany(static message => message.Contents)
            .OfType<FunctionCallContent>()
            .Where(static call => !call.InformationalOnly)
            .ToArray();
        return calls.Length == 1
            && string.Equals(
                calls[0].Name,
                OrchestrationProtocolCapability.ToolName,
                StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(calls[0].CallId);
    }

    private static AliPlanningInterimKind MapCompletionFailure(
        TemporaryCompletionFailureKind failureKind) =>
        failureKind switch
        {
            TemporaryCompletionFailureKind.CompletionInputNotAdmitted =>
                AliPlanningInterimKind.CompletionInputNotAdmitted,
            TemporaryCompletionFailureKind.CompletionDispatchBindingsChanged =>
                AliPlanningInterimKind.CompletionDispatchBindingsChanged,
            TemporaryCompletionFailureKind.CompletionOutputIncomplete =>
                AliPlanningInterimKind.CompletionOutputIncomplete,
            _ => throw new InvalidOperationException(
                "The completion failure kind is invalid.")
        };

    private static IReadOnlyList<AIFunctionDeclaration> SnapshotTaskTools(ChatOptions? options) =>
        options?.Tools?
            .OfType<AIFunctionDeclaration>()
            .Where(tool => !string.Equals(
                tool.Name,
                OrchestrationProtocolCapability.ToolName,
                StringComparison.Ordinal))
            .GroupBy(tool => tool.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray() ?? [];

    private static async ValueTask<AliPlanningTransitionReceipt> AcceptDecisionAsync(
        ActivePlanningTurn active,
        OrchestrationDecision decision,
        string? callId,
        string? toolName,
        bool requireRevisionAdvance,
        CancellationToken cancellationToken)
    {
        var expectedRevision = active.StateRevision;
        requireRevisionAdvance = requireRevisionAdvance
            || decision.WorkUpdate is not null
            || decision.MaterialClaims.Count > 0;
        var receipt = await active.Observer.OnDecisionAcceptedAsync(
            new AliPlanningDecisionAcceptedEvent(
                active.DurableIdentity.ConversationId,
                active.DurableIdentity.AssistantMessageId,
                expectedRevision,
                decision,
                callId,
                toolName),
            cancellationToken).ConfigureAwait(false);
        if (receipt.StateRevision < expectedRevision
            || (requireRevisionAdvance && receipt.StateRevision <= expectedRevision))
        {
            throw new InvalidOperationException(
                "The durable transition observer did not confirm the required authoritative revision.");
        }

        active.ApplyDecisionReceipt(decision, receipt);
        return receipt;
    }

    private static void RegisterToolPlanAndReportAcceptedSelection(
        CoordinatorTurnContext turn,
        AIFunctionDeclaration tool,
        string callId,
        CallToolAction call)
    {
        var displayName = ResolveUserFacingToolName(tool);
        var visibleNeed = VisibleActivityText(call.Need, tool.Name, displayName);
        var visibleProgress = VisibleActivityText(call.ExpectedProgress, tool.Name, displayName);
        var selectionHeadline = $"{visibleNeed} -> {displayName}";
        var resultHeadline = $"{visibleNeed} -> {visibleProgress}";
        var technicalArguments = AliPlanningProjectionSafety.BoundAndRedactText(
            JsonSerializer.Serialize(call.Arguments));
        turn.RegisterToolPlan(new CoordinatorToolPlan(
            callId,
            tool.Name,
            visibleNeed,
            visibleNeed,
            visibleProgress,
            selectionHeadline,
            resultHeadline,
            technicalArguments));
        try
        {
            turn.Report(
                AgentActivityKind.ToolCall,
                selectionHeadline,
                $"Next: {visibleProgress}");
        }
        catch
        {
            // Presentation is non-authoritative and must not prevent a prepared tool call.
        }
    }

    private static string ResolveUserFacingToolName(AIFunctionDeclaration tool)
    {
        try
        {
            if (tool is AIFunction function)
            {
                var displayName = function
                    .GetService<ActivityReportingAIFunction>()?
                    .UserFacingDisplayName;
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    return displayName;
                }
            }
        }
        catch
        {
            // A display-name service cannot change task-tool identity or execution.
        }

        var words = tool.Name.Replace('_', ' ').Replace('-', ' ');
        var normalized = string.Join(
            " ",
            words.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? "tool" : normalized;
    }

    private static string VisibleActivityText(string value, string internalName, string displayName)
    {
        var visible = value.Replace(internalName, displayName, StringComparison.Ordinal)
            .ReplaceLineEndings(" ")
            .Trim();
        return visible.Length <= 500 ? visible : visible[..500];
    }

    private static ChatResponse CopyMetadata(
        ChatResponse source,
        AIContent content,
        ChatFinishReason? finishReason = null)
    {
        var message = new ChatMessage(ChatRole.Assistant, string.Empty);
        message.Contents.Add(content);
        return new ChatResponse(message)
        {
            // Planner protocol finish reasons describe the model's protocol envelope, not the
            // synthesized response returned to Agent Framework. Prepared user-visible text is an
            // explicitly complete terminal response; real FunctionCallContent keeps the source
            // finish reason so the framework can execute that exact accepted call.
            FinishReason = finishReason ?? source.FinishReason,
            ModelId = source.ModelId,
            Usage = source.Usage,
            RawRepresentation = source.RawRepresentation
        };
    }

    private async Task ObserveCorrelatedToolResultsAsync(
        ActivePlanningTurn active,
        IReadOnlyList<ChatMessage> frameworkMessages,
        CancellationToken cancellationToken)
    {
        foreach (var result in frameworkMessages
                     .SelectMany(message => message.Contents)
                     .OfType<FunctionResultContent>())
        {
            if (!active.TryBeginObserving(
                    result.CallId,
                    out var acceptedCall,
                    out var pendingObservedEvent))
            {
                continue;
            }

            try
            {
                var observedEvent = pendingObservedEvent;
                if (observedEvent is null)
                {
                    var disposition = FrameworkToolResultClassifier.Classify(result);
                    var captured = BoundedToolResultCapture.Capture(result);
                    var invocationStatus = disposition == FrameworkToolResultDisposition.InvocationFailed
                        ? result.Exception is OperationCanceledException
                            ? PlanningToolInvocationStatus.Cancelled
                            : PlanningToolInvocationStatus.Threw
                        : PlanningToolInvocationStatus.Returned;
                    var domainOutcome = disposition switch
                    {
                        FrameworkToolResultDisposition.InvocationFailed => PlanningToolDomainOutcome.Failed,
                        FrameworkToolResultDisposition.CapabilityBlockedBeforeInvocation => PlanningToolDomainOutcome.Denied,
                        FrameworkToolResultDisposition.ExternalOutcomeUnknown => PlanningToolDomainOutcome.Unreported,
                        _ when captured.Withheld => PlanningToolDomainOutcome.Unreported,
                        _ => ClassifyCompletedReturn(
                            active.DurableIdentity,
                            result.CallId,
                            acceptedCall!.ToolName,
                            result.Result)
                    };
                    var projection = AliPlanningProjectionSafety.ProjectResult(captured.Value);
                    var expectedRevision = active.StateRevision;
                    observedEvent = active.CacheObservedEvent(
                        result.CallId,
                        new AliPlanningToolResultObservedEvent(
                            active.DurableIdentity.ConversationId,
                            active.DurableIdentity.AssistantMessageId,
                            expectedRevision,
                            $"evidence_{result.CallId}",
                            result.CallId,
                            acceptedCall!.ToolName,
                            invocationStatus,
                            domainOutcome,
                            acceptedCall.Arguments.Clone(),
                            captured.Value,
                            acceptedCall.StartedAtUtc,
                            DateTimeOffset.UtcNow,
                            projection,
                            AliPlanningProjectionSafety.Digest(projection)));
                }

                var receipt = await active.Observer.OnToolResultObservedAsync(
                    observedEvent,
                    cancellationToken).ConfigureAwait(false);
                if (receipt.StateRevision <= observedEvent.ExpectedStateRevision
                    || string.IsNullOrWhiteSpace(receipt.EvidenceId))
                {
                    throw new InvalidOperationException(
                        "The durable transition observer did not commit the observed tool terminal.");
                }

                active.CommitObservedResult(
                    new AcceptedEvidenceProjection(
                        receipt.EvidenceId,
                        result.CallId,
                        observedEvent.ToolName,
                        observedEvent.InvocationStatus,
                        observedEvent.DomainOutcome,
                        observedEvent.BoundedRedactedProjection,
                        receipt.WorkItemId),
                    receipt);
            }
            catch
            {
                active.CancelObserving(result.CallId);
                throw;
            }
        }
    }

    private PlanningToolDomainOutcome ClassifyCompletedReturn(
        TurnIdentity durableIdentity,
        string callId,
        string toolName,
        object? result)
    {
        if (_completedToolOutcomeClassifier is null)
        {
            return PlanningToolDomainOutcome.Unreported;
        }

        try
        {
            return _completedToolOutcomeClassifier(new AliCompletedToolOutcomeRequest(
                durableIdentity,
                callId,
                toolName,
                result));
        }
        catch
        {
            // A classifier is evidence interpretation only. Failure cannot promote an
            // unreported terminal to success or change invocation behavior.
            return PlanningToolDomainOutcome.Unreported;
        }
    }

    private ActivePlanningTurn CurrentTurn() =>
        Volatile.Read(ref _activeTurn)
        ?? throw new InvalidOperationException(
            "BeginTurn must install an authoritative planning context before model execution.");

    private sealed class ActivePlanningTurn
    {
        private readonly object _sync = new();
        private readonly List<AcceptedEvidenceProjection> _evidence;
        private readonly HashSet<string> _fallbackKnownWorkItemIds;
        private readonly HashSet<string> _approvedExternalTicketIds;
        private readonly Dictionary<string, AIFunctionDeclaration> _liveTools = new(StringComparer.Ordinal);
        private readonly HashSet<string> _selectedToolNames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AcceptedCall> _acceptedCalls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AliPlanningToolResultObservedEvent> _pendingObservedEvents = new(StringComparer.Ordinal);
        private readonly HashSet<string> _observingCalls = new(StringComparer.Ordinal);
        private readonly IAliPlanningEvidenceAuthority? _evidenceAuthority;
        private string _stateProjection;
        private long _workGraphRevision;
        private WorkGraphSnapshot? _workGraph;
        private string _workIdentityFingerprint;
        private long _workGraphFingerprintReads;
        private long _workGraphAnalysisCacheMisses;
        private long _workGraphFullDigestConstructionPasses;
        private long _workGraphFullDigestNodesVisited;
        private string _liveToolFingerprint = string.Empty;
        private AliPreparedFinalPublication? _preparedFinalPublication;
        private AliPreparedInterimResponse? _preparedInterimResponse;

        internal ActivePlanningTurn(
            CoordinatorTurnContext turn,
            AliPlanningTurnInput input,
            IAliPlanningTransitionObserver observer,
            AliPlanningAttachmentProjection attachmentProjection,
            TurnIdentity durableIdentity,
            string immutableOriginalRequest)
        {
            Turn = turn;
            Observer = observer;
            AttachmentProjection = attachmentProjection;
            DurableIdentity = durableIdentity;
            ImmutableOriginalRequest = immutableOriginalRequest;
            StateRevision = input.StateRevision;
            _workGraphRevision = input.WorkGraphRevision;
            _workGraph = input.AuthoritativeWorkGraph;
            _stateProjection = input.StateProjection;
            PriorConversation = input.AcceptedPriorConversation.ToArray();
            _evidence = input.AcceptedEvidence
                .TakeLast(AliDurablePlanningTurn.MaximumRetainedEvidenceProjections)
                .ToList();
            _fallbackKnownWorkItemIds = input.AuthoritativeWorkGraph is null
                ? new HashSet<string>(input.KnownWorkItemIds, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            _workIdentityFingerprint = input.AuthoritativeWorkGraph is null
                ? FingerprintFallbackWorkItems(_fallbackKnownWorkItemIds)
                : FingerprintWorkGraph(input.AuthoritativeWorkGraph);
            _approvedExternalTicketIds = new HashSet<string>(
                input.ApprovedExternalTicketIds,
                StringComparer.Ordinal);
            _evidenceAuthority = observer as IAliPlanningEvidenceAuthority;
        }

        internal CoordinatorTurnContext Turn { get; }

        internal TurnIdentity DurableIdentity { get; }

        internal string ImmutableOriginalRequest { get; }

        internal IAliPlanningTransitionObserver Observer { get; }

        internal AliPlanningAttachmentProjection AttachmentProjection { get; }

        internal IReadOnlyList<AcceptedConversationContextEntry> PriorConversation { get; }

        internal long StateRevision { get; private set; }

        internal string CapabilityDirectory { get; private set; } = string.Empty;

        internal void SetLiveTools(IReadOnlyList<AIFunctionDeclaration> tools)
        {
            lock (_sync)
            {
                var fingerprint = FingerprintTools(tools);
                var inventoryChanged = !string.Equals(
                    fingerprint,
                    _liveToolFingerprint,
                    StringComparison.Ordinal);
                _liveTools.Clear();
                foreach (var tool in tools)
                {
                    _liveTools.Add(tool.Name, tool);
                }

                _selectedToolNames.RemoveWhere(name => !_liveTools.ContainsKey(name));
                if (inventoryChanged)
                {
                    CapabilityDirectory = LiveSemanticToolDirectory.BuildBoundedDirectoryFor(tools);
                    _liveToolFingerprint = fingerprint;
                }
            }
        }

        private static string FingerprintTools(IReadOnlyList<AIFunctionDeclaration> tools)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var tool in tools.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                Append(tool.Name);
                Append(tool.Description ?? string.Empty);
                Append(tool.JsonSchema.GetRawText());
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

            void Append(string value)
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                try
                {
                    hash.AppendData(bytes);
                    hash.AppendData([0]);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
        }

        private static string FingerprintFallbackWorkItems(IEnumerable<string> workItemIds) =>
            WorkIdentityCanonicalizer.SetDigest(
                "ali-planning-fallback-work-items-v1",
                workItemIds);

        private string FingerprintWorkGraph(WorkGraphSnapshot graph)
        {
            ArgumentNullException.ThrowIfNull(graph);
            _workGraphFingerprintReads++;
            var analysis = WorkGraphSnapshotAnalysisCache.GetOrCreate(graph, out var cacheHit);
            if (!analysis.IsValid)
            {
                throw new InvalidDataException(
                    "The planning client received an invalid authoritative work graph: "
                    + string.Join(" ", analysis.Errors));
            }

            if (!cacheHit)
            {
                _workGraphAnalysisCacheMisses++;
                _workGraphFullDigestConstructionPasses +=
                    analysis.Diagnostics.FullDigestConstructionPasses;
                _workGraphFullDigestNodesVisited +=
                    analysis.Diagnostics.FullDigestNodesVisited;
            }

            return analysis.PlanningIdentityDigest;
        }

        internal IReadOnlyList<AIFunctionDeclaration> SelectedTools()
        {
            lock (_sync)
            {
                return _selectedToolNames
                    .Where(_liveTools.ContainsKey)
                    .Select(name => _liveTools[name])
                    .OrderBy(tool => tool.Name, StringComparer.Ordinal)
                    .ToArray();
            }
        }

        internal string SelectedToolFingerprint()
        {
            lock (_sync)
            {
                return FingerprintTools(_selectedToolNames
                    .Where(_liveTools.ContainsKey)
                    .Select(name => _liveTools[name])
                    .ToArray());
            }
        }

        internal string AuthoritativeMaterialFingerprint()
        {
            lock (_sync)
            {
                return FailureFingerprint(
                    _workGraphRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _workIdentityFingerprint,
                    string.Join("\0", _evidence
                        .Select(item => item.EvidenceId)
                        .Order(StringComparer.Ordinal)));
            }
        }

        internal PlanningWorkGraphConsumerDiagnostics CaptureWorkGraphConsumerDiagnostics()
        {
            lock (_sync)
            {
                return new PlanningWorkGraphConsumerDiagnostics(
                    FingerprintReads: _workGraphFingerprintReads,
                    AnalysisCacheMisses: _workGraphAnalysisCacheMisses,
                    FullDigestConstructionPasses: _workGraphFullDigestConstructionPasses,
                    FullDigestNodesVisited: _workGraphFullDigestNodesVisited);
            }
        }

        internal void ApplySelection(SemanticToolSelection selection)
        {
            lock (_sync)
            {
                _selectedToolNames.Clear();
                foreach (var selected in selection.Tools)
                {
                    if (_liveTools.ContainsKey(selected.Name))
                    {
                        _selectedToolNames.Add(selected.Name);
                    }
                }

                foreach (var retained in _acceptedCalls.Values.Select(call => call.ToolName))
                {
                    if (_liveTools.ContainsKey(retained))
                    {
                        _selectedToolNames.Add(retained);
                    }
                }

                CapabilityDirectory = string.IsNullOrWhiteSpace(selection.Directory)
                    ? LiveSemanticToolDirectory.BuildBoundedDirectoryFor(_liveTools.Values.ToArray())
                    : selection.Directory;
            }
        }

        internal IReadOnlyCollection<string> RetainedToolNames()
        {
            lock (_sync)
            {
                return _acceptedCalls.Values.Select(call => call.ToolName)
                    .Concat(_selectedToolNames)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }
        }

        internal AIFunctionDeclaration RequireLiveTool(string name)
        {
            lock (_sync)
            {
                return _liveTools.TryGetValue(name, out var tool)
                    ? tool
                    : throw new InvalidOperationException(
                        "An accepted task tool disappeared before its prepared call was emitted.");
            }
        }

        internal void RegisterAcceptedCall(
            string callId,
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            DateTimeOffset startedAtUtc)
        {
            lock (_sync)
            {
                _acceptedCalls.Add(
                    callId,
                    new AcceptedCall(
                        toolName,
                        JsonSerializer.SerializeToElement(arguments).Clone(),
                        startedAtUtc));
            }
        }

        internal bool TryBeginObserving(
            string callId,
            out AcceptedCall? acceptedCall,
            out AliPlanningToolResultObservedEvent? pendingObservedEvent)
        {
            lock (_sync)
            {
                if (!_acceptedCalls.TryGetValue(callId, out acceptedCall)
                    || !_observingCalls.Add(callId))
                {
                    acceptedCall = null;
                    pendingObservedEvent = null;
                    return false;
                }

                _pendingObservedEvents.TryGetValue(callId, out pendingObservedEvent);
                return true;
            }
        }

        internal AliPlanningToolResultObservedEvent CacheObservedEvent(
            string callId,
            AliPlanningToolResultObservedEvent observedEvent)
        {
            lock (_sync)
            {
                if (!_observingCalls.Contains(callId)
                    || !_acceptedCalls.ContainsKey(callId))
                {
                    throw new InvalidOperationException(
                        "A tool terminal can only be cached while its accepted call is being observed.");
                }

                if (_pendingObservedEvents.TryGetValue(callId, out var existing))
                {
                    return existing;
                }

                _pendingObservedEvents.Add(callId, observedEvent);
                return observedEvent;
            }
        }

        internal sealed record AcceptedCall(
            string ToolName,
            JsonElement Arguments,
            DateTimeOffset StartedAtUtc);

        internal void CancelObserving(string callId)
        {
            lock (_sync)
            {
                _observingCalls.Remove(callId);
            }
        }

        internal void CommitObservedResult(
            AcceptedEvidenceProjection evidence,
            AliPlanningEvidenceReceipt receipt)
        {
            lock (_sync)
            {
                _observingCalls.Remove(evidence.CallId);
                _pendingObservedEvents.Remove(evidence.CallId);
                _acceptedCalls.Remove(evidence.CallId);
                _evidence.Add(evidence);
                if (_evidence.Count > AliDurablePlanningTurn.MaximumRetainedEvidenceProjections)
                {
                    _evidence.RemoveRange(
                        0,
                        _evidence.Count - AliDurablePlanningTurn.MaximumRetainedEvidenceProjections);
                }
                StateRevision = receipt.StateRevision;
                if (receipt.AuthoritativeStateProjection is not null)
                {
                    _stateProjection = receipt.AuthoritativeStateProjection;
                }
                if (receipt.WorkGraphRevision is { } workGraphRevision)
                {
                    _workGraphRevision = workGraphRevision;
                }
                if (receipt.AuthoritativeWorkGraph is not null)
                {
                    InstallAuthoritativeWorkGraphUnderLock(receipt.AuthoritativeWorkGraph);
                }
            }
        }

        internal void ApplyDecisionReceipt(
            OrchestrationDecision decision,
            AliPlanningTransitionReceipt receipt)
        {
            lock (_sync)
            {
                StateRevision = receipt.StateRevision;
                if (receipt.AuthoritativeStateProjection is not null)
                {
                    _stateProjection = receipt.AuthoritativeStateProjection;
                }
                if (receipt.WorkGraphRevision is { } workGraphRevision)
                {
                    _workGraphRevision = workGraphRevision;
                }
                if (receipt.AuthoritativeWorkGraph is not null)
                {
                    InstallAuthoritativeWorkGraphUnderLock(receipt.AuthoritativeWorkGraph);
                }

                if (decision.WorkUpdate is not null && _workGraph is null)
                {
                    var fallbackChanged = false;
                    foreach (var item in decision.WorkUpdate.Items)
                    {
                        fallbackChanged |= _fallbackKnownWorkItemIds.Add(item.WorkItemId);
                    }

                    if (fallbackChanged)
                    {
                        _workIdentityFingerprint = FingerprintFallbackWorkItems(
                            _fallbackKnownWorkItemIds);
                    }
                }
            }
        }

        internal void ApplyPlanningPassAuthorization(
            AliPlanningPassAuthorization authorization)
        {
            lock (_sync)
            {
                StateRevision = authorization.StateRevision;
                if (authorization.AuthoritativeStateProjection is not null)
                {
                    _stateProjection = authorization.AuthoritativeStateProjection;
                }
            }
        }

        internal void ApplySuspensionReceipt(AliPlanningTransitionReceipt receipt)
        {
            lock (_sync)
            {
                StateRevision = receipt.StateRevision;
                if (receipt.AuthoritativeStateProjection is not null)
                {
                    _stateProjection = receipt.AuthoritativeStateProjection;
                }
                if (receipt.WorkGraphRevision is { } workGraphRevision)
                {
                    _workGraphRevision = workGraphRevision;
                }
                if (receipt.AuthoritativeWorkGraph is not null)
                {
                    InstallAuthoritativeWorkGraphUnderLock(receipt.AuthoritativeWorkGraph);
                }
            }
        }

        internal void ApplyPublicationReceipt(
            AliPlanningPublicationReceipt receipt,
            string answerText)
        {
            lock (_sync)
            {
                StateRevision = receipt.StateRevision;
                if (receipt.AuthoritativeStateProjection is not null)
                {
                    _stateProjection = receipt.AuthoritativeStateProjection;
                }
                if (receipt.WorkGraphRevision is { } workGraphRevision)
                {
                    _workGraphRevision = workGraphRevision;
                }
                if (receipt.AuthoritativeWorkGraph is not null)
                {
                    InstallAuthoritativeWorkGraphUnderLock(receipt.AuthoritativeWorkGraph);
                }

                _preparedFinalPublication = new AliPreparedFinalPublication(
                    receipt.PublicationId,
                    Turn.AssistantMessageId,
                    answerText,
                    receipt.AnswerDigest);
            }
        }

        internal void ApplyInterimResponse(AliPreparedInterimResponse response)
        {
            ArgumentNullException.ThrowIfNull(response);
            lock (_sync)
            {
                if (_preparedFinalPublication is not null)
                {
                    throw new InvalidOperationException(
                        "A final publication and interim pause response cannot share one planning result.");
                }

                _preparedInterimResponse = response;
            }
        }

        internal AliPreparedInterimResponse? PreparedInterimResponse
        {
            get
            {
                lock (_sync)
                {
                    return _preparedInterimResponse;
                }
            }
        }

        internal AliPreparedFinalPublication RequirePreparedFinalPublication()
        {
            lock (_sync)
            {
                return _preparedFinalPublication
                    ?? throw new InvalidOperationException(
                        "The planning client has no prepared final publication for this turn.");
            }
        }

        internal AliPlanningTurnInput SnapshotInput()
        {
            lock (_sync)
            {
                return new AliPlanningTurnInput(
                    StateRevision,
                    _stateProjection,
                    PriorConversation,
                    _evidence,
                    _workGraph is null ? _fallbackKnownWorkItemIds : null,
                    _approvedExternalTicketIds,
                    _workGraphRevision,
                    _workGraph);
            }
        }

        internal async Task<IReadOnlyDictionary<string, AcceptedEvidenceProjection>>
            ResolveEvidenceAsync(
                OrchestrationDecision decision,
                CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(decision);
            var required = new HashSet<string>(StringComparer.Ordinal);
            if (decision.WorkUpdate is not null)
            {
                foreach (var item in decision.WorkUpdate.Items)
                {
                    AddResolvableEvidenceIds(required, item.EvidenceIds);
                }
            }

            foreach (var claim in decision.MaterialClaims)
            {
                AddResolvableEvidenceIds(required, claim.EvidenceIds);
            }

            if (decision.NextAction is BeginCompletionAction completion)
            {
                foreach (var binding in completion.Plan.Bindings)
                {
                    AddResolvableEvidenceIds(required, binding.EvidenceIds);
                }

                WorkGraphSnapshot? graph;
                lock (_sync)
                {
                    graph = _workGraph;
                }

                if (graph is not null)
                {
                    foreach (var terminal in graph.Nodes.Values.Where(static node =>
                                 node.Status is WorkNodeStatus.Satisfied
                                     or WorkNodeStatus.Impossible))
                    {
                        AddResolvableEvidenceIds(required, terminal.EvidenceIds);
                    }
                }
            }

            Dictionary<string, AcceptedEvidenceProjection> evidence;
            lock (_sync)
            {
                evidence = _evidence
                    .Where(item => required.Contains(item.EvidenceId))
                    .ToDictionary(
                        item => item.EvidenceId,
                        item => item,
                        StringComparer.Ordinal);
            }

            required.ExceptWith(evidence.Keys);
            if (required.Count == 0 || _evidenceAuthority is null)
            {
                return evidence;
            }

            var resolved = await _evidenceAuthority.ResolveEvidenceAsync(
                required,
                cancellationToken).ConfigureAwait(false);
            foreach (var (evidenceId, projection) in resolved)
            {
                if (!required.Contains(evidenceId)
                    || !string.Equals(
                        evidenceId,
                        projection.EvidenceId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The exact evidence authority returned an unrequested or mismatched record.");
                }

                evidence.Add(evidenceId, projection);
            }

            return evidence;
        }

        internal OrchestrationValidationContext ValidationContext(
            IReadOnlyList<AIFunctionDeclaration> selectedTools,
            IReadOnlyDictionary<string, AcceptedEvidenceProjection>? resolvedEvidence = null)
        {
            lock (_sync)
            {
                var evidenceProjections = _evidence.ToDictionary(
                    item => item.EvidenceId,
                    item => item,
                    StringComparer.Ordinal);
                foreach (var (evidenceId, projection) in resolvedEvidence
                             ?? new Dictionary<string, AcceptedEvidenceProjection>())
                {
                    if (evidenceProjections.TryGetValue(evidenceId, out var existing)
                        && existing != projection)
                    {
                        throw new InvalidDataException(
                            "The exact evidence authority disagrees with retained evidence state.");
                    }

                    evidenceProjections[evidenceId] = projection;
                }

                var evidenceOutcomes = evidenceProjections.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value.DomainOutcome,
                    StringComparer.Ordinal);

                return new OrchestrationValidationContext(
                    StateRevision,
                    selectedTools,
                    evidenceOutcomes.Keys,
                    _workGraph is null ? _fallbackKnownWorkItemIds : null,
                    _approvedExternalTicketIds,
                    evidenceOutcomes,
                    _workGraphRevision,
                    _workGraph,
                    evidenceProjections);
            }
        }

        internal CompletionDossier BuildCompletionDossier(
            OrchestrationDecision decision,
            IReadOnlyDictionary<string, AcceptedEvidenceProjection> resolvedEvidence)
        {
            ArgumentNullException.ThrowIfNull(decision);
            ArgumentNullException.ThrowIfNull(resolvedEvidence);
            var completion = decision.NextAction as BeginCompletionAction
                ?? throw new InvalidOperationException(
                    "Only BeginCompletion can build a completion dossier.");

            WorkGraphSnapshot? candidateGraph;
            Dictionary<string, AcceptedEvidenceProjection> evidence;
            lock (_sync)
            {
                candidateGraph = _workGraph;
                evidence = _evidence.ToDictionary(
                    item => item.EvidenceId,
                    item => item,
                    StringComparer.Ordinal);
            }

            foreach (var (evidenceId, projection) in resolvedEvidence)
            {
                evidence[evidenceId] = projection;
            }

            if (candidateGraph is not null && decision.WorkUpdate is not null)
            {
                var delta = new WorkGraphDelta(
                    decision.WorkUpdate.BaseRevision,
                    decision.WorkUpdate.Items.Select(item => new WorkNode(
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
                    candidateGraph,
                    delta,
                    evidence.Keys.ToHashSet(StringComparer.Ordinal));
                if (!applied.Accepted)
                {
                    throw new InvalidDataException(
                        "The validated completion no longer applies to the authoritative work graph.");
                }

                candidateGraph = applied.Snapshot;
            }

            var requiredOutcomes = completion.Plan.RequiredOutcomeIds
                .Select(outcomeId => candidateGraph is not null
                    && candidateGraph.Nodes.TryGetValue(outcomeId, out var outcome)
                        ? outcome
                        : throw new InvalidDataException(
                            $"Completion outcome '{outcomeId}' has no exact authoritative projection."))
                .ToArray();
            var claimsById = decision.MaterialClaims.ToDictionary(
                claim => claim.ClaimId,
                claim => claim,
                StringComparer.Ordinal);
            var requiredClaims = completion.Plan.RequiredClaimIds
                .Select(claimId => claimsById.TryGetValue(claimId, out var claim)
                    ? claim
                    : throw new InvalidDataException(
                        $"Completion claim '{claimId}' has no exact accepted projection."))
                .ToArray();
            var citedEvidence = new List<AcceptedEvidenceProjection>();
            var citedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in completion.Plan.Bindings)
            {
                foreach (var evidenceId in binding.EvidenceIds)
                {
                    if (!citedIds.Add(evidenceId))
                    {
                        continue;
                    }

                    if (!evidence.TryGetValue(evidenceId, out var projection))
                    {
                        throw new InvalidDataException(
                            $"Completion evidence '{evidenceId}' has no exact accepted projection.");
                    }

                    citedEvidence.Add(projection);
                }
            }

            if (citedEvidence.Count >
                OrchestrationDecisionValidator.MaximumCompletionEvidenceProjections
                || citedEvidence.Sum(static item => (long)item.Projection.Length) >
                OrchestrationDecisionValidator.MaximumCompletionEvidenceProjectionCharacters)
            {
                throw new InvalidDataException(
                    "A completion dossier exceeded its validated fail-closed evidence budget.");
            }

            return new CompletionDossier(
                requiredOutcomes,
                requiredClaims,
                citedEvidence);
        }

        private void InstallAuthoritativeWorkGraphUnderLock(WorkGraphSnapshot graph)
        {
            if (!ReferenceEquals(_workGraph, graph))
            {
                _workGraph = graph;
                _workIdentityFingerprint = FingerprintWorkGraph(graph);
            }

            _fallbackKnownWorkItemIds.Clear();
        }

        private static void AddResolvableEvidenceIds(
            HashSet<string> target,
            IEnumerable<string> evidenceIds)
        {
            foreach (var evidenceId in evidenceIds)
            {
                if (!string.IsNullOrWhiteSpace(evidenceId)
                    && evidenceId.Length <= 256
                    && string.Equals(evidenceId, evidenceId.Trim(), StringComparison.Ordinal))
                {
                    target.Add(evidenceId);
                }
            }
        }
    }

    private sealed record CompletionDossier(
        IReadOnlyList<WorkNode> RequiredOutcomes,
        IReadOnlyList<OrchestrationMaterialClaim> RequiredClaims,
        IReadOnlyList<AcceptedEvidenceProjection> CitedEvidence);

    private sealed record PlanningPassDispatch(
        IChatClient ChatClient,
        ModelProfile Profile,
        bool SupportsNativeToolCalls,
        TurnRuntimeBindings? Bindings,
        string? BoundReasoningEffort);

    private sealed class ActiveTurnScope(
        AliOrchestrationPlanningClient owner,
        ActivePlanningTurn turn) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.CompareExchange(ref owner._activeTurn, null, turn);
            }
        }
    }
}

internal sealed record AliPreparedFinalPublication(
    string PublicationId,
    string AssistantMessageId,
    string AnswerText,
    string AnswerDigest);

internal sealed record AliPreparedInterimResponse(
    TurnIdentity DurableIdentity,
    string PublicationId,
    string AnswerText,
    string AnswerDigest,
    AliPlanningInterimKind Kind);
