using Ali.Core.Models;
using Ali.Core.Runtime;

namespace Ali.Infrastructure.Runtime;

public sealed class SafeActivatingLocalRuntime : ILocalModelRuntime
{
    private readonly ILocalModelRuntime _fallbackRuntime;
    private ILocalModelRuntime? _candidateRuntime;
    private ILocalModelRuntime? _lastHealthCheckedRuntime;
    private ILocalModelRuntime? _lastKnownGoodRuntime;
    private ILocalModelRuntime _activeRuntime;

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

    public void ConfigureCandidate(ILocalModelRuntime? candidateRuntime)
    {
        _candidateRuntime = candidateRuntime;
        _lastHealthCheckedRuntime = null;
        LastHealthCheck = null;
    }

    public IAsyncEnumerable<ModelToken> StreamChatAsync(
        ChatRequest request,
        CancellationToken cancellationToken) =>
        _activeRuntime.StreamChatAsync(request, cancellationToken);

    public Task<RuntimeHealthCheck> CheckHealthAsync(CancellationToken cancellationToken) =>
        CheckCandidateAsync(cancellationToken);

    public async Task<RuntimeHealthCheck> CheckCandidateAsync(CancellationToken cancellationToken)
    {
        if (_candidateRuntime is null)
        {
            LastHealthCheck = await _fallbackRuntime.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            _lastHealthCheckedRuntime = null;
            return LastHealthCheck;
        }

        LastHealthCheck = await _candidateRuntime.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
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
        return true;
    }

    public void RevertToFallback()
    {
        _activeRuntime = _fallbackRuntime;
    }

    public bool RevertToLastKnownGood()
    {
        if (_lastKnownGoodRuntime is null)
        {
            return false;
        }

        _activeRuntime = _lastKnownGoodRuntime;
        return true;
    }
}
