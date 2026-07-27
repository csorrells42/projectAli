using System.Text.Json;
using Ali.Modules.Runtime.Models;
using Ali.Modules.Runtime;

namespace Ali.Modules.Runtime;

public sealed class SafeActivatingLocalRuntime : ILocalModelRuntime, IReasoningEffortRuntime
{
    private readonly ILocalModelRuntime _fallbackRuntime;
    private ILocalModelRuntime? _candidateRuntime;
    private ILocalModelRuntime? _lastHealthCheckedRuntime;
    private ILocalModelRuntime? _lastKnownGoodRuntime;
    private ILocalModelRuntime _activeRuntime;
    private bool _activeRuntimeUnloadedForCandidate;

    public SafeActivatingLocalRuntime(
        ILocalModelRuntime fallbackRuntime,
        ILocalModelRuntime? candidateRuntime)
    {
        _fallbackRuntime = fallbackRuntime;
        _candidateRuntime = candidateRuntime;
        _activeRuntime = fallbackRuntime;
    }

    public ModelProfile ActiveProfile => _activeRuntime.ActiveProfile;

    public RuntimeHealthCheck? LastHealthCheck { get; private set; }

    public bool CanActivateCandidate =>
        LastHealthCheck is { Succeeded: true } && _lastHealthCheckedRuntime is not null;

    public bool CanRevertToLastKnownGood => _lastKnownGoodRuntime is not null;

    public bool IsUsingFallback => ReferenceEquals(_activeRuntime, _fallbackRuntime);

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

    public void ConfigureCandidate(ILocalModelRuntime? candidateRuntime)
    {
        _candidateRuntime = candidateRuntime;
        _activeRuntimeUnloadedForCandidate = false;
        _lastHealthCheckedRuntime = null;
        LastHealthCheck = null;
    }

    public IAsyncEnumerable<ModelToken> StreamChatAsync(
        ChatRequest request,
        CancellationToken cancellationToken)
    {
        _activeRuntimeUnloadedForCandidate = false;
        return _activeRuntime.StreamChatAsync(request, cancellationToken);
    }

    public Task<RuntimeHealthCheck> CheckHealthAsync(CancellationToken cancellationToken) =>
        CheckCandidateAsync(cancellationToken);

    public Task ShutdownAsync(CancellationToken cancellationToken) =>
        _activeRuntime.ShutdownAsync(cancellationToken);

    public Task<RuntimeHealthCheck> CheckActiveAsync(CancellationToken cancellationToken) =>
        _activeRuntime.CheckHealthAsync(cancellationToken);

    public async Task<RuntimeHealthCheck> CheckCandidateAsync(CancellationToken cancellationToken)
    {
        if (_candidateRuntime is null)
        {
            LastHealthCheck = await _fallbackRuntime.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            _lastHealthCheckedRuntime = null;
            return LastHealthCheck;
        }

        await UnloadActiveModelBeforeSwitchAsync(_candidateRuntime, cancellationToken).ConfigureAwait(false);

        LastHealthCheck = await _candidateRuntime.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _candidateRuntime.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TimeoutException)
        {
            LastHealthCheck = LastHealthCheck with
            {
                Succeeded = false,
                Summary = $"{LastHealthCheck.Summary} Candidate release verification failed: {ex.Message}",
                ErrorText = ex.Message
            };
        }

        _lastHealthCheckedRuntime = LastHealthCheck.Succeeded ? _candidateRuntime : null;
        return LastHealthCheck;
    }

    public bool ActivateLastHealthChecked()
    {
        if (!CanActivateCandidate || _lastHealthCheckedRuntime is null)
        {
            return false;
        }

        _activeRuntime = _lastHealthCheckedRuntime;
        _lastKnownGoodRuntime = _activeRuntime;
        _activeRuntimeUnloadedForCandidate = false;
        return true;
    }

    public async Task RevertToFallbackAsync(CancellationToken cancellationToken)
    {
        await ShutdownActiveRuntimeBeforeTransitionAsync(_fallbackRuntime, cancellationToken).ConfigureAwait(false);
        _activeRuntime = _fallbackRuntime;
        _activeRuntimeUnloadedForCandidate = false;
    }

    public async Task<bool> RevertToLastKnownGoodAsync(CancellationToken cancellationToken)
    {
        if (_lastKnownGoodRuntime is null)
        {
            return false;
        }

        await ShutdownActiveRuntimeBeforeTransitionAsync(_lastKnownGoodRuntime, cancellationToken).ConfigureAwait(false);
        _activeRuntime = _lastKnownGoodRuntime;
        _activeRuntimeUnloadedForCandidate = false;
        return true;
    }

    private async Task UnloadActiveModelBeforeSwitchAsync(
        ILocalModelRuntime candidateRuntime,
        CancellationToken cancellationToken)
    {
        if (_activeRuntimeUnloadedForCandidate
            || _activeRuntime is not IModelSwitchAwareRuntime active
            || candidateRuntime is not IModelSwitchAwareRuntime candidate
            || string.Equals(active.RuntimeIdentity, candidate.RuntimeIdentity, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await active.UnloadForModelSwitchAsync(cancellationToken).ConfigureAwait(false);
        _activeRuntimeUnloadedForCandidate = true;
    }

    private async Task ShutdownActiveRuntimeBeforeTransitionAsync(
        ILocalModelRuntime targetRuntime,
        CancellationToken cancellationToken)
    {
        if (ReferenceEquals(_activeRuntime, targetRuntime))
        {
            return;
        }

        if (_activeRuntime is IModelSwitchAwareRuntime active
            && targetRuntime is IModelSwitchAwareRuntime target
            && string.Equals(active.RuntimeIdentity, target.RuntimeIdentity, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await _activeRuntime.ShutdownAsync(cancellationToken).ConfigureAwait(false);
    }
}
