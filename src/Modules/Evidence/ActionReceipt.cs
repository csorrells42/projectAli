namespace Ali.Modules.Evidence;

public sealed record ActionReceipt(
    string Id,
    string ActionType,
    EvidenceStatus EvidenceStatus,
    bool Succeeded,
    string Summary,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int? ExitCode = null,
    string? StandardOutput = null,
    string? StandardError = null)
{
    public static ActionReceipt Success(
        string actionType,
        string summary,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        string? standardOutput = null) =>
        new(
            Id: NewId(),
            ActionType: actionType,
            EvidenceStatus: EvidenceStatus.Verified,
            Succeeded: true,
            Summary: summary,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            ExitCode: 0,
            StandardOutput: standardOutput);

    public static ActionReceipt Failure(
        string actionType,
        string summary,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        int? exitCode = null,
        string? standardError = null) =>
        new(
            Id: NewId(),
            ActionType: actionType,
            EvidenceStatus: EvidenceStatus.Verified,
            Succeeded: false,
            Summary: summary,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            ExitCode: exitCode,
            StandardError: standardError);

    private static string NewId() => $"receipt_{Guid.NewGuid():N}";
}
