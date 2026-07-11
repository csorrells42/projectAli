using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Ali.Core.Coding;
using Ali.Core.Evidence;
using Ali.Core.Feedback;
using Ali.Core.Memory;
using Ali.Core.Permissions;
using Ali.Core.Runtime;
using Ali.Core.Sources;

namespace Ali.Core.Orchestration;

public sealed record AssistantStreamChunk(
    string ConversationId,
    string UserMessageId,
    string AssistantMessageId,
    string Text,
    EvidenceStatus EvidenceStatus,
    string? FinishReason = null)
{
    public bool ReachedOutputLimit =>
        string.Equals(FinishReason, "length", StringComparison.OrdinalIgnoreCase);
}

public sealed class ConversationOrchestrator(
    ILocalModelRuntime runtime,
    PermissionService permissionService,
    CorrectionQueueService correctionQueue,
    ISourceRetriever? sourceRetriever = null,
    ISourceQueryPlanner? sourceQueryPlanner = null,
    IMemoryStore? memoryStore = null,
    ILocalCodingTool? localCodingTool = null,
    ICodingActionPlanner? codingActionPlanner = null,
    ICodingPatchPlanner? codingPatchPlanner = null)
{
    private const int MaxPromptMemories = 20;
    private const int MaxProgrammingActionPlannerStepsPerTurn = 2;
    private const int MaxProgrammingPatchPlannerStepsPerTurn = 1;
    private static readonly char[] MemoryRelevanceTokenSeparators =
        [' ', ',', '.', '?', '!', ':', ';', '/', '\\', '-', '_', '(', ')', '[', ']', '"', '\''];
    private static readonly HashSet<string> MemoryRelevanceTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "here",
        "local",
        "location",
        "located",
        "memory",
        "near",
        "nearby",
        "remember",
        "remembered",
        "weather",
        "where"
    };
    private static readonly HashSet<string> MemoryRelevanceIntents = new(StringComparer.OrdinalIgnoreCase)
    {
        "weather",
        "local_app"
    };
    private static readonly Regex SourcesCheckedRegex = new(
        @"(?:\r?\n){0,2}\s*Sources checked:\s*.*\z",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    public ILocalModelRuntime Runtime { get; } = runtime;

    public PermissionService Permissions { get; } = permissionService;

    public CorrectionQueueService Corrections { get; } = correctionQueue;

    public ISourceRetriever Sources { get; } = sourceRetriever ?? new NoOpSourceRetriever();

    public ISourceQueryPlanner SourcePlanner { get; } = sourceQueryPlanner ?? new ModelSourceQueryPlanner(runtime);

    public IMemoryStore? Memories { get; } = memoryStore;

    public ILocalCodingTool? LocalCodingTool { get; } = localCodingTool;

    public ICodingActionPlanner? CodingPlanner { get; } = localCodingTool is null
        ? null
        : codingActionPlanner ?? new ModelCodingActionPlanner(runtime);

    public ICodingPatchPlanner? CodingPatchPlanner { get; } = localCodingTool is null
        ? null
        : codingPatchPlanner ?? new ModelCodingPatchPlanner(runtime);

    public async IAsyncEnumerable<AssistantStreamChunk> StreamAnswerAsync(
        string conversationId,
        string userMessageId,
        string assistantMessageId,
        string userText,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);

        if (LocalCodingTool is not null)
        {
            var codingResult = await LocalCodingTool.TryHandleAsync(userText, cancellationToken).ConfigureAwait(false);
            if (codingResult.Handled)
            {
                yield return new AssistantStreamChunk(
                    conversationId,
                    userMessageId,
                    assistantMessageId,
                    codingResult.Message,
                    codingResult.Succeeded ? EvidenceStatus.Verified : EvidenceStatus.Unknown);
                yield break;
            }
        }

        var codingContext = LocalCodingTool is null
            ? CodingContextPack.Empty
            : await LocalCodingTool.BuildContextPackAsync(userText, cancellationToken).ConfigureAwait(false);
        var codingTaskPlan = LocalCodingTool is null
            ? CodingTaskPlan.Empty
            : await LocalCodingTool.BuildTaskPlanAsync(userText, codingContext, cancellationToken).ConfigureAwait(false);
        var plannerHistory = AddSavedMemories(history);
        var sourcePlan = await SourcePlanner.PlanAsync(userText, plannerHistory, cancellationToken).ConfigureAwait(false);
        var sourceResult = sourcePlan.UseSources
            ? await Sources.RetrieveAsync(sourcePlan, cancellationToken).ConfigureAwait(false)
            : SourceRetrievalResult.Empty;

        var answerHistory = ShouldIncludeSavedMemoriesInAnswer(userText, sourcePlan)
            ? plannerHistory
            : history;
        answerHistory = AddCodingContext(answerHistory, codingContext, codingTaskPlan);
        var enrichedHistory = answerHistory;
        if (sourceResult.HasSources)
        {
            enrichedHistory = answerHistory
                .Append(new ChatMessage(
                    $"msg_sources_instruction_{Guid.NewGuid():N}",
                    ChatRole.System,
                    SourcePromptFormatter.BuildPromptInstruction(sourceResult),
                    DateTimeOffset.UtcNow,
                    EvidenceStatus.Verified))
                .Append(new ChatMessage(
                    $"msg_sources_context_{Guid.NewGuid():N}",
                    ChatRole.User,
                    SourcePromptFormatter.BuildUntrustedExcerptContext(sourceResult),
                    DateTimeOffset.UtcNow,
                    EvidenceStatus.Verified))
                .ToList();
        }
        else if (sourcePlan.UseSources)
        {
            enrichedHistory = answerHistory
                .Append(new ChatMessage(
                    $"msg_sources_empty_{Guid.NewGuid():N}",
                    ChatRole.System,
                    SourcePromptFormatter.BuildNoSourceResultContext(sourcePlan, sourceResult),
                    DateTimeOffset.UtcNow,
                    EvidenceStatus.Verified))
                .ToList();
        }

        if (sourcePlan.UseSources && !sourceResult.HasSources && sourceResult.Warnings.Count > 0)
        {
            yield return new AssistantStreamChunk(
                conversationId,
                userMessageId,
                assistantMessageId,
                BuildSourceLookupFailureAnswer(sourceResult),
                EvidenceStatus.Verified);
            yield break;
        }

        var request = new ChatRequest(conversationId, userMessageId, userText, enrichedHistory)
        {
            Attachments = attachments
        };

        if (!sourceResult.HasSources)
        {
            var directAnswer = await CollectRuntimeAnswerAsync(request, cancellationToken).ConfigureAwait(false);
            if (!sourcePlan.UseSources && ShouldRetryWithSourceLookup(directAnswer.Text))
            {
                var retryPlan = new SourceQueryPlan(
                    true,
                    true,
                    "general_sources",
                    userText,
                    [userText],
                    Array.Empty<string>());
                var retrySourceResult = await Sources.RetrieveAsync(retryPlan, cancellationToken).ConfigureAwait(false);
                if (retrySourceResult.HasSources || retrySourceResult.Warnings.Count > 0)
                {
                    if (!retrySourceResult.HasSources && retrySourceResult.Warnings.Count > 0)
                    {
                        yield return new AssistantStreamChunk(
                            conversationId,
                            userMessageId,
                            assistantMessageId,
                            BuildSourceLookupFailureAnswer(retrySourceResult),
                            EvidenceStatus.Verified);
                        yield break;
                    }

                    var retryHistory = answerHistory;
                    if (retrySourceResult.HasSources)
                    {
                        retryHistory = retryHistory
                            .Append(new ChatMessage(
                                $"msg_sources_instruction_retry_{Guid.NewGuid():N}",
                                ChatRole.System,
                                SourcePromptFormatter.BuildPromptInstruction(retrySourceResult),
                                DateTimeOffset.UtcNow,
                                EvidenceStatus.Verified))
                            .Append(new ChatMessage(
                                $"msg_sources_context_retry_{Guid.NewGuid():N}",
                                ChatRole.User,
                                SourcePromptFormatter.BuildUntrustedExcerptContext(retrySourceResult),
                                DateTimeOffset.UtcNow,
                                EvidenceStatus.Verified))
                            .ToList();
                    }
                    else
                    {
                        retryHistory = retryHistory
                            .Append(new ChatMessage(
                                $"msg_sources_empty_retry_{Guid.NewGuid():N}",
                                ChatRole.System,
                                SourcePromptFormatter.BuildNoSourceResultContext(retryPlan, retrySourceResult),
                                DateTimeOffset.UtcNow,
                                EvidenceStatus.Verified))
                            .ToList();
                    }

                    var retryRequest = new ChatRequest(conversationId, userMessageId, userText, retryHistory)
                    {
                        Attachments = attachments
                    };
                    var retryAnswer = await CollectRuntimeAnswerAsync(retryRequest, cancellationToken).ConfigureAwait(false);
                    var cleanedRetryAnswer = StripModelGeneratedSourceAppendix(retryAnswer.Text);
                    if (!string.IsNullOrWhiteSpace(cleanedRetryAnswer))
                    {
                        yield return new AssistantStreamChunk(
                            conversationId,
                            userMessageId,
                            assistantMessageId,
                            cleanedRetryAnswer,
                            retryAnswer.EvidenceStatus,
                            retryAnswer.FinishReason);
                    }

                    var retrySourceAppendix = SourcePromptFormatter.BuildAnswerAppendix(retrySourceResult);
                    if (!string.IsNullOrWhiteSpace(retrySourceAppendix))
                    {
                        yield return new AssistantStreamChunk(
                            conversationId,
                            userMessageId,
                            assistantMessageId,
                            $"{Environment.NewLine}{Environment.NewLine}{retrySourceAppendix}",
                            EvidenceStatus.Verified);
                    }

                    yield break;
                }
            }

            if (!string.IsNullOrWhiteSpace(directAnswer.Text))
            {
                yield return new AssistantStreamChunk(
                    conversationId,
                    userMessageId,
                    assistantMessageId,
                    directAnswer.Text,
                    directAnswer.EvidenceStatus,
                    directAnswer.FinishReason);
            }

            yield break;
        }

        var answer = new StringBuilder();
        var evidenceStatus = EvidenceStatus.Unverified;
        string? finishReason = null;
        await foreach (var token in Runtime.StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
        {
            answer.Append(token.Text);
            evidenceStatus = token.EvidenceStatus;
            if (!string.IsNullOrWhiteSpace(token.FinishReason))
            {
                finishReason = token.FinishReason;
            }
        }

        var cleanedAnswer = StripModelGeneratedSourceAppendix(answer.ToString());
        if (!string.IsNullOrWhiteSpace(cleanedAnswer))
        {
            yield return new AssistantStreamChunk(
                conversationId,
                userMessageId,
                assistantMessageId,
                cleanedAnswer,
                evidenceStatus,
                finishReason);
        }

        var sourceAppendix = SourcePromptFormatter.BuildAnswerAppendix(sourceResult);
        if (!string.IsNullOrWhiteSpace(sourceAppendix))
        {
            yield return new AssistantStreamChunk(
                conversationId,
                userMessageId,
                assistantMessageId,
                $"{Environment.NewLine}{Environment.NewLine}{sourceAppendix}",
                EvidenceStatus.Verified);
        }
    }

    public async IAsyncEnumerable<AssistantStreamChunk> StreamProgrammingAnswerAsync(
        string conversationId,
        string userMessageId,
        string assistantMessageId,
        string userText,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);

        if (LocalCodingTool is null)
        {
            await foreach (var chunk in StreamAnswerAsync(
                               conversationId,
                               userMessageId,
                               assistantMessageId,
                               userText,
                               history,
                               attachments,
                               cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }

            yield break;
        }

        if (CodingToolRequestParser.TryParse(userText, out var directRequest)
            && ShouldRunDirectProgrammingCommand(directRequest, userText, history))
        {
            var directResult = await LocalCodingTool.TryHandleAsync(userText, cancellationToken).ConfigureAwait(false);
            if (directResult.Handled)
            {
                yield return new AssistantStreamChunk(
                    conversationId,
                    userMessageId,
                    assistantMessageId,
                    BuildProgrammingToolMessage(directResult),
                    directResult.Succeeded ? EvidenceStatus.Verified : EvidenceStatus.Unknown);
                yield break;
            }
        }

        var programmingContext = await LocalCodingTool.BuildContextPackAsync(userText, cancellationToken, force: true).ConfigureAwait(false);
        var result = await TryRunModelSelectedProgrammingToolAsync(
            userText,
            history,
            programmingContext,
            null,
            allowPatchPlanner: true,
            MaxProgrammingActionPlannerStepsPerTurn,
            MaxProgrammingPatchPlannerStepsPerTurn,
            cancellationToken).ConfigureAwait(false);

        if (result.Handled)
        {
            yield return new AssistantStreamChunk(
                conversationId,
                userMessageId,
                assistantMessageId,
                BuildProgrammingToolMessage(result.Result, result.SelectedPath),
                result.Succeeded ? EvidenceStatus.Verified : EvidenceStatus.Unknown);
            yield break;
        }

        yield return new AssistantStreamChunk(
            conversationId,
            userMessageId,
            assistantMessageId,
            BuildProgrammingNoRunnableToolMessage(result.Diagnostic),
            EvidenceStatus.Unknown);
    }

    private static string StripModelGeneratedSourceAppendix(string answer) =>
        SourcesCheckedRegex.Replace(answer, string.Empty).TrimEnd();

    private async Task<ProgrammingToolSelectionResult> TryRunModelSelectedProgrammingToolAsync(
        string userText,
        IReadOnlyList<ChatMessage> history,
        CodingContextPack? contextPack,
        CodingPatchPlan? patchPlan,
        bool allowPatchPlanner,
        int actionPlannerStepsRemaining,
        int patchPlannerStepsRemaining,
        CancellationToken cancellationToken)
    {
        if (CodingPlanner is null || LocalCodingTool is null)
        {
            return ProgrammingToolSelectionResult.NotHandled;
        }

        if (actionPlannerStepsRemaining <= 0)
        {
            return ProgrammingToolSelectionResult.NotHandled;
        }

        if (IsPatchPreviewAuthoringCommand(userText))
        {
            var queuedGoal = NormalizeStableProgrammingGoal(userText);
            var repeatedQueuedFailures = CountRecentSimilarProgrammingRepeatGuards(history, queuedGoal, out var latestStopReason);
            if (repeatedQueuedFailures >= 2)
            {
                return new ProgrammingToolSelectionResult(
                    BuildProgrammingRepeatStopResult(queuedGoal, repeatedQueuedFailures, latestStopReason),
                    string.Empty);
            }
        }

        var plannerContext = contextPack
            ?? await LocalCodingTool.BuildContextPackAsync(userText, cancellationToken, force: true).ConfigureAwait(false);
        var plannerHistory = patchPlan is null
            ? history
            : history.Append(BuildPatchPlannerEvidenceMessage(patchPlan)).ToList();
        var plan = await CodingPlanner.PlanAsync(userText, plannerHistory, cancellationToken, plannerContext).ConfigureAwait(false);
        var command = plan.UseCodingTool
            ? plan.Command
            : string.Empty;
        if (string.IsNullOrWhiteSpace(command) || !CodingToolRequestParser.TryParse(command, out var selectedRequest))
        {
            return ProgrammingToolSelectionResult.NotHandledWithDiagnostic(BuildProgrammingPlanDiagnostic(plan, command));
        }

        var recentSimilarRepeatFailures = CountRecentSimilarProgrammingRepeatGuards(history, plan);
        if (recentSimilarRepeatFailures >= 2)
        {
            return new ProgrammingToolSelectionResult(
                BuildProgrammingRepeatStopResult(plan, recentSimilarRepeatFailures),
                plan.SelectedPath);
        }

        var recentRepeatGuard = TryFindRecentProgrammingRepeatGuard(history);
        if (recentRepeatGuard is not null && BlocksProgrammingRepeat(userText, plan, recentRepeatGuard))
        {
            return new ProgrammingToolSelectionResult(
                BuildProgrammingRepeatStopResult(plan, 1, recentRepeatGuard.StopReason),
                plan.SelectedPath);
        }

        if (allowPatchPlanner
            && patchPlannerStepsRemaining > 0
            && IsModelPatchPreviewExecution(plan.ExecutionMode)
            && CodingPatchPlanner is not null)
        {
            var modelPatch = await CodingPatchPlanner.PlanPatchAsync(userText, plannerContext, cancellationToken, plan).ConfigureAwait(false);
            if (modelPatch.HasPatch && modelPatch.Edits.Count > 0)
            {
                var previewResult = await LocalCodingTool.PreviewPatchBundleAsync(userText, modelPatch.Edits, cancellationToken).ConfigureAwait(false);
                if (previewResult.Handled)
                {
                    var retriedAfterPreviewFailure = false;
                    if (!previewResult.Succeeded)
                    {
                        if (IsWpfBehaviorCoverageOnlyFailure(previewResult.Message)
                            && PatchPlanTouchesOnlyXaml(modelPatch))
                        {
                            var companionContext = BuildWpfBehaviorCompanionRetryContext(plannerContext, previewResult.Message);
                            var companionPlan = BuildWpfBehaviorCompanionActionPlan(plan, userText);
                            var companionPatch = await CodingPatchPlanner.PlanPatchAsync(userText, companionContext, cancellationToken, companionPlan).ConfigureAwait(false);
                            if (companionPatch.HasPatch
                                && companionPatch.Edits.Count > 0
                                && PatchPlanTouchesCode(companionPatch))
                            {
                                var combinedEdits = modelPatch.Edits.Concat(companionPatch.Edits).ToArray();
                                var combinedPreviewResult = await LocalCodingTool.PreviewPatchBundleAsync(userText, combinedEdits, cancellationToken).ConfigureAwait(false);
                                if (combinedPreviewResult.Handled)
                                {
                                    if (!combinedPreviewResult.Succeeded)
                                    {
                                        combinedPreviewResult = AppendProgrammingRepeatGuard(combinedPreviewResult, companionPlan, combinedPreviewResult.Message, userText);
                                    }

                                    return new ProgrammingToolSelectionResult(
                                        combinedPreviewResult,
                                        string.IsNullOrWhiteSpace(companionPatch.SelectedPath) ? plan.SelectedPath : companionPatch.SelectedPath);
                                }
                            }

                            previewResult = AppendWpfBehaviorCompanionOutcome(previewResult, companionPatch);
                            previewResult = AppendProgrammingRepeatGuard(previewResult, companionPlan, previewResult.Message, userText);
                            return new ProgrammingToolSelectionResult(
                                previewResult,
                                string.IsNullOrWhiteSpace(companionPatch.SelectedPath) ? plan.SelectedPath : companionPatch.SelectedPath);
                        }

                        if (IsRetryableModelPatchPreviewFailure(previewResult.Message))
                        {
                            retriedAfterPreviewFailure = true;
                            var retryContext = BuildPatchPreviewFailureRetryContext(plannerContext, previewResult.Message);
                            var retryPatch = await CodingPatchPlanner.PlanPatchAsync(userText, retryContext, cancellationToken, plan).ConfigureAwait(false);
                            if (retryPatch.HasPatch && retryPatch.Edits.Count > 0)
                            {
                                var retryPreviewResult = await LocalCodingTool.PreviewPatchBundleAsync(userText, retryPatch.Edits, cancellationToken).ConfigureAwait(false);
                                if (retryPreviewResult.Handled)
                                {
                                    if (!retryPreviewResult.Succeeded)
                                    {
                                        retryPreviewResult = AppendProgrammingRepeatGuard(retryPreviewResult, plan, retryPreviewResult.Message, userText);
                                    }

                                    return new ProgrammingToolSelectionResult(
                                        retryPreviewResult,
                                        string.IsNullOrWhiteSpace(retryPatch.SelectedPath) ? plan.SelectedPath : retryPatch.SelectedPath);
                                }
                            }
                        }

                        if (retriedAfterPreviewFailure)
                        {
                            previewResult = AppendProgrammingRepeatGuard(previewResult, plan, previewResult.Message, userText);
                        }

                        return new ProgrammingToolSelectionResult(
                            previewResult,
                            string.IsNullOrWhiteSpace(modelPatch.SelectedPath) ? plan.SelectedPath : modelPatch.SelectedPath);
                    }

                    return new ProgrammingToolSelectionResult(
                        previewResult,
                        string.IsNullOrWhiteSpace(modelPatch.SelectedPath) ? plan.SelectedPath : modelPatch.SelectedPath);
                }
            }

            return new ProgrammingToolSelectionResult(
                BuildProgrammingRepeatGuardResult(plan, modelPatch.StopReason),
                plan.SelectedPath);
        }

        if (!allowPatchPlanner && patchPlan is not null && IsModelPatchPreviewExecution(plan.ExecutionMode))
        {
            return new ProgrammingToolSelectionResult(
                BuildProgrammingRepeatGuardResult(plan, patchPlan.StopReason),
                plan.SelectedPath);
        }

        var result = await LocalCodingTool.TryHandleAsync(command, cancellationToken).ConfigureAwait(false);
        return new ProgrammingToolSelectionResult(result, plan.SelectedPath);
    }

    private static CodingToolResult AppendProgrammingRepeatGuard(
        CodingToolResult result,
        CodingActionPlan plan,
        string? stopReason,
        string? originalUserText = null)
    {
        var guard = BuildProgrammingRepeatGuardResult(plan, stopReason, originalUserText);
        var message = string.IsNullOrWhiteSpace(result.Message)
            ? guard.Message
            : string.Join(Environment.NewLine, result.Message.TrimEnd(), guard.Message);
        return result with { Message = message };
    }

    private static bool IsRetryableModelPatchPreviewFailure(string message) =>
        message.Contains("expected exactly one match", StringComparison.OrdinalIgnoreCase)
        || message.Contains("create file will not overwrite an existing path", StringComparison.OrdinalIgnoreCase)
        || message.Contains("could not resolve target path inside the approved workspace", StringComparison.OrdinalIgnoreCase)
        || message.Contains("patch preview would leave invalid WPF/XAML structure", StringComparison.OrdinalIgnoreCase)
        || message.Contains("XML parse failed", StringComparison.OrdinalIgnoreCase)
        || message.Contains("C# parse error", StringComparison.OrdinalIgnoreCase);

    private static bool IsWpfBehaviorCoverageOnlyFailure(string message)
    {
        if (!message.Contains("WPF behavior coverage missing", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var blockingStructuralSignals = new[]
        {
            "detached attribute-like text",
            "XML parse failed",
            "duplicate x:Name",
            "duplicate Name",
            "layout overlap",
            "simple Grid layout overlap",
            "C# parse error",
            "expected exactly one match"
        };
        return !blockingStructuralSignals.Any(signal => message.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PatchPlanTouchesOnlyXaml(CodingPatchPlan patchPlan) =>
        patchPlan.Edits.Count > 0
        && patchPlan.Edits.All(edit => edit.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase));

    private static bool PatchPlanTouchesCode(CodingPatchPlan patchPlan) =>
        patchPlan.Edits.Any(edit =>
            edit.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || edit.Path.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase));

    private static CodingToolResult AppendWpfBehaviorCompanionOutcome(
        CodingToolResult result,
        CodingPatchPlan companionPatch)
    {
        string outcome;
        if (!companionPatch.HasPatch || companionPatch.Edits.Count == 0)
        {
            outcome = string.IsNullOrWhiteSpace(companionPatch.StopReason)
                ? "companion planner returned no code patch."
                : $"companion planner returned no code patch: {companionPatch.StopReason}";
        }
        else if (!PatchPlanTouchesCode(companionPatch))
        {
            var targets = string.Join(", ", companionPatch.Edits.Select(edit => edit.Path).Distinct(StringComparer.OrdinalIgnoreCase).Take(4));
            outcome = $"companion planner returned edits without a code target: {targets}";
        }
        else
        {
            outcome = "companion planner returned a code edit, but the combined preview was not available.";
        }

        var message = string.Join(
            Environment.NewLine,
            result.Message.TrimEnd(),
            $"- WPF behavior companion outcome: {BuildCompactRepeatGuardReason(outcome)}");
        return result with { Message = message };
    }

    private static CodingActionPlan BuildWpfBehaviorCompanionActionPlan(CodingActionPlan plan, string userText)
    {
        var goal = LooksLikeProgrammingContinuationRequest(userText)
            ? BuildStableProgrammingGoal(plan)
            : NormalizeStableProgrammingGoal(userText);
        if (string.IsNullOrWhiteSpace(goal))
        {
            goal = BuildStableProgrammingGoal(plan);
        }

        return plan with
        {
            SelectedPath = "Existing feature or bug fix",
            SelectedTool = "concrete patch authoring <goal>",
            CommandGoal = $"{goal}; add only the missing WPF behavior code edit using the mapped event-handler method anchor",
            Summary = "Plan the missing WPF behavior companion edit for the existing XAML preview.",
            AcceptanceCriteria =
            [
                "Return a code-behind, view-model, or command edit for the behavior described by the request.",
                "Do not return another XAML-only patch.",
                "Use an exact current code anchor from the approved context."
            ]
        };
    }

    private static CodingContextPack BuildWpfBehaviorCompanionRetryContext(
        CodingContextPack contextPack,
        string failureMessage)
    {
        var retryEvidence = string.Join(
            Environment.NewLine,
            contextPack.Text,
            "Latest patch preview failure:",
            failureMessage.ReplaceLineEndings(" ").Trim(),
            "Missing WPF behavior companion contract:",
            "The previous preview changed XAML only, but the request also changes user-triggered behavior.",
            "Return one or more .xaml.cs/view-model/command edits that implement the behavior using exact current code anchors.",
            "Do not return another XAML-only patch in this companion step.",
            "Use the WPF event handler map to choose the existing handler method for the visible control.",
            "If no exact code anchor is available, return has_patch=false with the missing code anchor called out.");
        return contextPack with { Text = retryEvidence };
    }

    private static CodingContextPack BuildPatchPreviewFailureRetryContext(CodingContextPack contextPack, string failureMessage)
    {
        var retryEvidence = string.Join(
            Environment.NewLine,
            contextPack.Text,
            "Latest patch preview failure:",
            failureMessage.ReplaceLineEndings(" ").Trim(),
            "Retry patch contract:",
            "If failure says the target path could not be resolved inside the approved workspace, the previous patch used an invalid path. Retry with an actual FILE relative path from the editable excerpts or return has_patch=false.",
            "Never use placeholder, tutorial, demo, temp, or invented paths such as C:\\Workspace\\Demo\\File.cs.",
            "If exact oldText cannot be made unique from the evidence, use mode=\"replace_file\" with complete final file text, or return has_patch=false.",
            "For WPF/XAML structural failures, use mode=\"replace_file\" for each affected .xaml and .xaml.cs file with complete valid final XML/C# text. Do not retry with snippets, loose closing tags, or methods outside the matching partial class.",
            "If failure says an existing XAML event handler was removed, preserve that exact handler name in the retry unless the user explicitly asked to rename it.",
            "When preserving existing button behavior, keep existing Click attributes such as OnSendClick/OnClearClick/OnExitClick and edit those method bodies instead of inventing new handler names.",
            "If failure says missing XAML event handler, either remove the unnecessary XAML event attribute or include the matching void Handler(object sender, RoutedEventArgs e) method inside the .xaml.cs partial class in the same retry patch.",
            "If missing handlers are Checked/Unchecked handlers for a CheckBox whose state only changes an existing action, remove those XAML event attributes and read the named CheckBox IsChecked value inside the existing action handler.",
            "For CheckBox state used by a Send/Save/Add action, prefer x:Name plus IsChecked inside the existing action handler instead of Checked/Unchecked events.",
            "If failure context is an unknown XAML binding such as MainWindow.xaml -> IsKeepText and code-behind already reads keepCheckBox.IsChecked, remove only the unnecessary IsChecked binding attribute. Do not rewrite the whole Window.",
            "If the failure context is CS0246 for WPF control types in .xaml.cs, retry by adding the missing namespace using to the .xaml.cs file only. Do not rewrite XAML, remove named elements, rename handlers, or replace working handler bodies for that diagnostic.",
            "If the rejected .xaml.cs preview shows only using directives or says no matching class for XAML x:Class, the patch removed the namespace/class. Retry by preserving the namespace line: oldText is the exact namespace line, newText is the using directives followed by that same namespace line, or use replace_file with the complete valid source.",
            "If a retry removes WPF code-behind methods/properties, remove complete declarations only. For multiple member removals, exact-match failures, or any brace-balance uncertainty, MUST use mode=\"replace_file\" with the complete valid .xaml.cs file.",
            "For C# handler-only behavior changes, retry by replacing the exact whole existing method text inside the current class, preserving namespace, base class, constructor, usings, and other handlers.",
            "If retrying after C# parse errors such as 'private is not valid for this item', the previous replacement was a method snippet outside the class. Retry with either an exact whole-method replacement or mode=\"replace_file\" complete valid .xaml.cs source.");
        return contextPack with { Text = retryEvidence };
    }

    private static bool IsModelPatchPreviewExecution(string executionMode) =>
        string.Equals(executionMode, "model_patch_preview", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldRunDirectProgrammingCommand(
        CodingToolRequest request,
        string userText,
        IReadOnlyList<ChatMessage> history) =>
        request.UserConfirmed
        || request.Action is CodingToolAction.ShowLastPatchPreview
            or CodingToolAction.DiscardLastPatchPreview
            or CodingToolAction.ShowValidationLedger
            or CodingToolAction.ShowValidationQueueRunner
            or CodingToolAction.ShowPostPatchValidationRouter
            or CodingToolAction.PlanPostEditValidation
            or CodingToolAction.DiagnoseLastFailure
            or CodingToolAction.SuggestLastFailurePatch
            or CodingToolAction.ShowBuildErrorTriage
            or CodingToolAction.VerifyXamlBindings
            or CodingToolAction.VerifyCommandBindings
            or CodingToolAction.ShowCommandSurfaceDoctor
            or CodingToolAction.ScanDeadCommands
        || (IsQueuedProgrammingCommand(userText, history) && !IsPatchPreviewAuthoringCommand(userText));

    private static bool IsPatchPreviewAuthoringCommand(string userText)
    {
        var normalized = NormalizeProgrammingCommandText(userText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        foreach (var template in CodingAbilityCatalog.PatchPreviewToolTemplates)
        {
            var command = NormalizeProgrammingCommandText(template);
            var marker = command.IndexOf(" <", StringComparison.Ordinal);
            if (marker > 0)
            {
                var prefix = command[..marker];
                if (normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                    || normalized.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (normalized.Equals(command, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsQueuedProgrammingCommand(string userText, IReadOnlyList<ChatMessage> history)
    {
        var normalizedUserText = NormalizeProgrammingCommandText(userText);
        if (string.IsNullOrWhiteSpace(normalizedUserText))
        {
            return false;
        }

        foreach (var message in history.Where(message => message.Role is ChatRole.Assistant).Reverse())
        {
            foreach (var line in message.Text.Split('\n').Select(line => line.Trim()))
            {
                var queued = ExtractProgrammingLineValue(line, "Next command:")
                             ?? ExtractProgrammingLineValue(line, "Next:");
                if (string.IsNullOrWhiteSpace(queued)
                    || !CodingToolRequestParser.TryParse(queued, out _))
                {
                    continue;
                }

                if (NormalizeProgrammingCommandText(queued).Equals(normalizedUserText, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizeProgrammingCommandText(string value) =>
        string.Join(' ', value.ReplaceLineEndings(" ").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static CodingToolResult BuildProgrammingRepeatGuardResult(
        CodingActionPlan plan,
        string? stopReason,
        string? originalUserText = null)
    {
        var reason = BuildCompactRepeatGuardReason(stopReason);
        var nextCommand = BuildProgrammingRepeatGuardNextCommand(plan, reason, originalUserText);
        var message = string.Join(
            Environment.NewLine,
            string.Join(
                " ",
                "Repeat guard:",
                $"execution_mode={ValueOrUnknown(plan.ExecutionMode)};",
                $"selected_path={ValueOrUnknown(plan.SelectedPath)};",
                $"selected_tool={ValueOrUnknown(plan.SelectedTool)};",
                $"command_goal={ValueOrUnknown(plan.CommandGoal)};",
                $"stop_reason={reason}.",
                "Choose an Info/context/validation step before trying that preview again."),
            $"Next command: {nextCommand}");

        return new CodingToolResult(
            true,
            false,
            message,
            "Programming repeat guard");
    }

    private static CodingToolResult BuildProgrammingRepeatStopResult(
        CodingActionPlan plan,
        int repeatedFailures,
        string? stopReason = null)
    {
        var goal = BuildStableProgrammingGoal(plan);
        return BuildProgrammingRepeatStopResult(goal, repeatedFailures, stopReason);
    }

    private static CodingToolResult BuildProgrammingRepeatStopResult(
        string goal,
        int repeatedFailures,
        string? stopReason = null)
    {
        goal = string.IsNullOrWhiteSpace(goal)
            ? "current programming goal"
            : goal.ReplaceLineEndings(" ").Trim();
        var reason = BuildCompactRepeatGuardReason(stopReason);
        var message = string.Join(
            Environment.NewLine,
            "Repeated patch preview failure guard:",
            "No files were changed.",
            $"Goal: {goal}",
            $"Repeated guarded failures: {repeatedFailures}",
            $"Last stop reason: {reason}",
            "No safe Next command is queued for this same patch path. Use a different info/tool path or change the request before trying another patch preview.");

        return new CodingToolResult(
            true,
            false,
            message,
            "Programming repeat guard");
    }

    private static string BuildProgrammingRepeatGuardNextCommand(
        CodingActionPlan plan,
        string reason,
        string? originalUserText = null)
    {
        var goal = BuildStableProgrammingGoal(plan, originalUserText);

        if (plan.ExecutionMode.Equals("model_patch_preview", StringComparison.OrdinalIgnoreCase))
        {
            return $"coding context packet {TrimGoalForCommand(goal)}";
        }

        return $"coding context packet {TrimGoalForCommand(goal)}";
    }

    private static string BuildStableProgrammingGoal(CodingActionPlan plan, string? originalUserText = null)
    {
        foreach (var candidate in new[] { originalUserText ?? string.Empty, plan.CommandGoal, plan.UnderstoodGoal, plan.Summary })
        {
            if (LooksLikeProgrammingContinuationRequest(candidate))
            {
                continue;
            }

            var goal = NormalizeStableProgrammingGoal(candidate);
            if (!string.IsNullOrWhiteSpace(goal))
            {
                return goal.Length > 180 ? goal[..180].TrimEnd() : goal;
            }
        }

        return "current programming goal";
    }

    private static string NormalizeStableProgrammingGoal(string value)
    {
        var goal = value.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrWhiteSpace(goal))
        {
            return string.Empty;
        }

        foreach (var template in CodingAbilityCatalog.PatchPreviewToolTemplates)
        {
            var marker = template.IndexOf(" <", StringComparison.Ordinal);
            if (marker <= 0)
            {
                continue;
            }

            var prefix = template[..marker];
            if (goal.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
            {
                goal = goal[prefix.Length..].Trim();
                break;
            }
        }

        var repairMarker = goal.IndexOf("; repair the latest patch preview failure", StringComparison.OrdinalIgnoreCase);
        if (repairMarker >= 0)
        {
            goal = goal[..repairMarker].Trim();
        }

        repairMarker = goal.IndexOf(" repair the latest patch preview failure", StringComparison.OrdinalIgnoreCase);
        if (repairMarker > 0)
        {
            goal = goal[..repairMarker].Trim();
        }

        return goal;
    }

    private static string BuildCompactRepeatGuardReason(string? stopReason)
    {
        if (string.IsNullOrWhiteSpace(stopReason))
        {
            return "the patch planner did not provide a safe patch or stop reason";
        }

        var reason = stopReason.ReplaceLineEndings(" ").Trim();
        foreach (var marker in new[] { "Rejected preview snippets:", "Rejected preview targets:", "Repair route:" })
        {
            var index = reason.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                reason = reason[..index].Trim();
            }
        }

        return reason.Length > 320
            ? reason[..320].TrimEnd() + "..."
            : reason;
    }

    private static string TrimGoalForCommand(string goal)
    {
        goal = goal.ReplaceLineEndings(" ").Trim();
        return goal.Length > 220
            ? goal[..220]
            : goal;
    }

    private static string ValueOrUnknown(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.ReplaceLineEndings(" ").Trim();

    private sealed record ProgrammingRepeatGuard(
        string ExecutionMode,
        string SelectedPath,
        string SelectedTool,
        string CommandGoal,
        string StopReason);

    private static ProgrammingRepeatGuard? TryFindRecentProgrammingRepeatGuard(IReadOnlyList<ChatMessage> history)
    {
        foreach (var message in history
                     .Where(message => message.Role is ChatRole.Assistant)
                     .Reverse())
        {
            foreach (var line in message.Text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0))
            {
                if (IsFreshValidationEvidenceLine(line))
                {
                    return null;
                }

                var markerIndex = line.IndexOf("Repeat guard:", StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    continue;
                }

                var guard = ParseProgrammingRepeatGuard(line[(markerIndex + "Repeat guard:".Length)..]);
                if (guard is not null)
                {
                    return guard;
                }
            }
        }

        return null;
    }

    private static int CountRecentSimilarProgrammingRepeatGuards(
        IReadOnlyList<ChatMessage> history,
        CodingActionPlan plan)
    {
        if (!IsModelPatchPreviewExecution(plan.ExecutionMode))
        {
            return 0;
        }

        var count = 0;
        foreach (var message in history
                     .Where(message => message.Role is ChatRole.Assistant)
                     .Reverse())
        {
            foreach (var line in message.Text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0))
            {
                if (IsFreshValidationEvidenceLine(line))
                {
                    return count;
                }

                var markerIndex = line.IndexOf("Repeat guard:", StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    continue;
                }

                var guard = ParseProgrammingRepeatGuard(line[(markerIndex + "Repeat guard:".Length)..]);
                if (guard is null)
                {
                    continue;
                }

                if (IsSimilarProgrammingRepeatGuard(plan, guard))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int CountRecentSimilarProgrammingRepeatGuards(
        IReadOnlyList<ChatMessage> history,
        string goal,
        out string latestStopReason)
    {
        latestStopReason = string.Empty;
        goal = NormalizeStableProgrammingGoal(goal);
        if (string.IsNullOrWhiteSpace(goal))
        {
            return 0;
        }

        var count = 0;
        foreach (var message in history
                     .Where(message => message.Role is ChatRole.Assistant)
                     .Reverse())
        {
            foreach (var line in message.Text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0))
            {
                if (IsFreshValidationEvidenceLine(line))
                {
                    return count;
                }

                var markerIndex = line.IndexOf("Repeat guard:", StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    continue;
                }

                var guard = ParseProgrammingRepeatGuard(line[(markerIndex + "Repeat guard:".Length)..]);
                if (guard is null
                    || !guard.ExecutionMode.Equals("model_patch_preview", StringComparison.OrdinalIgnoreCase)
                    || !GoalsLikelySame(guard.CommandGoal, goal))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(latestStopReason))
                {
                    latestStopReason = guard.StopReason;
                }

                count++;
            }
        }

        return count;
    }

    private static bool IsSimilarProgrammingRepeatGuard(CodingActionPlan plan, ProgrammingRepeatGuard guard) =>
        string.Equals(guard.ExecutionMode, plan.ExecutionMode, StringComparison.OrdinalIgnoreCase)
        && string.Equals(guard.SelectedPath, plan.SelectedPath, StringComparison.OrdinalIgnoreCase)
        && GoalsLikelySame(guard.CommandGoal, plan.CommandGoal);

    private static bool IsFreshValidationEvidenceLine(string line) =>
        line.Contains("Build failed", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Test failed", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Restore failed", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Diagnostic summary", StringComparison.OrdinalIgnoreCase)
        || line.Contains("First useful errors", StringComparison.OrdinalIgnoreCase);

    private static ProgrammingRepeatGuard? ParseProgrammingRepeatGuard(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equalsIndex = part.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            var key = part[..equalsIndex].Trim();
            var value = part[(equalsIndex + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                fields[key] = value;
            }
        }

        fields.TryGetValue("execution_mode", out var executionMode);
        fields.TryGetValue("selected_path", out var selectedPath);
        fields.TryGetValue("selected_tool", out var selectedTool);
        fields.TryGetValue("command_goal", out var commandGoal);
        fields.TryGetValue("stop_reason", out var stopReason);
        if (string.IsNullOrWhiteSpace(executionMode)
            || string.IsNullOrWhiteSpace(selectedPath)
            || string.IsNullOrWhiteSpace(selectedTool))
        {
            return null;
        }

        return new ProgrammingRepeatGuard(
            executionMode,
            selectedPath,
            selectedTool,
            commandGoal ?? string.Empty,
            stopReason ?? string.Empty);
    }

    private static bool BlocksProgrammingRepeat(string userText, CodingActionPlan plan, ProgrammingRepeatGuard guard)
    {
        if (!IsModelPatchPreviewExecution(plan.ExecutionMode)
            || !string.Equals(guard.ExecutionMode, plan.ExecutionMode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(guard.SelectedPath, plan.SelectedPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(guard.SelectedTool, plan.SelectedTool, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return LooksLikeProgrammingContinuationRequest(userText)
               || GoalsLikelySame(guard.CommandGoal, plan.CommandGoal);
    }

    private static bool LooksLikeProgrammingContinuationRequest(string text)
    {
        var tokens = TokenizeForRepeatGuard(text).ToArray();
        return tokens.Length is > 0 and <= 5
               && tokens.Any(token => token is "continue" or "next" or "go" or "again" or "yes" or "yep" or "run" or "do" or "proceed" or "execute");
    }

    private static bool GoalsLikelySame(string first, string second)
    {
        var firstNormalized = string.Join(' ', TokenizeForRepeatGuard(first));
        var secondNormalized = string.Join(' ', TokenizeForRepeatGuard(second));
        if (string.IsNullOrWhiteSpace(firstNormalized) || string.IsNullOrWhiteSpace(secondNormalized))
        {
            return false;
        }

        if (firstNormalized.Contains(secondNormalized, StringComparison.OrdinalIgnoreCase)
            || secondNormalized.Contains(firstNormalized, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var firstTokens = TokenizeForRepeatGuard(first).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var secondTokens = TokenizeForRepeatGuard(second).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (firstTokens.Count == 0 || secondTokens.Count == 0)
        {
            return false;
        }

        var overlap = firstTokens.Count(secondTokens.Contains);
        return overlap >= Math.Ceiling(Math.Min(firstTokens.Count, secondTokens.Count) * 0.6);
    }

    private static IEnumerable<string> TokenizeForRepeatGuard(string text)
    {
        var token = new StringBuilder();
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (token.Length > 0)
            {
                yield return token.ToString();
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            yield return token.ToString();
        }
    }

    private static ChatMessage BuildPatchPlannerEvidenceMessage(CodingPatchPlan patchPlan)
    {
        var lines = new List<string>
        {
            "Patch planner evidence for Info step:",
            string.IsNullOrWhiteSpace(patchPlan.SelectedPath)
                ? "Selected path: not provided."
                : $"Selected path: {patchPlan.SelectedPath}",
            string.IsNullOrWhiteSpace(patchPlan.Summary)
                ? "Summary: not provided."
                : $"Summary: {patchPlan.Summary}",
            string.IsNullOrWhiteSpace(patchPlan.StopReason)
                ? "Stop reason: not provided."
                : $"Stop reason: {patchPlan.StopReason}",
            $"Confidence: {patchPlan.Confidence:0.###}",
            $"Patch edits: {patchPlan.Edits.Count}"
        };

        return new ChatMessage(
            $"msg_patch_planner_evidence_{Guid.NewGuid():N}",
            ChatRole.Assistant,
            string.Join(Environment.NewLine, lines),
            DateTimeOffset.UtcNow,
            EvidenceStatus.Verified);
    }

    private static string BuildProgrammingToolMessage(CodingToolResult result, string selectedPath = "")
    {
        var nextCommand = ExtractProgrammingLineValue(result.Message, "Next command:");
        var status = BuildProgrammingStatus(result, selectedPath);
        if (!string.IsNullOrWhiteSpace(nextCommand))
        {
            var lines = new List<string>
            {
                $"Next: {nextCommand}",
                $"Status: {status}",
            };
            AddProgrammingDetails(lines, result);
            lines.Add("Use Next to run that step.");
            return string.Join(Environment.NewLine, lines);
        }

        var next = result.Succeeded
            ? "no queued step yet."
            : "review the status before continuing.";
        var messageLines = new List<string>
        {
            $"Next: {next}",
            $"Status: {status}"
        };
        AddProgrammingDetails(messageLines, result);
        return string.Join(Environment.NewLine, messageLines);
    }

    private static void AddProgrammingDetails(List<string> lines, CodingToolResult result)
    {
        if (result.Succeeded && !IsDiagnosticProgrammingResult(result))
        {
            return;
        }

        var detailCandidates = result.Message
            .Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !line.StartsWith("Next command:", StringComparison.OrdinalIgnoreCase))
            .Skip(1)
            .ToArray();
        var detailLines = result.Succeeded
            ? detailCandidates.Take(8).ToArray()
            : detailCandidates
                .Where(IsHighSignalProgrammingDetail)
                .Concat(detailCandidates)
                .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        if (detailLines.Length == 0)
        {
            return;
        }

        lines.Add("Details:");
        lines.AddRange(detailLines.Select(line => $"- {TrimProgrammingDiagnostic(line)}"));
    }

    private static bool IsDiagnosticProgrammingResult(CodingToolResult result) =>
        result.ToolName?.Contains("diagnos", StringComparison.OrdinalIgnoreCase) == true
        || result.ToolName?.Contains("triage", StringComparison.OrdinalIgnoreCase) == true
        || result.ToolName?.Contains("context packet", StringComparison.OrdinalIgnoreCase) == true
        || result.ToolName?.Contains("validation", StringComparison.OrdinalIgnoreCase) == true
        || result.ToolName?.Contains("binding check", StringComparison.OrdinalIgnoreCase) == true
        || result.Message.StartsWith("Coding context packet:", StringComparison.OrdinalIgnoreCase)
        || result.Message.Contains("diagnosis", StringComparison.OrdinalIgnoreCase)
        || result.Message.Contains("diagnostic", StringComparison.OrdinalIgnoreCase);

    private static bool IsHighSignalProgrammingDetail(string line) =>
        line.Contains("error", StringComparison.OrdinalIgnoreCase)
        || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
        || line.Contains("missing", StringComparison.OrdinalIgnoreCase)
        || line.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || line.Contains("has no", StringComparison.OrdinalIgnoreCase)
        || line.Contains("detached", StringComparison.OrdinalIgnoreCase)
        || line.Contains("overlap", StringComparison.OrdinalIgnoreCase)
        || line.Contains("structural", StringComparison.OrdinalIgnoreCase)
        || line.Contains("blocked", StringComparison.OrdinalIgnoreCase)
        || line.Contains("behavior companion", StringComparison.OrdinalIgnoreCase)
        || line.Contains("returned no code", StringComparison.OrdinalIgnoreCase)
        || line.Contains("needs", StringComparison.OrdinalIgnoreCase);

    private static string BuildProgrammingNoRunnableToolMessage(string diagnostic)
    {
        var lines = new List<string>
        {
            "Next: no queued step yet.",
            "Status: The programming planner did not select a runnable tool from the current context."
        };
        if (!string.IsNullOrWhiteSpace(diagnostic))
        {
            lines.Add($"Planner diagnostic: {TrimProgrammingDiagnostic(diagnostic)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildProgrammingPlanDiagnostic(CodingActionPlan plan, string command)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(plan.Diagnostic))
        {
            lines.Add(plan.Diagnostic);
        }

        if (plan.UseCodingTool && string.IsNullOrWhiteSpace(command))
        {
            lines.Add("Planner returned use_coding_tool true but Ali did not derive a command.");
        }
        else if (!string.IsNullOrWhiteSpace(command) && !CodingToolRequestParser.TryParse(command, out _))
        {
            lines.Add($"Derived command was not accepted by the coding parser: {command}");
        }

        if (!string.IsNullOrWhiteSpace(plan.RawOutputExcerpt))
        {
            lines.Add($"Raw planner output excerpt: {plan.RawOutputExcerpt}");
        }

        return string.Join(" ", lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string TrimProgrammingDiagnostic(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 1_200 ? normalized : normalized[..1_200];
    }

    private static string BuildProgrammingStatus(CodingToolResult result, string selectedPath = "")
    {
        var pathPrefix = string.IsNullOrWhiteSpace(selectedPath)
            ? string.Empty
            : $"Path: {selectedPath}. ";
        if (result.Message.Contains("Coding tool needs confirmation:", StringComparison.OrdinalIgnoreCase))
        {
            return $"{pathPrefix}Waiting for confirmation.";
        }

        if (!result.Succeeded)
        {
            return pathPrefix + (FirstProgrammingMessageLine(result.Message) ?? "Needs attention.");
        }

        if (result.Message.Contains("No files were changed.", StringComparison.OrdinalIgnoreCase))
        {
            return $"{pathPrefix}No files changed yet.";
        }

        return pathPrefix + (FirstProgrammingMessageLine(result.Message) ?? "Ready.");
    }

    private sealed record ProgrammingToolSelectionResult(
        CodingToolResult Result,
        string SelectedPath,
        string Diagnostic = "")
    {
        public static ProgrammingToolSelectionResult NotHandled { get; } = new(CodingToolResult.NotHandled, string.Empty);

        public static ProgrammingToolSelectionResult NotHandledWithDiagnostic(string diagnostic) =>
            new(CodingToolResult.NotHandled, string.Empty, diagnostic);

        public bool Handled => Result.Handled;

        public bool Succeeded => Result.Succeeded;
    }

    private static string? ExtractProgrammingLineValue(string text, string prefix)
    {
        return text
            .Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(line => line[prefix.Length..].Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? FirstProgrammingMessageLine(string text)
    {
        return text
            .Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
    }

    private async Task<CollectedRuntimeAnswer> CollectRuntimeAnswerAsync(
        ChatRequest request,
        CancellationToken cancellationToken)
    {
        var answer = new StringBuilder();
        var evidenceStatus = EvidenceStatus.Unverified;
        string? finishReason = null;
        await foreach (var token in Runtime.StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
        {
            answer.Append(token.Text);
            evidenceStatus = token.EvidenceStatus;
            if (!string.IsNullOrWhiteSpace(token.FinishReason))
            {
                finishReason = token.FinishReason;
            }
        }

        return new CollectedRuntimeAnswer(answer.ToString(), evidenceStatus, finishReason);
    }

    private static bool ShouldRetryWithSourceLookup(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        var normalized = answer.ReplaceLineEndings(" ");
        return normalized.Contains("don't have real-time access", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("do not have real-time access", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("no real-time access", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("don't have access to current", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("do not have access to current", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("don't have internet access", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("do not have internet access", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("cannot browse", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("can't browse", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("check reliable news sources", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("search engines", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("latest information yourself", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSourceLookupFailureAnswer(SourceRetrievalResult result)
    {
        var lines = new List<string>
        {
            "I tried the source lookup for this current/source-backed question, but the internet backend did not return usable source excerpts."
        };

        if (result.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Source backend warnings:");
            foreach (var warning in result.Warnings.Take(5))
            {
                lines.Add($"- {warning}");
            }
        }

        lines.Add(string.Empty);
        lines.Add("Configure the internet backend settings or API keys, then ask again.");
        return string.Join(Environment.NewLine, lines);
    }

    private sealed record CollectedRuntimeAnswer(
        string Text,
        EvidenceStatus EvidenceStatus,
        string? FinishReason);

    private static IReadOnlyList<ChatMessage> AddCodingContext(
        IReadOnlyList<ChatMessage> history,
        CodingContextPack contextPack,
        CodingTaskPlan taskPlan)
    {
        if ((!contextPack.HasContext || string.IsNullOrWhiteSpace(contextPack.Text))
            && (!taskPlan.HasPlan || string.IsNullOrWhiteSpace(taskPlan.Text)))
        {
            return history;
        }

        var instruction = contextPack.IncludesLastFailure
            ? "A local coding context pack is attached. Use it to explain the likely build/test failure and propose a small fix. Do not say you changed files. File edits still require explicit user confirmation."
            : "A local coding context pack is attached. Use it as read-only context for this coding question. Do not say you changed files or ran tools unless a tool result says so.";
        if (taskPlan.HasPlan)
        {
            instruction += " A guarded coding task plan is also attached. Use it as the work order and preserve its confirmation gates.";
        }

        var updated = history
            .Append(new ChatMessage(
                $"msg_coding_instruction_{Guid.NewGuid():N}",
                ChatRole.System,
                instruction,
                DateTimeOffset.UtcNow,
                EvidenceStatus.Verified))
            .ToList();

        if (contextPack.HasContext && !string.IsNullOrWhiteSpace(contextPack.Text))
        {
            updated.Add(new ChatMessage(
                $"msg_coding_context_{Guid.NewGuid():N}",
                ChatRole.User,
                contextPack.Text,
                DateTimeOffset.UtcNow,
                EvidenceStatus.Verified));
        }

        if (taskPlan.HasPlan && !string.IsNullOrWhiteSpace(taskPlan.Text))
        {
            updated.Add(new ChatMessage(
                $"msg_coding_plan_{Guid.NewGuid():N}",
                ChatRole.User,
                taskPlan.Text,
                DateTimeOffset.UtcNow,
                EvidenceStatus.Verified));
        }

        return updated;
    }

    private IReadOnlyList<ChatMessage> AddSavedMemories(IReadOnlyList<ChatMessage> history)
    {
        if (Memories is null)
        {
            return history;
        }

        var result = Memories.List();
        var memories = result.Memories
            .Where(memory => memory.Active && memory.Sensitivity == MemorySensitivity.Normal)
            .Take(MaxPromptMemories)
            .ToList();
        if (memories.Count == 0)
        {
            return history;
        }

        var lines = new List<string>
        {
            "Saved local user memories. These are facts about the human user and their context, not facts about the assistant identity.",
            "Use them only when they directly help answer the current user message.",
            "Never use saved memories, user names, friend names, or customer profile details to rename the assistant or answer as if they are the assistant's name.",
            "Use them for location-dependent requests, explicit memory questions, preferences, and continuity.",
            "Do not mention or paraphrase saved memories unless the user asked about them or they are essential to the answer.",
            "If the current user contradicts a saved memory, follow the current user."
        };

        foreach (var memory in memories)
        {
            lines.Add($"- {memory.Text}");
        }

        return history
            .Append(new ChatMessage(
                $"msg_memories_{Guid.NewGuid():N}",
                ChatRole.System,
                string.Join(Environment.NewLine, lines),
                DateTimeOffset.UtcNow,
                EvidenceStatus.Verified))
            .ToList();
    }

    private static bool ShouldIncludeSavedMemoriesInAnswer(string userText, SourceQueryPlan sourcePlan)
    {
        if (MemoryRelevanceIntents.Contains(sourcePlan.Intent))
        {
            return true;
        }

        var tokens = userText
            .Split(MemoryRelevanceTokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return tokens.Overlaps(MemoryRelevanceTerms)
            || tokens.Contains("my");
    }
}
