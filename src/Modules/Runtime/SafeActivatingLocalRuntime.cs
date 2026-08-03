using System.Runtime.CompilerServices;
using System.Text.Json;
using Ali.Modules.Runtime.Models;
using Ali.Modules.Runtime;

namespace Ali.Modules.Runtime;

internal sealed class StaleBoundModelDispatchException : InvalidOperationException
{
    internal StaleBoundModelDispatchException(string message)
        : base(message)
    {
    }
}

public sealed partial class SafeActivatingLocalRuntime : ILocalModelRuntime, IReasoningEffortRuntime, Microsoft.Extensions.AI.IChatClient, IBoundModelDispatchSource
{
    private readonly ILocalModelRuntime _fallbackRuntime;
    private ILocalModelRuntime? _candidateRuntime;
    private ILocalModelRuntime? _lastHealthCheckedRuntime;
    private ILocalModelRuntime? _lastKnownGoodRuntime;
    private ILocalModelRuntime _activeRuntime;
    private RuntimeHealthCheck? _lastHealthCheck;
    private bool _activeRuntimeUnloadedForCandidate;
    // One model server can be unloaded or replaced by the settings UI. Holding this gate for
    // the complete request/stream lifetime makes dispatch a lease: transitions wait for it, and
    // a dispatch queued behind a transition revalidates its pinned runtime before touching it.
    private readonly SemaphoreSlim _dispatchTransitionGate = new(1, 1);

    public SafeActivatingLocalRuntime(
        ILocalModelRuntime fallbackRuntime,
        ILocalModelRuntime? candidateRuntime)
    {
        _fallbackRuntime = fallbackRuntime;
        _candidateRuntime = candidateRuntime;
        _activeRuntime = fallbackRuntime;
    }

    public ModelProfile ActiveProfile => Volatile.Read(ref _activeRuntime).ActiveProfile;

    public RuntimeHealthCheck? LastHealthCheck => Volatile.Read(ref _lastHealthCheck);

    public bool CanActivateCandidate =>
        LastHealthCheck is { Succeeded: true }
        && Volatile.Read(ref _lastHealthCheckedRuntime) is not null;

    public bool CanRevertToLastKnownGood => Volatile.Read(ref _lastKnownGoodRuntime) is not null;

    public bool IsUsingFallback =>
        ReferenceEquals(Volatile.Read(ref _activeRuntime), _fallbackRuntime);

    public string ReasoningEffort =>
        (_activeRuntime as IReasoningEffortRuntime)?.ReasoningEffort
        ?? (_candidateRuntime as IReasoningEffortRuntime)?.ReasoningEffort
        ?? OllamaRuntimeSafetyPolicy.DefaultGptOssReasoningEffort;

    public void SetReasoningEffort(string effort)
    {
        var visited = new HashSet<ILocalModelRuntime>(ReferenceEqualityComparer.Instance);
        foreach (var runtime in new[]
                 {
                     _activeRuntime,
                     _candidateRuntime,
                     _lastHealthCheckedRuntime,
                     _lastKnownGoodRuntime
                 })
        {
            if (runtime is IReasoningEffortRuntime adjustable && visited.Add(runtime))
            {
                adjustable.SetReasoningEffort(effort);
            }
        }
    }

    BoundModelDispatchSnapshot IBoundModelDispatchSource.CaptureBoundModelDispatch()
    {
        var runtime = Volatile.Read(ref _activeRuntime);
        if (runtime is IBoundModelDispatchSource boundSource
            && !ReferenceEquals(runtime, this))
        {
            var dispatch = boundSource.CaptureBoundModelDispatch()
                ?? throw new InvalidOperationException(
                    "The active runtime returned no exact bound dispatch snapshot.");
            ArgumentNullException.ThrowIfNull(dispatch.ChatClient);
            return dispatch with
            {
                ChatClient = CreatePinnedBoundDispatchClient(
                    runtime,
                    dispatch.ChatClient)
            };
        }

        throw new InvalidOperationException(
            "The active runtime cannot expose the exact client and settings envelope required for a bound completion dispatch.");
    }

    public void ConfigureCandidate(ILocalModelRuntime? candidateRuntime)
    {
        if (!_dispatchTransitionGate.Wait(0))
        {
            throw new InvalidOperationException(
                "The runtime candidate cannot be changed while a model dispatch or runtime transition is active.");
        }

        try
        {
            Volatile.Write(ref _candidateRuntime, candidateRuntime);
            Volatile.Write(ref _activeRuntimeUnloadedForCandidate, false);
            Volatile.Write(ref _lastHealthCheckedRuntime, null);
            Volatile.Write(ref _lastHealthCheck, null);
        }
        finally
        {
            _dispatchTransitionGate.Release();
        }
    }

    public IAsyncEnumerable<ModelToken> StreamChatAsync(
        ChatRequest request,
        CancellationToken cancellationToken) =>
        StreamChatUnderDispatchGateAsync(request, cancellationToken);

    private async IAsyncEnumerable<ModelToken> StreamChatUnderDispatchGateAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await _dispatchTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _activeRuntimeUnloadedForCandidate, false);
            var runtime = Volatile.Read(ref _activeRuntime);
            await foreach (var token in runtime
                               .StreamChatAsync(request, cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return token;
            }
        }
        finally
        {
            _dispatchTransitionGate.Release();
        }
    }

    public Task<RuntimeHealthCheck> CheckHealthAsync(CancellationToken cancellationToken) =>
        CheckCandidateAsync(cancellationToken);

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await _dispatchTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Volatile.Read(ref _activeRuntime)
                .ShutdownAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _dispatchTransitionGate.Release();
        }
    }

    public async Task<RuntimeHealthCheck> CheckActiveAsync(CancellationToken cancellationToken)
    {
        await _dispatchTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Volatile.Read(ref _activeRuntime)
                .CheckHealthAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _dispatchTransitionGate.Release();
        }
    }

    public async Task<RuntimeHealthCheck> CheckCandidateAsync(CancellationToken cancellationToken)
    {
        await _dispatchTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidateRuntime = Volatile.Read(ref _candidateRuntime);
            if (candidateRuntime is null)
            {
                var fallbackHealth = await _fallbackRuntime
                    .CheckHealthAsync(cancellationToken)
                    .ConfigureAwait(false);
                Volatile.Write(ref _lastHealthCheck, fallbackHealth);
                Volatile.Write(ref _lastHealthCheckedRuntime, null);
                return fallbackHealth;
            }

            await UnloadActiveModelBeforeSwitchAsync(candidateRuntime, cancellationToken)
                .ConfigureAwait(false);

            var health = await candidateRuntime
                .CheckHealthAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!health.Succeeded)
            {
                try
                {
                    await candidateRuntime.ShutdownAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or TimeoutException)
                {
                    health = health with
                    {
                        Summary = $"{health.Summary} Failed candidate cleanup also failed: {ex.Message}",
                        ErrorText = ex.Message
                    };
                }
            }

            Volatile.Write(ref _lastHealthCheck, health);
            Volatile.Write(
                ref _lastHealthCheckedRuntime,
                health.Succeeded ? candidateRuntime : null);
            return health;
        }
        finally
        {
            _dispatchTransitionGate.Release();
        }
    }

    public bool ActivateLastHealthChecked()
    {
        if (!_dispatchTransitionGate.Wait(0))
        {
            return false;
        }

        try
        {
            var lastHealthCheckedRuntime = Volatile.Read(ref _lastHealthCheckedRuntime);
            if (LastHealthCheck is not { Succeeded: true }
                || lastHealthCheckedRuntime is null)
            {
                return false;
            }

            Volatile.Write(ref _activeRuntime, lastHealthCheckedRuntime);
            Volatile.Write(ref _lastKnownGoodRuntime, lastHealthCheckedRuntime);
            Volatile.Write(ref _activeRuntimeUnloadedForCandidate, false);
            return true;
        }
        finally
        {
            _dispatchTransitionGate.Release();
        }
    }

    public async Task RevertToFallbackAsync(CancellationToken cancellationToken)
    {
        await _dispatchTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ShutdownActiveRuntimeBeforeTransitionAsync(_fallbackRuntime, cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _activeRuntime, _fallbackRuntime);
            Volatile.Write(ref _activeRuntimeUnloadedForCandidate, false);
        }
        finally
        {
            _dispatchTransitionGate.Release();
        }
    }

    public async Task<bool> RevertToLastKnownGoodAsync(CancellationToken cancellationToken)
    {
        await _dispatchTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lastKnownGoodRuntime = Volatile.Read(ref _lastKnownGoodRuntime);
            if (lastKnownGoodRuntime is null)
            {
                return false;
            }

            await ShutdownActiveRuntimeBeforeTransitionAsync(lastKnownGoodRuntime, cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _activeRuntime, lastKnownGoodRuntime);
            Volatile.Write(ref _activeRuntimeUnloadedForCandidate, false);
            return true;
        }
        finally
        {
            _dispatchTransitionGate.Release();
        }
    }

    private async Task UnloadActiveModelBeforeSwitchAsync(
        ILocalModelRuntime candidateRuntime,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _activeRuntimeUnloadedForCandidate)
            || Volatile.Read(ref _activeRuntime) is not IModelSwitchAwareRuntime active
            || candidateRuntime is not IModelSwitchAwareRuntime candidate
            || string.Equals(active.RuntimeIdentity, candidate.RuntimeIdentity, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await active.UnloadForModelSwitchAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _activeRuntimeUnloadedForCandidate, true);
    }

    private void NotifyPinnedBoundDispatchStarting(ILocalModelRuntime pinnedRuntime)
    {
        if (!ReferenceEquals(Volatile.Read(ref _activeRuntime), pinnedRuntime))
        {
            throw new StaleBoundModelDispatchException(
                "The bound model dispatch is stale because the active runtime changed. Capture a fresh dispatch before retrying.");
        }

        // A direct call through the exact pinned client can reload/use the logical active
        // runtime without passing through this switching facade. Keep the physical-load hint
        // honest so a later candidate check cannot skip the required unload.
        Volatile.Write(ref _activeRuntimeUnloadedForCandidate, false);
    }

    private async Task ShutdownActiveRuntimeBeforeTransitionAsync(
        ILocalModelRuntime targetRuntime,
        CancellationToken cancellationToken)
    {
        var activeRuntime = Volatile.Read(ref _activeRuntime);
        if (ReferenceEquals(activeRuntime, targetRuntime))
        {
            return;
        }

        if (activeRuntime is IModelSwitchAwareRuntime active
            && targetRuntime is IModelSwitchAwareRuntime target
            && string.Equals(active.RuntimeIdentity, target.RuntimeIdentity, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await activeRuntime.ShutdownAsync(cancellationToken).ConfigureAwait(false);
    }
}
