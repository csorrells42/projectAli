namespace Ali.Modules.UserMemory;

/// <summary>
/// Narrow worker boundary for participant memory. Keeping orchestration behind this
/// contract lets authority, timeout, reconciliation, and stale-generation behavior be
/// verified without starting provider processes.
/// </summary>
internal interface IParticipantMemoryTransport : IAsyncDisposable
{
    string DataRoot { get; }

    ValueTask<Mem0EmbeddingSpaceConfiguration> ResolveCurrentEmbeddingSpaceAsync(
        CancellationToken cancellationToken);

    Task<Mem0Response> SendAsync(object request, CancellationToken cancellationToken);
}
