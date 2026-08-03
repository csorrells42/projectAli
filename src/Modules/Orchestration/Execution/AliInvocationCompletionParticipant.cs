namespace Ali.Modules.Orchestration.Execution;

/// <summary>
/// Optional exact invocation-lifecycle participant used by a domain adapter that must finish
/// or abandon staged work only after the inner AIFunction returns. Registration is permitted
/// only after the current exact grant has been consumed.
/// </summary>
internal interface IAliInvocationCompletionParticipant
{
    ValueTask CompleteAsync(object? result, CancellationToken cancellationToken);

    ValueTask FailAsync(Exception exception, CancellationToken cancellationToken);

    ValueTask MarkInDoubtAsync(string reasonCode, CancellationToken cancellationToken);
}
