using Ali.Core.Evidence;
using Ali.Core.Models;
using Ali.Core.Runtime;

namespace Ali.Infrastructure.Runtime;

public sealed class DevelopmentLocalModelRuntime : ILocalModelRuntime
{
    public ModelProfile ActiveProfile { get; } = ModelProfile.UnconfiguredFactorySafe();

    public async IAsyncEnumerable<ModelToken> StreamChatAsync(
        ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chunks = new[]
        {
            "Unknown: no validated local model runtime is configured yet. ",
            "This bootstrap runtime is local and deterministic, so it can prove the WPF chat loop, cancellation, and correction queue without pretending to be a real model. ",
            "Next engineering step: connect an approved local OpenAI-compatible endpoint, run a health check, and activate only after the receipt is verified."
        };

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(90, cancellationToken).ConfigureAwait(false);
            yield return new ModelToken(chunk, EvidenceStatus.Unknown);
        }
    }

    public async Task<RuntimeHealthCheck> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        return new RuntimeHealthCheck(
            Succeeded: true,
            Summary: "Development stub is available. No real local model is configured.",
            CheckedAt: DateTimeOffset.UtcNow,
            Elapsed: DateTimeOffset.UtcNow - started);
    }
}
