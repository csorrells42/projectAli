using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Ali.Modules.AgentWorkMemory;
using Ali.Modules.Capabilities;
using Ali.Modules.Coding;
using Ali.Modules.Conversation;
using Ali.Modules.Evidence;
using Ali.Modules.WorkstationFiles;
using Ali.Modules.Identity;
using Ali.Modules.Internet;
using Ali.Modules.Memory;
using Ali.Modules.Mcp;
using Ali.Modules.Orchestration;
using Ali.Modules.Permissions;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Observation;
using Ali.Modules.Reminders;
using Ali.Modules.Runtime;
using Ali.Modules.UserMemory;
using Ali.Modules.Serena;
using Ali.Modules.ToolDiscovery;
using Microsoft.Extensions.AI;
using RuntimeChatMessage = Ali.Modules.Runtime.ChatMessage;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Thin boundary between Ali's conversation stream, immutable capability modules, and the
/// Agent Framework runner. English interpretation belongs to the model and harness.
/// </summary>
public sealed class AliToolCoordinator : IDisposable
{
    private readonly AliAgentHarnessRunner _harness;
    private readonly CoordinatorTurnLease _turn = new();
    private readonly AliFrameworkToolOutcomeSidecar _toolOutcomes;
    private readonly IActiveUserSession? _activeUsers;
    private readonly IParticipantRosterAuthority? _participantRosterAuthority;
    private readonly string _assistantName;
    private int _disposed;

    // Retained for the desktop orchestration surface; CP11 removed the legacy
    // background memory-review producer, so no participant-memory path raises it.
#pragma warning disable CS0067
    public event Action<AssistantStreamChunk>? BackgroundActivity;
#pragma warning restore CS0067

    public AliToolCoordinator(
        ILocalModelRuntime runtime,
        IChatClient chatClient,
        ISourceRetriever localLibrary,
        ISourceRetriever webSources,
        McpWebResearchClient webResearch,
        IMemoryStore memories,
        IReminderStore reminders,
        AssistantProfile assistantProfile,
        McpClientManager mcpClients,
        AgentToolPermissionStore toolPermissions,
        AliWorkstationFileAccess fileAccess,
        AliAgentWorkMemory workMemory,
        string capabilitySettingsDataRoot,
        AliCodingModule? codingModule = null,
        IUserMemoryService? userMemories = null,
        IActiveUserSession? activeUsers = null,
        Func<UserMemorySettings>? memorySettings = null,
        string? workflowCheckpointPath = null,
        Func<AgentOrchestrationSettings>? orchestrationSettings = null,
        ISemanticToolCatalog? semanticToolCatalog = null,
        IConversationPublicationProbe? conversationPublicationProbe = null,
        IParticipantMemoryService? participantMemories = null,
        IParticipantRosterAuthority? participantRosterAuthority = null,
        ParticipantMemoryReceiptAuthority? participantReceiptAuthority = null,
        Func<WebSourceBackendSettings>? internetSettings = null,
        SerenaCodingService? serenaCoding = null)
        : this(
            runtime,
            chatClient,
            localLibrary,
            webSources,
            webResearch,
            memories,
            reminders,
            assistantProfile,
            mcpClients,
            toolPermissions,
            fileAccess,
            workMemory,
            codingModule,
            userMemories,
            activeUsers,
            memorySettings,
            workflowCheckpointPath,
            orchestrationSettings,
            semanticToolCatalog,
            shadowObserver: null,
            capabilitySettingsDataRoot: RequireCapabilitySettingsDataRoot(
                capabilitySettingsDataRoot),
            conversationPublicationProbe,
            participantMemories,
            participantRosterAuthority,
            participantReceiptAuthority,
            internetSettings,
            serenaCoding)
    {
    }

    internal AliToolCoordinator(
        ILocalModelRuntime runtime,
        IChatClient chatClient,
        ISourceRetriever localLibrary,
        ISourceRetriever webSources,
        McpWebResearchClient webResearch,
        IMemoryStore memories,
        IReminderStore reminders,
        AssistantProfile assistantProfile,
        McpClientManager mcpClients,
        AgentToolPermissionStore toolPermissions,
        AliWorkstationFileAccess fileAccess,
        AliAgentWorkMemory workMemory,
        AliCodingModule? codingModule,
        IUserMemoryService? userMemories,
        IActiveUserSession? activeUsers,
        Func<UserMemorySettings>? memorySettings,
        string? workflowCheckpointPath,
        Func<AgentOrchestrationSettings>? orchestrationSettings,
        ISemanticToolCatalog? semanticToolCatalog,
        IShadowToolObserver? shadowObserver,
        string? capabilitySettingsDataRoot,
        IConversationPublicationProbe? conversationPublicationProbe = null,
        IParticipantMemoryService? participantMemories = null,
        IParticipantRosterAuthority? participantRosterAuthority = null,
        ParticipantMemoryReceiptAuthority? participantReceiptAuthority = null,
        Func<WebSourceBackendSettings>? internetSettings = null,
        SerenaCodingService? serenaCoding = null)
    {
        _assistantName = assistantProfile.Normalize().AssistantName;
        _activeUsers = activeUsers;
        _participantRosterAuthority = participantRosterAuthority;
        participantMemories ??= userMemories as IParticipantMemoryService;
        participantReceiptAuthority ??= (userMemories as Mem0UserMemoryService)?.ReceiptAuthority;
        _toolOutcomes = new AliFrameworkToolOutcomeSidecar();
        fileAccess.ConfigureOutcomeReporting(_toolOutcomes);
        workMemory.ConfigureOutcomeReporting(_toolOutcomes);
        codingModule ??= new AliCodingModule(fileAccess);
        var catalog = new AliToolCatalog(
            localLibrary,
            webSources,
            webResearch,
            memories,
            reminders,
            assistantProfile,
            mcpClients,
            toolPermissions,
            fileAccess,
            codingModule,
            () => _turn.Current,
            userMemories,
            activeUsers,
            memorySettings,
            orchestrationSettings,
            waitForPendingMemoryReview: null,
            semanticToolCatalog,
            shadowObserver,
            participantMemories,
            participantRosterAuthority,
            participantReceiptAuthority,
            internetSettings);
        var targetStateAdapters = fileAccess.TargetStateAdapters
            .Concat(workMemory.TargetStateAdapters)
            .Concat(codingModule.TargetStateAdapters)
            .ToArray();
        var executionEffectAdapters = fileAccess.ExecutionEffectAdapters
            .Concat(workMemory.ExecutionEffectAdapters)
            .Concat(codingModule.ExecutionEffectAdapters)
            .ToArray();
        _harness = new AliAgentHarnessRunner(
            chatClient,
            runtime,
            assistantProfile,
            catalog,
            mcpClients,
            toolPermissions,
            fileAccess,
            workMemory,
            activeUsers,
            () => _turn.Current,
            workflowCheckpointPath ?? Path.Combine(Path.GetTempPath(), "ProjectAli", "WorkflowCheckpoints"),
            orchestrationSettings ?? (() => new AgentOrchestrationSettings()),
            semanticToolCatalog,
            shadowObserver,
            capabilitySettingsDataRoot,
            conversationPublicationProbe is null
                ? null
                : new ConversationStoreFinalPublicationReconciler(
                    conversationPublicationProbe),
            targetStates: AliProductionTargetStateAdapters.Create(
                fileAccess,
                targetStateAdapters),
            executionAdapters: new AliExecutionEffectAdapterRegistry(
                executionEffectAdapters),
            durableEffects: AliProductionDurableEffectAdapters.Create(),
            toolOutcomes: _toolOutcomes,
            serenaCoding: serenaCoding);
    }

    internal CapabilitySettingsSnapshotOwner? CapabilitySettings => _harness.CapabilitySettings;

    private static string RequireCapabilitySettingsDataRoot(string capabilitySettingsDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilitySettingsDataRoot);
        return capabilitySettingsDataRoot;
    }

    public bool ResolveToolApproval(AgentToolApprovalDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return _harness.ResolveToolApproval(decision);
    }

    public async IAsyncEnumerable<AssistantStreamChunk> StreamAnswerAsync(
        string conversationId,
        string userMessageId,
        string assistantMessageId,
        string userText,
        IReadOnlyList<RuntimeChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);

        var channel = Channel.CreateUnbounded<AssistantStreamChunk>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        using var streamLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceAgentTurnAsync(
            conversationId,
            userMessageId,
            assistantMessageId,
            userText,
            history,
            attachments,
            channel.Writer,
            streamLifetime.Token);

        try
        {
            await foreach (var chunk in channel.Reader
                               .ReadAllAsync(streamLifetime.Token)
                               .ConfigureAwait(false))
            {
                yield return chunk;
            }

            await producer.ConfigureAwait(false);
        }
        finally
        {
            if (!producer.IsCompleted)
            {
                streamLifetime.Cancel();
                try
                {
                    await producer.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (streamLifetime.IsCancellationRequested)
                {
                    // Consumer disposal revokes any publication that was not yet exactly
                    // acknowledged by the live conversation sink.
                }
            }
        }
    }

    public async IAsyncEnumerable<AssistantStreamChunk> StreamRecoveryDecisionAsync(
        string conversationId,
        string userMessageId,
        string assistantMessageId,
        AgentRecoveryDecision decision,
        IReadOnlyList<RuntimeChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantMessageId);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(attachments);
        decision.Validate();
        if (!string.Equals(
                conversationId,
                decision.Prompt.DurableIdentity.ConversationId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A recovery stream cannot cross its durable conversation boundary.");
        }

        var channel = Channel.CreateUnbounded<AssistantStreamChunk>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        using var streamLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceRecoveryDecisionAsync(
            conversationId,
            userMessageId,
            assistantMessageId,
            decision,
            history,
            attachments,
            channel.Writer,
            streamLifetime.Token);

        try
        {
            await foreach (var chunk in channel.Reader
                               .ReadAllAsync(streamLifetime.Token)
                               .ConfigureAwait(false))
            {
                yield return chunk;
            }

            await producer.ConfigureAwait(false);
        }
        finally
        {
            if (!producer.IsCompleted)
            {
                streamLifetime.Cancel();
                try
                {
                    await producer.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (streamLifetime.IsCancellationRequested)
                {
                    // Consumer disposal revokes any publication that was not yet durably
                    // acknowledged by the conversation sink.
                }
            }
        }
    }

    public async Task CancelRecoveryDecisionAsync(
        string conversationId,
        AgentRecoveryPrompt prompt,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(prompt);
        prompt.Validate();
        if (!string.Equals(
                conversationId,
                prompt.DurableIdentity.ConversationId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A recovery cancellation cannot cross its durable conversation boundary.");
        }

        var (capturedUserSelection, observationIdentity) = CaptureTurnAdmissionIdentity(
            _activeUsers,
            conversationId,
            prompt.DurableIdentity.AssistantMessageId);
        var participantRoster = CaptureParticipantRoster(
            _participantRosterAuthority,
            capturedUserSelection,
            conversationId,
            prompt.DurableIdentity.AssistantMessageId);
        var turn = new CoordinatorTurnContext(
            conversationId,
            prompt.PromptPublicationId,
            prompt.DurableIdentity.AssistantMessageId,
            string.Empty,
            _ => { },
            capturedUserSelection,
            observationIdentity,
            participantRoster);
        using var turnScope = _turn.Enter(turn);
        try
        {
            await _harness.CancelRecoveryAsync(turn, prompt, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _toolOutcomes.DiscardCoordinatorTurn(turn);
        }
    }

    private async Task ProduceAgentTurnAsync(
        string conversationId,
        string userMessageId,
        string assistantMessageId,
        string userText,
        IReadOnlyList<RuntimeChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        ChannelWriter<AssistantStreamChunk> writer,
        CancellationToken cancellationToken)
    {
        var (capturedUserSelection, observationIdentity) = CaptureTurnAdmissionIdentity(
            _activeUsers,
            conversationId,
            assistantMessageId);
        var participantRoster = CaptureParticipantRoster(
            _participantRosterAuthority,
            capturedUserSelection,
            conversationId,
            assistantMessageId);

        var turn = new CoordinatorTurnContext(
            conversationId,
            userMessageId,
            assistantMessageId,
            userText,
            chunk => writer.TryWrite(chunk),
            capturedUserSelection,
            observationIdentity,
            participantRoster);
        using var turnScope = _turn.Enter(turn);
        try
        {
            async ValueTask<FinalAnswerPublicationAcknowledgment> PublishFinalAsync(
                FinalAnswerPublication publication,
                CancellationToken publicationCancellation)
            {
                publicationCancellation.ThrowIfCancellationRequested();
                var delivery = new FinalAnswerPublicationDelivery(
                    publication,
                    FinalAnswerPublicationDisposition.DisplayedInMemory);
                var accepted = writer.TryWrite(new AssistantStreamChunk(
                    publication.ConversationId,
                    publication.UserMessageId,
                    publication.AssistantMessageId,
                    turn.HasPublishedResponseText ? string.Empty : publication.AnswerText,
                    publication.EvidenceStatus,
                    publication.FinishReason)
                {
                    FinalPublicationDelivery = delivery
                });
                if (!accepted)
                {
                    return FinalAnswerPublicationAcknowledgment.Rejected(publication);
                }

                return await delivery.WaitAsync(publicationCancellation).ConfigureAwait(false);
            }

            var result = await _harness.RunAsync(
                turn,
                userText,
                history,
                attachments,
                PublishFinalAsync,
                cancellationToken).ConfigureAwait(false);

            if (result.Paused)
            {
                if (result.ResumeIdentity is null)
                {
                    throw new InvalidDataException(
                        "A paused agent run did not retain its durable turn identity.");
                }
                turn.Report(
                    AgentActivityKind.Status,
                    result.StructuredRecoveryRequired
                        ? "Recovery decision required"
                        : "Turn paused",
                    result.StructuredRecoveryRequired
                        ? "Choose one of the recovery buttons; Ali will not infer the answer from chat text."
                        : $"{_assistantName} preserved the work and is waiting for your next message.");
            }
            else if (!result.CompletedSuccessfully)
            {
                turn.Report(
                    AgentActivityKind.Warning,
                    "Request not completed",
                    $"{_assistantName} returned the exact unresolved Workspace obstacle without claiming success.");
            }
            else
            {
                turn.Report(
                    AgentActivityKind.Complete,
                    "Response complete",
                    $"{_assistantName} finished the agent run.");
            }

            writer.TryComplete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            turn.Report(AgentActivityKind.Error, "Agent run failed safely", ex.Message);
            writer.TryComplete(ex);
        }
        finally
        {
            _toolOutcomes.DiscardCoordinatorTurn(turn);
        }
    }

    private async Task ProduceRecoveryDecisionAsync(
        string conversationId,
        string userMessageId,
        string assistantMessageId,
        AgentRecoveryDecision decision,
        IReadOnlyList<RuntimeChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        ChannelWriter<AssistantStreamChunk> writer,
        CancellationToken cancellationToken)
    {
        var (capturedUserSelection, observationIdentity) = CaptureTurnAdmissionIdentity(
            _activeUsers,
            conversationId,
            assistantMessageId);
        var participantRoster = CaptureParticipantRoster(
            _participantRosterAuthority,
            capturedUserSelection,
            conversationId,
            assistantMessageId);
        var turn = new CoordinatorTurnContext(
            conversationId,
            userMessageId,
            assistantMessageId,
            string.Empty,
            chunk => writer.TryWrite(chunk),
            capturedUserSelection,
            observationIdentity,
            participantRoster);
        using var turnScope = _turn.Enter(turn);
        try
        {
            async ValueTask<FinalAnswerPublicationAcknowledgment> PublishFinalAsync(
                FinalAnswerPublication publication,
                CancellationToken publicationCancellation)
            {
                publicationCancellation.ThrowIfCancellationRequested();
                var delivery = new FinalAnswerPublicationDelivery(publication);
                var accepted = writer.TryWrite(new AssistantStreamChunk(
                    publication.ConversationId,
                    publication.UserMessageId,
                    publication.AssistantMessageId,
                    publication.AnswerText,
                    publication.EvidenceStatus,
                    publication.FinishReason)
                {
                    FinalPublicationDelivery = delivery
                });
                if (!accepted)
                {
                    return FinalAnswerPublicationAcknowledgment.Rejected(publication);
                }

                return await delivery.WaitAsync(publicationCancellation).ConfigureAwait(false);
            }

            var result = await _harness.ResolveRecoveryAsync(
                turn,
                decision,
                history,
                attachments,
                PublishFinalAsync,
                cancellationToken).ConfigureAwait(false);
            if (result.Paused)
            {
                if (result.ResumeIdentity is null)
                {
                    throw new InvalidDataException(
                        "A paused recovery run did not retain its durable turn identity.");
                }

                turn.Report(
                    AgentActivityKind.Status,
                    result.StructuredRecoveryRequired
                        ? "Recovery decision required"
                        : "Turn paused",
                    result.StructuredRecoveryRequired
                        ? "Choose one of the two recovery options; Ali will not infer the answer from chat text."
                        : $"{_assistantName} preserved the work and is waiting for your next message.");
            }
            else if (decision.Choice == AgentRecoveryDecisionChoice.ConfirmDisplayed)
            {
                turn.Report(
                    AgentActivityKind.Complete,
                    "Answer display confirmed",
                    "Ali closed the recovered turn without publishing a duplicate answer.");
            }
            else
            {
                turn.Report(
                    AgentActivityKind.Complete,
                    "Recovery complete",
                    $"{_assistantName} applied the explicit recovery decision and finished the agent run.");
            }

            writer.TryComplete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            turn.Report(AgentActivityKind.Error, "Recovery failed safely", ex.Message);
            writer.TryComplete(ex);
        }
        finally
        {
            _toolOutcomes.DiscardCoordinatorTurn(turn);
        }
    }

    internal static (
        ActiveUserSelectionSnapshot? CapturedUserSelection,
        TurnIdentity? ObservationIdentity) CaptureTurnAdmissionIdentity(
            IActiveUserSession? activeUsers,
            string conversationId,
            string assistantMessageId)
    {
        ActiveUserSelectionSnapshot? capturedUserSelection;
        try
        {
            capturedUserSelection = activeUsers?.CaptureSelectionSnapshot();
        }
        catch
        {
            // An unusable selection source must fail closed for all user-bound work.
            return (ActiveUserSelectionSnapshot.SelectionRequired, null);
        }

        if (capturedUserSelection?.IsResolved != true)
        {
            return (capturedUserSelection, null);
        }

        try
        {
            return (
                capturedUserSelection,
                new TurnIdentity(
                    capturedUserSelection.SelectedUser!.StableId,
                    conversationId,
                    assistantMessageId));
        }
        catch
        {
            // Protected evidence is optional. Its identity validation cannot replace an
            // already captured user or alter live memory, permission, or tool behavior.
            return (capturedUserSelection, null);
        }
    }

    private static ParticipantRosterSnapshot? CaptureParticipantRoster(
        IParticipantRosterAuthority? rosterAuthority,
        ActiveUserSelectionSnapshot? capturedSelection,
        string conversationId,
        string turnId)
    {
        if (rosterAuthority is null
            || capturedSelection is null)
        {
            return null;
        }

        try
        {
            var roster = rosterAuthority.CaptureAtAdmission(
                turnId,
                conversationId,
                DateTimeOffset.UtcNow);
            var capturedReference = capturedSelection.IsResolved
                ? capturedSelection.SelectedUser!.Normalize().StableId
                : null;
            if (!string.Equals(
                    roster.SelectedParticipantReference,
                    capturedReference,
                    StringComparison.Ordinal))
            {
                return null;
            }
            return rosterAuthority.CheckCurrent(roster).IsCurrent ? roster : null;
        }
        catch
        {
            // Participant memory is optional and fails closed without an admitted,
            // live-generation roster. Other Agent Framework capabilities continue.
            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _harness.Dispose();
        }
    }

}
