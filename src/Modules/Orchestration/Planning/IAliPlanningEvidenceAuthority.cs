namespace Ali.Modules.Orchestration.Planning;

/// <summary>
/// Resolves exact accepted evidence for validation and for the explicitly bounded completion
/// dossier without retaining an unbounded in-memory history.
/// </summary>
internal interface IAliPlanningEvidenceAuthority
{
    Task<IReadOnlyDictionary<string, AcceptedEvidenceProjection>> ResolveEvidenceAsync(
        IReadOnlyCollection<string> evidenceIds,
        CancellationToken cancellationToken);
}
