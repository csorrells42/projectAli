using System.Collections.Frozen;
using System.Text.Json;
using Ali.Modules.Capabilities;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Execution;
using Ali.Modules.Orchestration.State;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Orchestration;

/// <summary>
/// The exact identity under which one effect adapter is allowed to prepare and reconcile a
/// capability. Registration is explicit; tool-name patterns and capability inference are not
/// execution authority.
/// </summary>
internal interface IAliExecutionEffectAdapter : ITurnActionReconciler
{
    string ToolName { get; }

    string CapabilityId { get; }

    /// <summary>
    /// Prepares durable, target-specific execution material without applying the target-domain
    /// effect. The returned identity must name the exact material that the invocation consumes.
    /// </summary>
    ValueTask<AliExecutionPreparation> PrepareAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken);
}

internal sealed record AliExecutionPreparationRequest(
    TurnIdentity TurnIdentity,
    string CallId,
    string WorkItemId,
    string ToolName,
    string CapabilityId,
    string ReconcilerId,
    JsonElement Arguments,
    string CanonicalArgumentsDigest,
    string ActionIdentityFingerprint,
    string TargetVersionDigest,
    string PermissionReceiptDigest,
    string RegistryRevisionDigest,
    string ExecutionRegistryIdentityDigest)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(TurnIdentity);
        TurnStateIntegrity.RequireBoundedValue(CallId, 256, nameof(CallId));
        TurnStateIntegrity.RequireBoundedValue(WorkItemId, 256, nameof(WorkItemId));
        TurnStateIntegrity.RequireBoundedValue(ToolName, 256, nameof(ToolName));
        TurnStateIntegrity.RequireBoundedValue(CapabilityId, 256, nameof(CapabilityId));
        TurnStateIntegrity.RequireBoundedValue(ReconcilerId, 256, nameof(ReconcilerId));
        if (Arguments.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException(
                "Execution-preparation arguments must be defined.",
                nameof(Arguments));
        }

        TurnStateIntegrity.RequireDigest(
            CanonicalArgumentsDigest,
            nameof(CanonicalArgumentsDigest));
        TurnStateIntegrity.RequireDigest(
            ActionIdentityFingerprint,
            nameof(ActionIdentityFingerprint));
        TurnStateIntegrity.RequireDigest(TargetVersionDigest, nameof(TargetVersionDigest));
        TurnStateIntegrity.RequireDigest(
            PermissionReceiptDigest,
            nameof(PermissionReceiptDigest));
        TurnStateIntegrity.RequireDigest(
            RegistryRevisionDigest,
            nameof(RegistryRevisionDigest));
        TurnStateIntegrity.RequireDigest(
            ExecutionRegistryIdentityDigest,
            nameof(ExecutionRegistryIdentityDigest));
    }
}

internal sealed record AliExecutionPreparation(
    string PreparationIdentity,
    string RootBinding,
    string TargetVersionDigest)
{
    internal void Validate()
    {
        TurnStateIntegrity.RequireBoundedValue(
            PreparationIdentity,
            256,
            nameof(PreparationIdentity));
        TurnStateIntegrity.RequireOptionalIdentifier(
            PreparationIdentity,
            256,
            nameof(PreparationIdentity));
        TurnStateIntegrity.RequireBoundedValue(RootBinding, 2048, nameof(RootBinding));
        TurnStateIntegrity.RequireDigest(TargetVersionDigest, nameof(TargetVersionDigest));
    }
}

internal sealed class AliExecutionEffectAdapterRegistry
{
    private readonly FrozenDictionary<AdapterKey, IAliExecutionEffectAdapter> _adapters;
    private readonly IReadOnlyList<ITurnActionReconciler> _reconcilers;

    internal AliExecutionEffectAdapterRegistry(
        IEnumerable<IAliExecutionEffectAdapter>? adapters = null)
    {
        var byKey = new Dictionary<AdapterKey, IAliExecutionEffectAdapter>();
        var reconcilers = new Dictionary<string, ITurnActionReconciler>(StringComparer.Ordinal);
        foreach (var adapter in adapters ?? [])
        {
            ArgumentNullException.ThrowIfNull(adapter);
            var exactIdentity = new AliExactExecutionAdapterIdentity(
                adapter.ToolName,
                adapter.CapabilityId,
                adapter.ReconcilerId);
            var key = new AdapterKey(
                exactIdentity.ToolName,
                exactIdentity.CapabilityId,
                exactIdentity.ReconcilerId);
            if (!byKey.TryAdd(key, adapter))
            {
                throw new ArgumentException(
                    $"Execution adapter '{adapter.ToolName}'/'{adapter.CapabilityId}'/"
                    + $"'{adapter.ReconcilerId}' is registered more than once.",
                    nameof(adapters));
            }

            if (!reconcilers.TryAdd(
                    adapter.ReconcilerId,
                    new ExactAdapterReconciler(adapter)))
            {
                throw new ArgumentException(
                    $"Execution reconciler '{adapter.ReconcilerId}' is registered more than once.",
                    nameof(adapters));
            }
        }

        _adapters = byKey.ToFrozenDictionary();
        _reconcilers = Array.AsReadOnly(reconcilers.Values.ToArray());
    }

    internal static AliExecutionEffectAdapterRegistry Empty { get; } = new();

    internal IReadOnlyList<ITurnActionReconciler> Reconcilers => _reconcilers;

    internal bool TryResolve(
        CapabilityDescriptor descriptor,
        out IAliExecutionEffectAdapter? adapter)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Effect.ReconcilerId is null)
        {
            adapter = null;
            return false;
        }

        return _adapters.TryGetValue(
            new AdapterKey(
                descriptor.ToolName,
                descriptor.Id,
                descriptor.Effect.ReconcilerId),
            out adapter);
    }

    private readonly record struct AdapterKey(
        string ToolName,
        string CapabilityId,
        string ReconcilerId);

    private sealed class ExactAdapterReconciler(
        IAliExecutionEffectAdapter adapter) : ITurnActionReconciler
    {
        public string ReconcilerId => adapter.ReconcilerId;

        public ValueTask<ActionReconciliationResult> ReconcileAsync(
            TurnIdentity identity,
            PreparedActionIntent intent,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentNullException.ThrowIfNull(intent);
            if (!string.Equals(intent.ToolName, adapter.ToolName, StringComparison.Ordinal)
                || !string.Equals(
                    intent.CapabilityId,
                    adapter.CapabilityId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    intent.ReconcilerId,
                    adapter.ReconcilerId,
                    StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(intent.PreparationIdentity))
            {
                return ValueTask.FromResult(
                    ActionReconciliationResult.Unknown(
                        "effect-adapter-identity-mismatch"));
            }

            return adapter.ReconcileAsync(identity, intent, cancellationToken);
        }
    }
}

internal sealed record AliPreparedExecution(
    PreparedActionIntent Intent,
    TurnState State,
    AliExecutionInvocationScope InvocationScope);

/// <summary>
/// Creates one durable prepare record before granting an exact inner invocation. It has no API
/// that can apply the target effect itself.
/// </summary>
internal sealed class AliExecutionBroker
{
    private readonly AliExecutionEffectAdapterRegistry _adapters;

    internal AliExecutionBroker(AliExecutionEffectAdapterRegistry adapters)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
    }

    internal bool TryResolve(
        CapabilityDescriptor descriptor,
        out IAliExecutionEffectAdapter? adapter) =>
        _adapters.TryResolve(descriptor, out adapter);

    internal async ValueTask<AliPreparedExecution> PrepareAsync(
        IAliExecutionEffectAdapter adapter,
        AliExecutionPreparationRequest request,
        bool requiresApproval,
        TurnTransitionWriter writer,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writer);
        request.Validate();
        if (!string.Equals(adapter.ToolName, request.ToolName, StringComparison.Ordinal)
            || !string.Equals(
                adapter.CapabilityId,
                request.CapabilityId,
                StringComparison.Ordinal)
            || !string.Equals(
                adapter.ReconcilerId,
                request.ReconcilerId,
                StringComparison.Ordinal))
        {
            throw new AliExecutionPreparationException(
                "The selected execution adapter does not match the accepted call identity.");
        }

        var preparation = await adapter
            .PrepareAsync(request with { Arguments = request.Arguments.Clone() }, cancellationToken)
            .ConfigureAwait(false);
        if (preparation is null)
        {
            throw new AliExecutionPreparationException(
                "The execution adapter returned no preparation identity.");
        }

        try
        {
            preparation.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new AliExecutionPreparationException(
                "The execution adapter returned invalid preparation metadata.",
                ex);
        }

        if (!string.Equals(
                preparation.TargetVersionDigest,
                request.TargetVersionDigest,
                StringComparison.Ordinal))
        {
            throw new AliExecutionPreparationException(
                "The execution adapter prepared a different target-version identity.");
        }

        var intent = new PreparedActionIntent(
            request.ActionIdentityFingerprint,
            request.WorkItemId,
            request.ToolName,
            request.CapabilityId,
            request.CanonicalArgumentsDigest,
            request.TargetVersionDigest,
            request.PermissionReceiptDigest,
            request.RegistryRevisionDigest,
            request.ExecutionRegistryIdentityDigest,
            request.ReconcilerId,
            preparation.RootBinding,
            requiresApproval,
            request.CallId,
            preparation.PreparationIdentity);
        intent.Validate();
        var written = await writer.PrepareActionAsync(
            request.TurnIdentity,
            expectedRevision,
            intent,
            AliPlanningStateCoordinator.Correlation(
                request.TurnIdentity,
                "action-prepare",
                request.CallId + ":" + request.ActionIdentityFingerprint,
                expectedRevision),
            cancellationToken).ConfigureAwait(false);
        if (written.Status != TurnTransitionWriteStatus.Committed
            || written.State is null
            || !written.State.TryGetPendingAction(intent.IdempotencyKey, out var pending)
            || pending is null
            || pending.Intent != intent)
        {
            throw new InvalidOperationException(
                "The durable action preparation lost its exact accepted-call boundary.");
        }

        var durableIntent = pending.Intent;
        var grant = new AliExecutionGrant(
            durableIntent.IdempotencyKey,
            durableIntent.AcceptedCallId!,
            durableIntent.ToolName,
            durableIntent.CapabilityId,
            durableIntent.CanonicalArgumentsDigest,
            durableIntent.TargetVersionDigest,
            durableIntent.PermissionReceiptDigest,
            durableIntent.ExecutionRegistryIdentityDigest,
            durableIntent.ReconcilerId,
            durableIntent.PreparationIdentity!,
            durableIntent.RootBinding);
        return new AliPreparedExecution(
            durableIntent,
            written.State,
            new AliExecutionInvocationScope(grant));
    }
}

internal sealed class AliExecutionPreparationException : Exception
{
    internal AliExecutionPreparationException(string message)
        : base(message)
    {
    }

    internal AliExecutionPreparationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed record AliExecutionGrant(
    string IdempotencyKey,
    string CallId,
    string ToolName,
    string CapabilityId,
    string CanonicalArgumentsDigest,
    string TargetVersionDigest,
    string PermissionReceiptDigest,
    string RegistryIdentityDigest,
    string ReconcilerId,
    string PreparationIdentity,
    string RootBinding);

/// <summary>
/// A one-use invocation grant. The activation is visible only through the current async
/// execution context and becomes unusable in every captured context as soon as it is disposed.
/// </summary>
internal sealed class AliExecutionInvocationScope
{
    private readonly AliExecutionGrant _grant;
    private int _entered;

    internal AliExecutionInvocationScope(AliExecutionGrant grant)
    {
        _grant = grant ?? throw new ArgumentNullException(nameof(grant));
        _ = new AliExactExecutionAdapterIdentity(
            grant.ToolName,
            grant.CapabilityId,
            grant.ReconcilerId);
    }

    internal AliExecutionInvocationActivation Enter(AIFunctionArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (Interlocked.CompareExchange(ref _entered, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "This durable execution authorization has already been entered.");
        }

        var element = JsonSerializer.SerializeToElement(arguments).Clone();
        var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(element);
        string argumentsDigest;
        try
        {
            argumentsDigest = TurnStateIntegrity.Digest(bytes);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }

        if (!string.Equals(
                argumentsDigest,
                _grant.CanonicalArgumentsDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The durable execution authorization does not match the inner call arguments.");
        }

        var frame = new AliExecutionGrantFrame(_grant);
        var previous = AliExecutionGrantContext.Push(frame);
        return new AliExecutionInvocationActivation(frame, previous);
    }
}

internal sealed class AliExecutionInvocationActivation(
    AliExecutionGrantFrame frame,
    AliExecutionGrantFrame? previous) : IDisposable, IAsyncDisposable
{
    private const string AbandonedReasonCode =
        "invocation-activation-disposed-without-terminal";
    private int _disposed;

    internal ValueTask CompleteAsync(
        object? result,
        CancellationToken cancellationToken) =>
        frame.CompleteAsync(result, cancellationToken);

    internal ValueTask FailAsync(
        Exception exception,
        CancellationToken cancellationToken) =>
        frame.FailAsync(exception, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            Task.Run(async () =>
                    await frame.MarkInDoubtIfNeededAsync(
                            AbandonedReasonCode,
                            CancellationToken.None)
                        .ConfigureAwait(false))
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            frame.Deactivate();
            AliExecutionGrantContext.Pop(frame, previous);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            return frame.MarkInDoubtIfNeededAsync(
                AbandonedReasonCode,
                CancellationToken.None);
        }
        finally
        {
            // Pop in the caller's execution context before returning the ValueTask. AsyncLocal
            // changes made only in an awaited child continuation do not flow back to the caller.
            frame.Deactivate();
            AliExecutionGrantContext.Pop(frame, previous);
        }
    }
}

internal static class AliExecutionGrantContext
{
    private static readonly AsyncLocal<AliExecutionGrantFrame?> CurrentFrame = new();

    internal static bool HasCurrentActiveGrant =>
        CurrentFrame.Value is { IsActive: true };

    internal static AliExecutionGrantFrame? Push(AliExecutionGrantFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var previous = CurrentFrame.Value;
        CurrentFrame.Value = frame;
        return previous;
    }

    internal static void Pop(
        AliExecutionGrantFrame frame,
        AliExecutionGrantFrame? previous)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (ReferenceEquals(CurrentFrame.Value, frame))
        {
            CurrentFrame.Value = previous;
        }
    }

    internal static bool TryConsumeCurrent(
        string expectedToolName,
        string expectedCapabilityId,
        string expectedReconcilerId,
        out AliExecutionGrant? grant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedToolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCapabilityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedReconcilerId);
        var frame = CurrentFrame.Value;
        if (frame is not null
            && frame.TryConsumeIdentity(
                expectedToolName,
                expectedCapabilityId,
                expectedReconcilerId))
        {
            grant = frame.Grant;
            return true;
        }

        grant = null;
        return false;
    }

    internal static bool TryGetCurrentBinding(
        string expectedToolName,
        string expectedCapabilityId,
        string expectedReconcilerId,
        out string? preparationIdentity,
        out string? rootBinding)
    {
        var exactIdentity = new AliExactExecutionAdapterIdentity(
            expectedToolName,
            expectedCapabilityId,
            expectedReconcilerId);
        var frame = CurrentFrame.Value;
        if (frame is not null
            && frame.TryGetBinding(
                exactIdentity,
                out preparationIdentity,
                out rootBinding))
        {
            return true;
        }

        preparationIdentity = null;
        rootBinding = null;
        return false;
    }

    internal static bool TryConsumeCurrent(
        string expectedToolName,
        string expectedCapabilityId,
        string expectedReconcilerId,
        string expectedPreparationIdentity,
        string expectedRootBinding,
        out AliExecutionGrant? grant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedToolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCapabilityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedReconcilerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPreparationIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRootBinding);
        var frame = CurrentFrame.Value;
        if (frame is not null
            && frame.TryConsume(
                expectedToolName,
                expectedCapabilityId,
                expectedReconcilerId,
                expectedPreparationIdentity,
                expectedRootBinding))
        {
            grant = frame.Grant;
            return true;
        }

        grant = null;
        return false;
    }

    internal static bool TryRegisterCurrentCompletionParticipant(
        string expectedToolName,
        string expectedCapabilityId,
        string expectedReconcilerId,
        string expectedPreparationIdentity,
        string expectedRootBinding,
        IAliInvocationCompletionParticipant participant)
    {
        var exactIdentity = new AliExactExecutionAdapterIdentity(
            expectedToolName,
            expectedCapabilityId,
            expectedReconcilerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPreparationIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRootBinding);
        ArgumentNullException.ThrowIfNull(participant);
        var frame = CurrentFrame.Value;
        return frame is not null
            && frame.TryRegisterCompletionParticipant(
                exactIdentity,
                expectedPreparationIdentity,
                expectedRootBinding,
                participant);
    }
}

internal sealed class AliExecutionGrantFrame
{
    private readonly object _completionGate = new();
    private int _active = 1;
    private int _consumed;
    private bool _terminalSignaled;
    private IAliInvocationCompletionParticipant? _completionParticipant;

    internal AliExecutionGrantFrame(AliExecutionGrant grant)
    {
        Grant = grant ?? throw new ArgumentNullException(nameof(grant));
    }

    internal AliExecutionGrant Grant { get; }

    internal bool IsActive => Volatile.Read(ref _active) != 0;

    internal bool TryGetBinding(
        AliExactExecutionAdapterIdentity exactIdentity,
        out string? preparationIdentity,
        out string? rootBinding)
    {
        if (Volatile.Read(ref _active) == 0
            || !exactIdentity.Matches(
                Grant.ToolName,
                Grant.CapabilityId,
                Grant.ReconcilerId))
        {
            preparationIdentity = null;
            rootBinding = null;
            return false;
        }

        var matchedPreparationIdentity = Grant.PreparationIdentity;
        var matchedRootBinding = Grant.RootBinding;
        if (Volatile.Read(ref _active) == 0)
        {
            preparationIdentity = null;
            rootBinding = null;
            return false;
        }

        preparationIdentity = matchedPreparationIdentity;
        rootBinding = matchedRootBinding;
        return true;
    }

    internal bool TryConsume(
        string expectedToolName,
        string expectedCapabilityId,
        string expectedReconcilerId,
        string expectedPreparationIdentity,
        string expectedRootBinding)
    {
        if (Volatile.Read(ref _active) == 0
            || !string.Equals(Grant.ToolName, expectedToolName, StringComparison.Ordinal)
            || !string.Equals(
                Grant.CapabilityId,
                expectedCapabilityId,
                StringComparison.Ordinal)
            || !string.Equals(
                Grant.ReconcilerId,
                expectedReconcilerId,
                StringComparison.Ordinal)
            || !string.Equals(
                Grant.PreparationIdentity,
                expectedPreparationIdentity,
                StringComparison.Ordinal)
            || !string.Equals(
                Grant.RootBinding,
                expectedRootBinding,
                StringComparison.Ordinal))
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _consumed, 1, 0) == 0
            && Volatile.Read(ref _active) != 0;
    }

    internal bool TryConsumeIdentity(
        string expectedToolName,
        string expectedCapabilityId,
        string expectedReconcilerId)
    {
        if (Volatile.Read(ref _active) == 0
            || !string.Equals(Grant.ToolName, expectedToolName, StringComparison.Ordinal)
            || !string.Equals(
                Grant.CapabilityId,
                expectedCapabilityId,
                StringComparison.Ordinal)
            || !string.Equals(
                Grant.ReconcilerId,
                expectedReconcilerId,
                StringComparison.Ordinal))
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _consumed, 1, 0) == 0
            && Volatile.Read(ref _active) != 0;
    }

    internal bool TryRegisterCompletionParticipant(
        AliExactExecutionAdapterIdentity exactIdentity,
        string expectedPreparationIdentity,
        string expectedRootBinding,
        IAliInvocationCompletionParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        lock (_completionGate)
        {
            if (Volatile.Read(ref _active) == 0
                || Volatile.Read(ref _consumed) == 0
                || _terminalSignaled
                || _completionParticipant is not null
                || !exactIdentity.Matches(
                    Grant.ToolName,
                    Grant.CapabilityId,
                    Grant.ReconcilerId)
                || !string.Equals(
                    Grant.PreparationIdentity,
                    expectedPreparationIdentity,
                    StringComparison.Ordinal)
                || !string.Equals(
                    Grant.RootBinding,
                    expectedRootBinding,
                    StringComparison.Ordinal))
            {
                return false;
            }

            _completionParticipant = participant;
            return Volatile.Read(ref _active) != 0;
        }
    }

    internal async ValueTask CompleteAsync(
        object? result,
        CancellationToken cancellationToken)
    {
        var participant = BeginTerminalTransition(requireConsumedGrant: true);
        if (participant is not null)
        {
            await participant.CompleteAsync(result, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal async ValueTask FailAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var participant = BeginTerminalTransition(requireConsumedGrant: false);
        if (participant is not null)
        {
            await participant.FailAsync(exception, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal async ValueTask MarkInDoubtIfNeededAsync(
        string reasonCode,
        CancellationToken cancellationToken)
    {
        AliDurableInvocationValidation.RequireSafeIdentifier(
            reasonCode,
            128,
            nameof(reasonCode));
        IAliInvocationCompletionParticipant? participant;
        lock (_completionGate)
        {
            if (Volatile.Read(ref _active) == 0 || _terminalSignaled)
            {
                return;
            }

            _terminalSignaled = true;
            participant = _completionParticipant;
        }

        if (participant is not null)
        {
            await participant.MarkInDoubtAsync(reasonCode, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private IAliInvocationCompletionParticipant? BeginTerminalTransition(
        bool requireConsumedGrant)
    {
        lock (_completionGate)
        {
            if (Volatile.Read(ref _active) == 0)
            {
                throw new InvalidOperationException(
                    "The durable execution authorization is no longer active.");
            }
            if (requireConsumedGrant && Volatile.Read(ref _consumed) == 0)
            {
                throw new InvalidOperationException(
                    "A durable invocation cannot complete before its exact grant is consumed.");
            }
            if (_terminalSignaled)
            {
                throw new InvalidOperationException(
                    "The durable execution authorization already received a terminal signal.");
            }

            _terminalSignaled = true;
            return _completionParticipant;
        }
    }

    internal void Deactivate() => Volatile.Write(ref _active, 0);
}
